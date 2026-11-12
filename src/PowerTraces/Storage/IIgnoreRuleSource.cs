using PowerTraces.Dashboard.Query;

namespace PowerTraces.Storage;

/// <summary>Implemented by an <see cref="IStorageWriter"/> that can read the persisted ignore rules
/// for its database. The trace exporter checks every exported span against them and drops matches
/// before they're written — every built-in provider implements it, so rules created from the
/// dashboard apply no matter which database is in use. Implementations should serve reads from
/// <see cref="IgnoreRuleCache"/> rather than hitting the database for every export.</summary>
public interface IIgnoreRuleSource
{
    Task<IReadOnlyList<IgnoreRuleDto>> ListIgnoreRulesAsync(CancellationToken ct);
}
