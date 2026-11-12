using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PowerTraces.MySQL;

/// <summary>How MySQL/MariaDB keep the trace search index: each trace's search terms (see
/// <c>PowerTraces.Search.SearchTerms</c>) as space-separated tokens in the trace summary's
/// <see cref="Column"/>, under an InnoDB FULLTEXT index — compressed posting lists instead of one
/// row per term. InnoDB's parser splits words on anything but letters, digits and <c>_</c>, so
/// every other character of a term is escaped as <c>_xx</c> (or <c>_uxxxx</c>) and a term reaches
/// the index whole. A token longer than InnoDB's 84-character limit keeps its <c>a:key=</c> part
/// and replaces the value with a hash (<c>_h…</c>): still found exactly, and by field, but not by
/// a prefix of its value.</summary>
public static class MySqlSearchText
{
    public const string Column = "SearchText";

    /// <summary>InnoDB's default <c>innodb_ft_max_token_size</c>; longer tokens aren't indexed.</summary>
    public const int MaxTokenLength = 84;

    /// <summary>A trace's search text stops growing past this many characters.</summary>
    public const int MaxLength = 200_000;

    private const int HashLength = 20;

    public static string Encode(string term)
    {
        var token = Escape(term);
        if (token.Length <= MaxTokenLength)
        {
            return token;
        }

        var equals = term.StartsWith("a:", StringComparison.Ordinal) ? term.IndexOf('=') : -1;
        if (equals > 0)
        {
            var head = Escape(term[..(equals + 1)]);
            if (head.Length + 2 + HashLength <= MaxTokenLength)
            {
                return head + "_h" + Hash(term[(equals + 1)..]);
            }
        }

        return "h_h" + Hash(term);
    }

    /// <summary>The token prefix that finds terms starting with <paramref name="prefix"/> — cut to
    /// the token limit, past which nothing longer was indexed unhashed anyway.</summary>
    public static string EncodePrefix(string prefix)
    {
        var token = Escape(prefix);
        return token.Length <= MaxTokenLength ? token : token[..MaxTokenLength];
    }

    public static string Join(IEnumerable<string> terms) => string.Join(' ', terms.Select(Encode));

    /// <summary>The term a token encodes, or null for a hashed token.</summary>
    public static string? Decode(string token)
    {
        var text = new StringBuilder(token.Length);
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != '_')
            {
                text.Append(token[i]);
                continue;
            }

            if (i + 1 < token.Length && token[i + 1] == 'h')
            {
                return null;
            }

            if (i + 1 < token.Length && token[i + 1] == 'u' && i + 5 < token.Length)
            {
                text.Append((char)int.Parse(token.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 5;
            }
            else if (i + 2 < token.Length)
            {
                text.Append((char)int.Parse(token.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 2;
            }
        }

        return text.ToString();
    }

    private static string Escape(string term)
    {
        var text = new StringBuilder(term.Length + 8);
        foreach (var c in term)
        {
            if (c != '_' && char.IsLetterOrDigit(c))
            {
                text.Append(c);
            }
            else if (c <= 0xff)
            {
                text.Append('_').Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append("_u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..HashLength];
}
