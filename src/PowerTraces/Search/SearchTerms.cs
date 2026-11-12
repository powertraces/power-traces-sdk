using System.Text.Json;
using PowerTraces.Storage;

namespace PowerTraces.Search;

/// <summary>The search terms one batch contributes to one trace — written to the search index
/// (the <see cref="PowerTracesSchema.SearchTermsTable"/> table, or the trace document's term array
/// on MongoDB) next to the spans themselves.</summary>
public sealed record TraceTerms(string TraceId, DateTime StartTime, IReadOnlyCollection<string> Terms);

/// <summary>Turns spans into the terms the trace search index stores, and search input into the
/// terms to look up — both sides through the same normalization, so they always agree.
///
/// A trace is indexed under three kinds of term, each lowercase:
/// <list type="bullet">
/// <item><c>a:key=value</c> — a field and its whole value, for exact filters (values up to
/// <see cref="MaxValueLength"/> characters);</item>
/// <item><c>k:key</c> — a field whose value is too long for an <c>a:</c> term, so a filter can
/// still tell the field is there;</item>
/// <item><c>w:word</c> — a word anywhere in the trace, for free-text search and "field has word"
/// filters (the word, and the field, both in the trace).</item>
/// </list>
/// Fields are span attributes, event names and attributes, span names (<c>span.name</c>) and
/// status messages (<c>span.status_message</c>). A string attribute holding JSON — a request or
/// response body — is unpacked into one field per value, keyed by path:
/// <c>http.request.body.customer.email</c>, <c>http.request.body.lines[].sku</c>.
///
/// Words are runs of letters and digits; "compound" tokens such as <c>sara@acme.io</c>,
/// <c>SKU-4471</c> or <c>api/orders</c> are indexed whole as well as word by word, so searching for
/// either finds the trace. Lookups of three characters or more match by prefix.</summary>
public static class SearchTerms
{
    public const string AttributePrefix = "a:";
    public const string FieldKeyPrefix = "k:";
    public const string WordPrefix = "w:";

    /// <summary>Longest stored term (the index column's width is a little more).</summary>
    public const int MaxTermLength = 200;

    /// <summary>Longest field value indexed as a whole (<c>a:</c>); longer values are found by
    /// their words.</summary>
    public const int MaxValueLength = 100;

    /// <summary>Longest indexed word; longer words are cut, and still match by prefix.</summary>
    public const int MaxWordLength = 48;

    /// <summary>Cap on distinct terms per trace per batch, so one enormous body can't flood the
    /// index. Whole-value terms are added before words, so they're the last to be dropped.</summary>
    public const int MaxTermsPerTrace = 2_000;

    /// <summary>Words of a single field value beyond this are not indexed.</summary>
    public const int MaxWordsPerValue = 200;

    /// <summary>Longest field key; matches the attribute picker's key column.</summary>
    public const int MaxKeyLength = AttributeText.MaxKeyLength;

    public const string SpanNameKey = "span.name";
    public const string StatusMessageKey = "span.status_message";
    public const string EventNameKey = "event.name";

    private const int MaxJsonDepth = 6;
    private const int MaxJsonFieldsPerAttribute = 200;
    private const int MaxJsonLength = 1_000_000;

    // Characters that end a token. Everything else (letters, digits, and joiners such as . @ - /
    // _ : +) stays inside it, so emails, ids, paths and decimals survive as one compound token.
    private const string Separators = "\"'`{}[](),;<>=&?|\\*!^~";
    private const string TrimmedFromTokenEnds = ".,:;-/_+#@";

    // ------------------------------------------------------------------ term shapes

    public static string Attribute(string key, string value) =>
        Clip(AttributePrefix + Clean(Key(key)) + "=" + Clean(value.ToLowerInvariant()));

    public static string FieldKey(string key) => Clip(FieldKeyPrefix + Clean(Key(key)));

    public static string Word(string word) => WordPrefix + word;

    /// <summary>The exclusive upper bound of a prefix range starting at <paramref name="prefix"/>
    /// under a binary (code-point) collation: every term that starts with the prefix sorts at or
    /// after it and before this.</summary>
    public static string PrefixUpperBound(string prefix) =>
        prefix[..^1] + (char)(prefix[^1] + 1);

    // ------------------------------------------------------------------ write side

    /// <summary>Groups a batch's spans by trace and collects each trace's terms. The start time is
    /// the earliest span start in the batch — never before the trace's own start, which is what
    /// lets a time-range search bound its index range from below.</summary>
    public static IReadOnlyList<TraceTerms> FromBatch(IReadOnlyList<SpanRow> rows)
    {
        var byTrace = new Dictionary<string, (DateTime Start, TermSet Terms)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!byTrace.TryGetValue(row.TraceId, out var entry))
            {
                entry = (row.StartTime, new TermSet(MaxTermsPerTrace));
            }

            entry.Terms.AddFields(Fields(row.Name, row.StatusMessage, row.AttributesJson, row.EventsJson));
            byTrace[row.TraceId] = (row.StartTime < entry.Start ? row.StartTime : entry.Start, entry.Terms);
        }

        var result = new List<TraceTerms>(byTrace.Count);
        foreach (var (traceId, (start, terms)) in byTrace)
        {
            result.Add(new TraceTerms(traceId, start, terms.ToList()));
        }

        // Same order as the trace summaries, so concurrent writers take locks in the same order.
        result.Sort((a, b) => string.CompareOrdinal(a.TraceId, b.TraceId));
        return result;
    }

    /// <summary>The terms of one trace's spans — what a backfill of existing data indexes.</summary>
    public static IReadOnlyCollection<string> ForSpans(IEnumerable<(string Name, string? StatusMessage, string? AttributesJson, string? EventsJson)> spans)
    {
        var terms = new TermSet(MaxTermsPerTrace);
        foreach (var span in spans)
        {
            terms.AddFields(Fields(span.Name, span.StatusMessage, span.AttributesJson, span.EventsJson));
        }

        return terms.ToList();
    }

    /// <summary>Every searchable field of a span, as (key, text) pairs — JSON bodies unpacked into
    /// one pair per value. Also what the dashboard reads to explain why a trace matched, and the
    /// keys it records for the attribute picker.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Fields(string? name, string? statusMessage, string? attributesJson, string? eventsJson)
    {
        if (!string.IsNullOrEmpty(name))
        {
            yield return new(SpanNameKey, name);
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            yield return new(StatusMessageKey, statusMessage);
        }

        foreach (var field in ObjectFields(StoredJson.Unpack(attributesJson)))
        {
            yield return field;
        }

        foreach (var field in EventFields(StoredJson.Unpack(eventsJson)))
        {
            yield return field;
        }
    }

    /// <summary>The keys <see cref="Fields"/> produces for a batch — attribute keys plus the paths
    /// unpacked from JSON values — for the attribute picker.</summary>
    public static IEnumerable<string> FieldKeys(IEnumerable<SpanRow> rows) =>
        rows.SelectMany(r => Fields(null, null, r.AttributesJson, r.EventsJson)).Select(f => f.Key).Distinct(StringComparer.Ordinal);

    private static IEnumerable<KeyValuePair<string, string>> ObjectFields(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}")
        {
            return [];
        }

        var fields = new List<KeyValuePair<string, string>>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    var budget = MaxJsonFieldsPerAttribute;
                    Collect(Key(property.Name), property.Value, 0, ref budget, fields);
                }
            }
        }
        catch (JsonException)
        {
        }

        return fields;
    }

    private static IEnumerable<KeyValuePair<string, string>> EventFields(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]")
        {
            return [];
        }

        var fields = new List<KeyValuePair<string, string>>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (e.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text)
                {
                    fields.Add(new(EventNameKey, text));
                }

                if (e.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in attributes.EnumerateObject())
                    {
                        var budget = MaxJsonFieldsPerAttribute;
                        Collect(Key(property.Name), property.Value, 0, ref budget, fields);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return fields;
    }

    /// <summary>Walks one value: scalars become a field; objects descend by member name (only
    /// names that look like real field names — a dictionary keyed by ids would otherwise mint a
    /// new key per id); arrays descend with <c>[]</c> for their objects and keep the key for their
    /// scalars; a string that holds JSON is parsed and walked the same way.</summary>
    private static void Collect(string key, JsonElement value, int depth, ref int budget, List<KeyValuePair<string, string>> fields)
    {
        if (budget <= 0)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? "";
                if (depth < MaxJsonDepth && LooksLikeJson(text))
                {
                    try
                    {
                        using var nested = JsonDocument.Parse(text);
                        if (nested.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            Collect(key, nested.RootElement, depth + 1, ref budget, fields);
                            return;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                if (text.Length > 0)
                {
                    fields.Add(new(key, text));
                    budget--;
                }

                return;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                fields.Add(new(key, value.GetRawText()));
                budget--;
                return;

            case JsonValueKind.Object:
                if (depth >= MaxJsonDepth)
                {
                    return;
                }

                foreach (var property in value.EnumerateObject())
                {
                    var childKey = LooksLikeFieldName(property.Name) ? Key(key + "." + property.Name) : key;
                    Collect(childKey, property.Value, depth + 1, ref budget, fields);
                    if (budget <= 0)
                    {
                        return;
                    }
                }

                return;

            case JsonValueKind.Array:
                if (depth >= MaxJsonDepth)
                {
                    return;
                }

                foreach (var item in value.EnumerateArray())
                {
                    var childKey = item.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? Key(key + "[]") : key;
                    Collect(childKey, item, depth + 1, ref budget, fields);
                    if (budget <= 0)
                    {
                        return;
                    }
                }

                return;
        }
    }

    private static bool LooksLikeJson(string text)
    {
        if (text.Length < 2 || text.Length > MaxJsonLength)
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        return span.Length >= 2 && ((span[0] == '{' && span[^1] == '}') || (span[0] == '[' && span[^1] == ']'));
    }

    /// <summary>A name like <c>orderId</c> or <c>line_items</c>, not an id or a hash used as a
    /// dictionary key.</summary>
    private static bool LooksLikeFieldName(string name)
    {
        if (name.Length is 0 or > 40 || !(char.IsLetter(name[0]) || name[0] is '_' or '$' or '@'))
        {
            return false;
        }

        var hexLike = name.Length >= 16;
        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '$' or '@'))
            {
                return false;
            }

            hexLike &= char.IsAsciiHexDigit(c) || c == '-';
        }

        return !hexLike;
    }

    // ------------------------------------------------------------------ tokens

    /// <summary>The lowercase tokens of <paramref name="text"/>: each word (letters and digits,
    /// two characters or more) and each compound token (three or more, e.g. <c>sara@acme.io</c>).</summary>
    public static List<string> Tokenize(string text, int max = MaxWordsPerValue)
    {
        var tokens = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var compound in Compounds(text.ToLowerInvariant()))
        {
            if (IsWord(compound))
            {
                Add(compound, 2);
                continue;
            }

            Add(compound, 3);
            var start = -1;
            for (var i = 0; i <= compound.Length; i++)
            {
                var isWordChar = i < compound.Length && char.IsLetterOrDigit(compound[i]);
                if (isWordChar && start < 0)
                {
                    start = i;
                }
                else if (!isWordChar && start >= 0)
                {
                    Add(compound[start..i], 2);
                    start = -1;
                }
            }

            if (tokens.Count >= max)
            {
                break;
            }
        }

        return tokens;

        void Add(string token, int minLength)
        {
            if (token.Length < minLength || tokens.Count >= max)
            {
                return;
            }

            token = token.Length > MaxWordLength ? token[..MaxWordLength] : token;
            if (seen.Add(token))
            {
                tokens.Add(token);
            }
        }
    }

    /// <summary>Search input split the way stored text is: each piece is either one word or one
    /// compound token, lowercase. A search for <c>sara@acme.io</c> looks up the compound itself.</summary>
    public static List<string> QueryTokens(string text)
    {
        var tokens = new List<string>();
        foreach (var compound in Compounds(text.ToLowerInvariant()))
        {
            var minLength = IsWord(compound) ? 2 : 3;
            if (compound.Length >= minLength)
            {
                tokens.Add(compound.Length > MaxWordLength ? compound[..MaxWordLength] : compound);
            }
        }

        return tokens;
    }

    private static IEnumerable<string> Compounds(string lower)
    {
        var start = -1;
        for (var i = 0; i <= lower.Length; i++)
        {
            var separator = i == lower.Length || char.IsWhiteSpace(lower[i]) || Separators.Contains(lower[i]);
            if (!separator && start < 0)
            {
                start = i;
            }
            else if (separator && start >= 0)
            {
                var token = lower[start..i].Trim(TrimmedFromTokenEnds.ToCharArray());
                if (token.Length > 0)
                {
                    yield return token;
                }

                start = -1;
            }
        }
    }

    private static bool IsWord(string token)
    {
        foreach (var c in token)
        {
            if (!char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Key(string key) => AttributeText.Truncate(key, MaxKeyLength);

    private static string Clip(string term) => term.Length <= MaxTermLength ? term : term[..MaxTermLength];

    /// <summary>Control characters become spaces, so a term never contains one — providers may use
    /// them as separators when passing a trace's terms as one string.</summary>
    private static string Clean(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                return string.Create(text.Length, text, static (span, source) =>
                {
                    for (var i = 0; i < source.Length; i++)
                    {
                        span[i] = char.IsControl(source[i]) ? ' ' : source[i];
                    }
                });
            }
        }

        return text;
    }

    /// <summary>A trace's terms, deduplicated and capped.</summary>
    private sealed class TermSet(int capacity)
    {
        private readonly HashSet<string> _terms = new(StringComparer.Ordinal);

        public void AddFields(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var values = new List<string>();
            foreach (var (key, value) in fields)
            {
                Add(value.Length <= MaxValueLength ? Attribute(key, value) : FieldKey(key));
                values.Add(value);
            }

            // Words after every whole value, so the cap drops words first.
            foreach (var value in values)
            {
                foreach (var token in Tokenize(value))
                {
                    Add(Word(token));
                }
            }
        }

        public List<string> ToList() => [.. _terms];

        private void Add(string term)
        {
            if (_terms.Count < capacity)
            {
                _terms.Add(term);
            }
        }
    }
}
