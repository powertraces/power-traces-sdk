using System.Security.Cryptography;
using System.Text;

namespace PowerTraces.MongoDb;

/// <summary>Computes a stable hash of a Resource's AttributesJson, stored as
/// <see cref="ResourceDocument.AttributesHash"/> and indexed so resource dedup can narrow down to
/// candidate documents before comparing the full JSON — the Mongo equivalent of SQL Server's
/// persisted CHECKSUM(AttributesJson) column.</summary>
internal static class AttributesHasher
{
    public static string Compute(string attributesJson) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(attributesJson)));
}
