using System.Text.Json.Serialization;
using PowerTraces.Search;

namespace PowerTraces.Dashboard.Query;

/// <summary>How a <see cref="FieldFilter"/> compares. Serialized by name ("Equals", "NotEquals",
/// "HasWord") whatever the host's JSON settings.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FieldMatch>))]
public enum FieldMatch
{
    /// <summary>The field's whole value equals the filter value (case-insensitive).</summary>
    Equals,

    /// <summary>No span of the trace has the field with this value.</summary>
    NotEquals,

    /// <summary>The trace has the field, and a word starting with each word of the filter value
    /// (the search index keeps words per trace, not per field).</summary>
    HasWord,
}

/// <summary>One field constraint. <see cref="Key"/> is an attribute key, an event attribute key,
/// <c>span.name</c>, or the path of a value inside a JSON attribute
/// (<c>http.request.body.customer.email</c>).</summary>
public sealed record FieldFilter(string Key, string Value, FieldMatch Match = FieldMatch.Equals);

/// <summary>A trace search. Every part is optional and they combine with AND; a trace matches a
/// field or text condition when any of its spans does.
///
/// <see cref="Text"/> is free text: words (a trace must contain each, matched by prefix from three
/// characters), <c>"quoted phrases"</c> (each word required), <c>-word</c> (must not contain),
/// <c>key=value</c> (a field filter) and a 32-character trace id (that trace).</summary>
public sealed record TraceSearchQuery
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? Text { get; init; }

    /// <summary>Root operations (a trace's root span name) — any of them.</summary>
    public IReadOnlyList<string>? Endpoints { get; init; }

    /// <summary>Services — a trace matches when any of its spans came from one of them.</summary>
    public IReadOnlyList<string>? Services { get; init; }

    public bool? HasError { get; init; }
    public long? MinDurationMs { get; init; }
    public long? MaxDurationMs { get; init; }
    public IReadOnlyList<FieldFilter>? Filters { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>Why a trace is in the results: the first field whose value matched the search, with
/// the matched part's position in <see cref="Value"/> (which may be an excerpt of a long value).</summary>
public sealed record TraceMatchDto(string Key, string Value, int HighlightStart, int HighlightLength);

/// <summary>A count per value of one facet.</summary>
public sealed record FacetValueDto(string Value, int Count);

/// <summary>Breakdown of a search's matches, for the filter side panel. Counts cover the newest
/// <see cref="TraceFacets.SampleSize"/> matches; <see cref="Sampled"/> says when there were more.</summary>
public sealed record TraceFacetsDto(IReadOnlyList<FacetValueDto> Endpoints, int ErrorCount, int OkCount, bool Sampled);

public static class TraceFacets
{
    public const int SampleSize = 20_000;
    public const int MaxEndpoints = 50;
}

/// <summary>One index lookup: a term, exactly or as a prefix.</summary>
public sealed record TermLookup(string Term, bool Prefix);

/// <summary>A <see cref="TraceSearchQuery"/>'s text and field filters turned into index lookups.
/// Each entry of <see cref="Required"/> is a group of lookups of which a trace must match at least
/// one; a trace must match every group, and none of <see cref="Excluded"/>.</summary>
public sealed class TraceSearchPlan
{
    public List<IReadOnlyList<TermLookup>> Required { get; } = [];
    public List<TermLookup> Excluded { get; } = [];

    /// <summary>Set when the text was a trace id: the search is for that trace alone.</summary>
    public string? TraceId { get; private set; }

    /// <summary>Text words, for explaining matches.</summary>
    public List<string> Words { get; } = [];

    /// <summary>Field filters from the query and its text, for explaining matches.</summary>
    public List<FieldFilter> Filters { get; } = [];

    /// <summary>True when the query had text or filters but nothing in them can ever match (e.g.
    /// only one-letter words) — the search returns nothing rather than everything.</summary>
    public bool MatchesNothing { get; private set; }

    public bool UsesIndex => Required.Count > 0 || Excluded.Count > 0;

    public static TraceSearchPlan Build(TraceSearchQuery query)
    {
        var plan = new TraceSearchPlan();
        var text = query.Text?.Trim();
        if (!string.IsNullOrEmpty(text) && text.Length == 32 && text.All(char.IsAsciiHexDigit))
        {
            plan.TraceId = text.ToLowerInvariant();
        }
        else if (!string.IsNullOrEmpty(text))
        {
            plan.AddText(text);
        }

        var hadUnusable = plan.MatchesNothing;
        plan.MatchesNothing = false;

        foreach (var filter in query.Filters ?? [])
        {
            if (!string.IsNullOrWhiteSpace(filter.Key) && !string.IsNullOrEmpty(filter.Value))
            {
                plan.AddFilter(filter with { Key = filter.Key.Trim() });
            }
        }

        // Words too short to look up are dropped; only a search made of nothing else matches nothing.
        plan.MatchesNothing = (hadUnusable || plan.MatchesNothing) && !plan.UsesIndex && plan.TraceId is null;
        return plan;
    }

    private void AddText(string text)
    {
        foreach (var (piece, quoted) in SplitText(text))
        {
            if (!quoted && piece.Length > 1 && piece[0] == '-')
            {
                foreach (var token in SearchTerms.QueryTokens(piece[1..]))
                {
                    Excluded.Add(new TermLookup(SearchTerms.Word(token), false));
                }

                continue;
            }

            var equals = quoted ? -1 : piece.IndexOf('=');
            if (equals > 0 && equals < piece.Length - 1)
            {
                AddFilter(new FieldFilter(piece[..equals], piece[(equals + 1)..]));
                continue;
            }

            var tokens = SearchTerms.QueryTokens(piece);
            if (tokens.Count == 0)
            {
                MatchesNothing = true;
            }

            foreach (var token in tokens)
            {
                Words.Add(token);
                Required.Add([new TermLookup(SearchTerms.Word(token), token.Length >= 3)]);
            }
        }
    }

    private void AddFilter(FieldFilter filter)
    {
        Filters.Add(filter);
        if (filter.Match != FieldMatch.HasWord && filter.Value.Length <= SearchTerms.MaxValueLength)
        {
            var lookup = new TermLookup(SearchTerms.Attribute(filter.Key, filter.Value), false);
            if (filter.Match == FieldMatch.NotEquals)
            {
                Excluded.Add(lookup);
            }
            else
            {
                Required.Add([lookup]);
            }

            return;
        }

        if (filter.Match == FieldMatch.NotEquals)
        {
            // Too long to be indexed whole, so it can't be excluded exactly — and excluding by its
            // words would drop traces that merely share a word. Ignore rather than guess.
            return;
        }

        // A word filter, or a value too long to be indexed whole: the trace must have the field,
        // and every word — in that field or, as the index keeps words per trace, another one. The
        // match explanation then shows where the words are.
        var tokens = SearchTerms.QueryTokens(filter.Value);
        if (tokens.Count == 0)
        {
            MatchesNothing = true;
        }

        Required.Add(
        [
            new TermLookup(SearchTerms.Attribute(filter.Key, ""), true),
            new TermLookup(SearchTerms.FieldKey(filter.Key), false),
        ]);
        foreach (var token in tokens)
        {
            Required.Add([new TermLookup(SearchTerms.Word(token), token.Length >= 3)]);
        }
    }

    /// <summary>Splits on whitespace, keeping "quoted phrases" together.</summary>
    private static IEnumerable<(string Piece, bool Quoted)> SplitText(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                yield break;
            }

            if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                end = end < 0 ? text.Length : end;
                yield return (text[(i + 1)..end], true);
                i = end + 1;
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            yield return (text[start..i], false);
        }
    }
}

/// <summary>Finds the field that explains a match, from a trace's spans.</summary>
public static class TraceMatchFinder
{
    private const int ExcerptLength = 160;

    public static TraceMatchDto? Find(TraceSearchPlan plan, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var all = fields as IReadOnlyList<KeyValuePair<string, string>> ?? fields.ToList();
        foreach (var word in plan.Words)
        {
            foreach (var (key, value) in all)
            {
                var at = value.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    return Excerpt(key, value, at, word.Length);
                }
            }
        }

        foreach (var filter in plan.Filters)
        {
            if (filter.Match == FieldMatch.NotEquals)
            {
                continue;
            }

            var needle = filter.Match == FieldMatch.HasWord
                ? SearchTerms.QueryTokens(filter.Value).FirstOrDefault() ?? filter.Value
                : filter.Value;
            foreach (var (key, value) in all)
            {
                if (!string.Equals(key, filter.Key, StringComparison.Ordinal))
                {
                    continue;
                }

                var at = value.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    return Excerpt(key, value, at, needle.Length);
                }
            }
        }

        return null;
    }

    private static TraceMatchDto Excerpt(string key, string value, int at, int length)
    {
        if (value.Length <= ExcerptLength)
        {
            return new TraceMatchDto(key, value, at, length);
        }

        var start = Math.Max(0, at - 50);
        var end = Math.Min(value.Length, start + ExcerptLength);
        var excerpt = (start > 0 ? "…" : "") + value[start..end] + (end < value.Length ? "…" : "");
        return new TraceMatchDto(key, excerpt, at - start + (start > 0 ? 1 : 0), Math.Min(length, end - at));
    }
}
