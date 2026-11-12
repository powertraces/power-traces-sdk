using MongoDB.Bson.Serialization.Attributes;

namespace PowerTraces.MongoDb;

/// <summary>BSON-mapped shapes for the library's three MongoDB collections (plus the auxiliary
/// Counters collection), shared by the exporter's write side and the dashboard's read side so both
/// agree on field names/types without either depending on the other's internals. Every field gets
/// an explicit <see cref="BsonElementAttribute"/> so the stored documents use the same PascalCase
/// names as the SQL Server/PostgreSQL columns — the driver's default camelCase convention never
/// leaks in, which keeps cross-provider debugging easy.</summary>

/// <summary>Mirrors the SQL Server Resources table. <see cref="AttributesHash"/> is computed
/// client-side (see AttributesHasher) since Mongo has no equivalent of SQL Server's persisted
/// CHECKSUM computed column — it exists purely as an indexed pre-filter for resource dedup.</summary>
public sealed class ResourceDocument
{
    [BsonId]
    public Guid Id { get; set; }

    [BsonElement("ServiceName")]
    public string? ServiceName { get; set; }

    [BsonElement("AttributesJson")]
    public string AttributesJson { get; set; } = "{}";

    [BsonElement("AttributesHash")]
    public string AttributesHash { get; set; } = "";

    [BsonElement("SchemaUrl")]
    public string? SchemaUrl { get; set; }

    [BsonElement("FirstSeenAt")]
    public DateTime FirstSeenAt { get; set; }

    [BsonElement("LastSeenAt")]
    public DateTime LastSeenAt { get; set; }
}

/// <summary>Mirrors the SQL Server Spans table.</summary>
public sealed class SpanDocument
{
    [BsonId]
    public Guid Id { get; set; }

    [BsonElement("TraceId")]
    public string TraceId { get; set; } = "";

    [BsonElement("SpanId")]
    public string SpanId { get; set; } = "";

    [BsonElement("ParentSpanId")]
    public string ParentSpanId { get; set; } = "";

    [BsonElement("ResourceId")]
    public Guid? ResourceId { get; set; }

    [BsonElement("Name")]
    public string Name { get; set; } = "";

    [BsonElement("Kind")]
    public string? Kind { get; set; }

    [BsonElement("StartTime")]
    public DateTime StartTime { get; set; }

    [BsonElement("EndTime")]
    public DateTime? EndTime { get; set; }

    [BsonElement("Status")]
    public string? Status { get; set; }

    [BsonElement("StatusMessage")]
    public string? StatusMessage { get; set; }

    [BsonElement("HasError")]
    public bool HasError { get; set; }

    [BsonElement("HttpStatus")]
    public string? HttpStatus { get; set; }

    [BsonElement("AttributesJson")]
    public string AttributesJson { get; set; } = "{}";

    [BsonElement("ScopeLibraryName")]
    public string? ScopeLibraryName { get; set; }

    [BsonElement("ScopeLibraryVersion")]
    public string? ScopeLibraryVersion { get; set; }

    [BsonElement("EventsJson")]
    public string EventsJson { get; set; } = "[]";

    [BsonElement("LinksJson")]
    public string LinksJson { get; set; } = "[]";
}

/// <summary>Mirrors the SQL Server Logs table. <see cref="Id"/> stays a sequential
/// <see langword="long"/> (allocated via the Counters collection) rather than an ObjectId, because
/// it's shared verbatim with the dashboard SPA through <c>LogListItemDto</c>/<c>LogDetailDto</c>
/// regardless of which provider is active.</summary>
public sealed class LogDocument
{
    [BsonId]
    public long Id { get; set; }

    [BsonElement("ResourceId")]
    public Guid? ResourceId { get; set; }

    [BsonElement("Timestamp")]
    public DateTime Timestamp { get; set; }

    [BsonElement("ObservedTimestamp")]
    public DateTime? ObservedTimestamp { get; set; }

    [BsonElement("TraceId")]
    public string? TraceId { get; set; }

    [BsonElement("SpanId")]
    public string? SpanId { get; set; }

    [BsonElement("SeverityNumber")]
    public int SeverityNumber { get; set; }

    [BsonElement("SeverityText")]
    public string? SeverityText { get; set; }

    [BsonElement("Body")]
    public string? Body { get; set; }

    [BsonElement("AttributesJson")]
    public string AttributesJson { get; set; } = "{}";

    [BsonElement("ScopeLibraryName")]
    public string? ScopeLibraryName { get; set; }

    [BsonElement("ScopeLibraryVersion")]
    public string? ScopeLibraryVersion { get; set; }

    [BsonElement("SchemaUrl")]
    public string? SchemaUrl { get; set; }

    /// <summary>The attributes as (key, text) pairs — see <see cref="PowerTraces.Storage.AttributeText"/>.
    /// Carries a multikey index, so attribute filters are index lookups rather than parsing
    /// <see cref="AttributesJson"/>. Absent on documents written before this field existed until
    /// the one-time backfill in <see cref="MongoSchemaInitializer"/> fills it in.</summary>
    [BsonElement("Attributes")]
    [BsonIgnoreIfNull]
    public List<AttributeEntry>? Attributes { get; set; }
}

/// <summary>One indexed attribute: key and its text value.</summary>
public sealed class AttributeEntry
{
    [BsonElement("K")]
    public string K { get; set; } = "";

    [BsonElement("V")]
    public string V { get; set; } = "";
}

/// <summary>One document per trace, upserted by every span batch (Mongo's counterpart of the SQL
/// providers' Traces table). Besides the summary the trace list shows, it carries the distinct
/// span names, resource ids and attribute pairs seen across all of the trace's spans, so every
/// trace-list filter is a query on this one collection, served by its indexes.</summary>
[BsonIgnoreExtraElements]
public sealed class TraceDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("StartTime")]
    public DateTime StartTime { get; set; }

    [BsonElement("EndTime")]
    public DateTime EndTime { get; set; }

    [BsonElement("HasError")]
    public bool HasError { get; set; }

    [BsonElement("SpanCount")]
    public int SpanCount { get; set; }

    [BsonElement("HasRoot")]
    public bool HasRoot { get; set; }

    [BsonElement("RootName")]
    public string? RootName { get; set; }

    [BsonElement("ServiceName")]
    public string? ServiceName { get; set; }

    [BsonElement("RootAttributesJson")]
    public string? RootAttributesJson { get; set; }

    /// <summary>Written by versions before the search index; no longer maintained.</summary>
    [BsonElement("SpanNames")]
    [BsonIgnoreIfNull]
    public List<string>? SpanNames { get; set; }

    [BsonElement("ResourceIds")]
    public List<Guid>? ResourceIds { get; set; }

    /// <summary>Written by versions before the search index; no longer maintained.</summary>
    [BsonElement("Attributes")]
    [BsonIgnoreIfNull]
    public List<AttributeEntry>? Attributes { get; set; }

    /// <summary>The trace's search terms (see <see cref="PowerTraces.Search.SearchTerms"/>) — this
    /// provider's search index, a multikey index on (Terms, StartTime).</summary>
    [BsonElement("Terms")]
    [BsonIgnoreIfNull]
    public List<string>? Terms { get; set; }
}

/// <summary>A persisted ignore rule — see <c>IIgnoreRuleStore</c>.</summary>
public sealed class IgnoreRuleDocument
{
    [BsonId]
    public Guid Id { get; set; }

    [BsonElement("Kind")]
    public string Kind { get; set; } = "";

    [BsonElement("Name")]
    public string Name { get; set; } = "";

    [BsonElement("CreatedAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>One distinct attribute key per source ("traces"/"logs"); the id is
/// "{source}:{key}" so an upsert needs no extra unique index.</summary>
public sealed class AttributeKeyDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("Key")]
    public string Key { get; set; } = "";

    [BsonElement("Source")]
    public string Source { get; set; } = "";

    [BsonElement("FirstSeenAt")]
    public DateTime FirstSeenAt { get; set; }
}

/// <summary>One dashboard setting (e.g. the retention policy), its value as JSON.</summary>
public sealed class SettingDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("Value")]
    public string Value { get; set; } = "";

    [BsonElement("UpdatedAt")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A trace excluded from retention — see <c>IRetentionStore</c>.</summary>
public sealed class PinnedTraceDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("RootName")]
    public string? RootName { get; set; }

    [BsonElement("ServiceName")]
    public string? ServiceName { get; set; }

    [BsonElement("StartTime")]
    public DateTime? StartTime { get; set; }

    [BsonElement("HasError")]
    public bool HasError { get; set; }

    [BsonElement("PinnedAt")]
    public DateTime PinnedAt { get; set; }
}

/// <summary>One document per named counter, e.g. <c>{ _id: "Logs", Value: 42 }</c> — the classic
/// MongoDB auto-increment pattern, incremented atomically via
/// <c>FindOneAndUpdateAsync</c>/<c>$inc</c>. Needs no index beyond its default <c>_id</c>.</summary>
public sealed class CounterDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("Value")]
    public long Value { get; set; }
}
