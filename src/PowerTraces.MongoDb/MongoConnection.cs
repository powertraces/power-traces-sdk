using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace PowerTraces.MongoDb;

/// <summary>Resolves an <see cref="IMongoDatabase"/> from a connection string, shared by the
/// exporter's write side and the dashboard's read side. <see cref="MongoClient"/> instances are
/// meant to be reused for their whole lifetime (they own an internal connection pool) rather than
/// constructed per call, so one is cached per distinct connection string.</summary>
public static class MongoConnection
{
    private static readonly ConcurrentDictionary<string, MongoClient> Clients = new();

    static MongoConnection()
    {
        // The driver refuses to serialize a bare Guid unless a representation is picked
        // explicitly (it no longer has an implicit legacy default) — pin it to Standard (BSON
        // binary subtype 4, RFC 4122) once, globally, before any document is read/written, rather
        // than annotating every single Guid/Guid? property across ResourceDocument/SpanDocument.
        BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
    }

    /// <summary>The database name comes from the connection string itself (e.g.
    /// <c>mongodb://localhost:27017/mydb</c>), the same way the SQL Server/PostgreSQL providers
    /// take their database from theirs — <see cref="PowerTracesExporterOptions.Schema"/> only prefixes
    /// collection names within it.</summary>
    public static IMongoDatabase GetDatabase(string connectionString)
    {
        var url = MongoUrl.Create(connectionString);
        if (string.IsNullOrWhiteSpace(url.DatabaseName))
        {
            throw new InvalidOperationException(
                "The MongoDB connection string must include a database name, e.g. " +
                "'mongodb://localhost:27017/mydb'.");
        }

        var client = Clients.GetOrAdd(connectionString, static cs => new MongoClient(MongoUrl.Create(cs)));
        return client.GetDatabase(url.DatabaseName);
    }
}
