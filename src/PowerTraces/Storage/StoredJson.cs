using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace PowerTraces.Storage;

/// <summary>How span attribute and event JSON is kept in the database. Searching goes through the
/// search index (<see cref="Search.SearchTerms"/>), not through the database's JSON functions, so
/// the JSON only has to come back intact when a trace is opened — which lets anything large be
/// stored Brotli-compressed (as base64 text, so the column types don't change). Small values stay
/// plain JSON: compressing them saves nothing.
///
/// Readers must go through <see cref="Unpack"/>; it accepts both forms, so rows written before
/// compression existed read the same way.</summary>
public static class StoredJson
{
    /// <summary>Marks a compressed value. JSON can't start with it, so a plain value is never
    /// mistaken for one.</summary>
    public const string CompressedPrefix = "br1:";

    /// <summary>Values shorter than this are stored as they are.</summary>
    public const int MinCompressLength = 256;

    public static string Pack(string json)
    {
        if (json.Length < MinCompressLength)
        {
            return json;
        }

        var source = Encoding.UTF8.GetBytes(json);
        var buffer = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(source.Length));
        try
        {
            // Quality 5 / 4 MB window: most of Brotli's ratio on JSON at a fraction of the
            // CPU of the maximum quality — this runs on the exporter's background thread.
            if (!BrotliEncoder.TryCompress(source, buffer, out var written, quality: 5, window: 22))
            {
                return json;
            }

            var packed = CompressedPrefix + Convert.ToBase64String(buffer, 0, written);
            return packed.Length < json.Length ? packed : json;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string? Unpack(string? stored)
    {
        if (stored is null || !stored.StartsWith(CompressedPrefix, StringComparison.Ordinal))
        {
            return stored;
        }

        var compressed = Convert.FromBase64String(stored.AsSpan(CompressedPrefix.Length).ToString());
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
