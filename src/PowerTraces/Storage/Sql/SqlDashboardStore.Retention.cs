using System.Data.Common;
using System.Text;
using System.Text.Json;
using PowerTraces.Dashboard.Query;

namespace PowerTraces.Storage.Sql;

/// <summary>Retention for every SQL provider: the policy and the last run live as JSON rows in
/// the <see cref="PowerTracesSchema.SettingsTable"/> key/value table, pinned traces in
/// <see cref="PowerTracesSchema.PinnedTracesTable"/>. A sweep finds expired rows through the
/// time indexes a batch at a time and deletes them by id, so it never holds a long transaction
/// or lock on tables the exporter is writing to.</summary>
public abstract partial class SqlDashboardStore : IRetentionStore
{
    private const string RetentionSettingsKey = "retention";
    private const string RetentionLastRunKey = "retention.lastRun";

    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web);

    private string Settings => dialect.Table(options.Schema, PowerTracesSchema.SettingsTable);
    private string PinnedTraces => dialect.Table(options.Schema, PowerTracesSchema.PinnedTracesTable);

    public async Task<RetentionStateDto> GetRetentionAsync(CancellationToken ct)
    {
        var (settingsJson, updatedAt) = await ReadSettingAsync(RetentionSettingsKey, ct).ConfigureAwait(false);
        var (lastRunJson, _) = await ReadSettingAsync(RetentionLastRunKey, ct).ConfigureAwait(false);
        return new RetentionStateDto(
            Deserialize<RetentionSettingsDto>(settingsJson) ?? RetentionSettingsDto.Disabled,
            updatedAt,
            Deserialize<RetentionRunDto>(lastRunJson));
    }

    public Task SaveRetentionSettingsAsync(RetentionSettingsDto settings, CancellationToken ct) =>
        WriteSettingAsync(RetentionSettingsKey, JsonSerializer.Serialize(settings, SettingsJson), ct);

    public async Task<IReadOnlyList<PinnedTraceDto>> ListPinnedTracesAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.Sql($"""
            SELECT "TraceId", "RootName", "ServiceName", "StartTime", "HasError", "PinnedAt" FROM {PinnedTraces} ORDER BY "PinnedAt" DESC
            """);

        var pinned = new List<PinnedTraceDto>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            pinned.Add(new PinnedTraceDto(
                reader.GetString(0),
                SqlDialect.ReadStringOrNull(reader, 1),
                SqlDialect.ReadStringOrNull(reader, 2),
                SqlDialect.ReadUtcOrNull(reader, 3),
                SqlDialect.ReadBool(reader, 4),
                SqlDialect.ReadUtc(reader, 5)));
        }

        return pinned;
    }

    public async Task<bool> IsTracePinnedAsync(string traceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.Sql($"""SELECT COUNT(*) FROM {PinnedTraces} WHERE "TraceId" = @TraceId""");
        dialect.AddParameter(cmd, "@TraceId", NormalizeTraceId(traceId), ansi: true);
        return SqlDialect.ReadInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    public async Task PinTraceAsync(PinnedTraceDto trace, CancellationToken ct)
    {
        if (await IsTracePinnedAsync(trace.TraceId, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            // A plain VALUES insert (not INSERT ... SELECT) so PostgreSQL types a null
            // parameter from its target column.
            cmd.CommandText = dialect.Sql($"""
                INSERT INTO {PinnedTraces} ("TraceId", "RootName", "ServiceName", "StartTime", "HasError", "PinnedAt")
                VALUES (@TraceId, @RootName, @ServiceName, @StartTime, @HasError, @PinnedAt)
                """);
            dialect.AddParameter(cmd, "@TraceId", NormalizeTraceId(trace.TraceId), ansi: true);
            dialect.AddParameter(cmd, "@RootName", Truncate(trace.RootName, 400));
            dialect.AddParameter(cmd, "@ServiceName", Truncate(trace.ServiceName, 200));
            dialect.AddParameter(cmd, "@StartTime", trace.StartTime);
            dialect.AddParameter(cmd, "@HasError", trace.HasError);
            dialect.AddParameter(cmd, "@PinnedAt", trace.PinnedAt);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // A primary-key violation means another request pinned the same trace at the same
            // moment — it's pinned either way. Anything else is a real failure.
            if (!await IsTracePinnedAsync(trace.TraceId, ct).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    public async Task UnpinTraceAsync(string traceId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.Sql($"""DELETE FROM {PinnedTraces} WHERE "TraceId" = @TraceId""");
        dialect.AddParameter(cmd, "@TraceId", NormalizeTraceId(traceId), ansi: true);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<RetentionRunDto?> ApplyRetentionAsync(CancellationToken ct)
    {
        var settings = (await GetRetentionAsync(ct).ConfigureAwait(false)).Settings;
        if (!settings.Enabled)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var traceCutoff = Retention.Cutoff(settings.TraceRetentionDays, now);
        var run = await Retention.RunAsync(async () =>
        {
            var traces = await DeleteExpiredTracesAsync(traceCutoff, settings.KeepErrorTraces, ct).ConfigureAwait(false);
            await DeleteOrphanSearchTermsAsync(traceCutoff, ct).ConfigureAwait(false);
            return (traces, await DeleteExpiredLogsAsync(Retention.Cutoff(settings.LogRetentionDays, now), settings.KeepErrorLogs, ct).ConfigureAwait(false));
        }).ConfigureAwait(false);

        await WriteSettingAsync(RetentionLastRunKey, JsonSerializer.Serialize(run, SettingsJson), ct).ConfigureAwait(false);
        return run;
    }

    /// <summary>Trace summaries that started before the cutoff (through the StartTime index),
    /// minus failures when they're kept and pinned traces; each batch's spans and summaries are
    /// then deleted together.</summary>
    private async Task<long> DeleteExpiredTracesAsync(DateTime cutoff, bool keepErrors, CancellationToken ct)
    {
        var where = new StringBuilder($"""t."StartTime" < @Cutoff AND NOT EXISTS (SELECT 1 FROM {PinnedTraces} p WHERE p."TraceId" = t."TraceId")""");
        if (keepErrors)
        {
            where.Append(""" AND t."HasError" = @False""");
        }

        long deleted = 0;
        while (true)
        {
            var ids = new List<string>(Retention.BatchSize);
            await using (var connection = await OpenAsync(ct).ConfigureAwait(false))
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = dialect.Sql(dialect.SelectLimited("""t."TraceId" """, $"FROM {Traces} t WHERE {where}", Retention.BatchSize));
                dialect.AddParameter(cmd, "@Cutoff", cutoff);
                if (keepErrors)
                {
                    dialect.AddParameter(cmd, "@False", false);
                }

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    ids.Add(reader.GetString(0));
                }
            }

            if (ids.Count == 0)
            {
                return deleted;
            }

            await SqlIgnoreRules.DeleteTracesAsync(dialect, options.ConnectionString, options.Schema, ids, ct).ConfigureAwait(false);
            deleted += ids.Count;
            if (ids.Count < Retention.BatchSize)
            {
                return deleted;
            }
        }
    }

    /// <summary>Where a trace's search terms aren't deleted with it (see
    /// <see cref="SqlDialect.SearchTermsIndexedByTrace"/>), deletes the terms older than the cutoff
    /// whose trace is gone, in batches. Terms of traces retention keeps (failed or pinned) stay.</summary>
    private async Task<long> DeleteOrphanSearchTermsAsync(DateTime cutoff, CancellationToken ct)
    {
        if (!dialect.UsesSearchTermsTable || dialect.SearchTermsIndexedByTrace)
        {
            return 0;
        }

        const int batch = 50_000;
        var terms = dialect.Table(options.Schema, PowerTracesSchema.SearchTermsTable);
        long deleted = 0;
        while (true)
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = dialect.Sql(dialect.DeleteLimited(terms, "st",
                $"""st."StartTime" < @Cutoff AND NOT EXISTS (SELECT 1 FROM {Traces} t WHERE t."TraceId" = {dialect.SearchTraceIdText("st")})""", batch));
            dialect.AddParameter(cmd, "@Cutoff", cutoff);
            var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            deleted += n;
            if (n < batch)
            {
                return deleted;
            }
        }
    }

    /// <summary>Log records older than the cutoff (through the Timestamp index), minus errors when
    /// they're kept and the logs of pinned traces.</summary>
    private async Task<long> DeleteExpiredLogsAsync(DateTime cutoff, bool keepErrors, CancellationToken ct)
    {
        var where = new StringBuilder($"""l."Timestamp" < @Cutoff AND (l."TraceId" IS NULL OR NOT EXISTS (SELECT 1 FROM {PinnedTraces} p WHERE p."TraceId" = l."TraceId"))""");
        if (keepErrors)
        {
            where.Append(""" AND l."SeverityNumber" < @ErrorSeverity""");
        }

        long deleted = 0;
        while (true)
        {
            var ids = new List<long>(Retention.BatchSize);
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = dialect.Sql(dialect.SelectLimited("""l."Id" """, $"FROM {Logs} l WHERE {where}", Retention.BatchSize));
                dialect.AddParameter(cmd, "@Cutoff", cutoff);
                if (keepErrors)
                {
                    dialect.AddParameter(cmd, "@ErrorSeverity", Retention.ErrorSeverityNumber);
                }

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    ids.Add(SqlDialect.ReadInt64(reader.GetValue(0)));
                }
            }

            if (ids.Count == 0)
            {
                return deleted;
            }

            await using (var delete = connection.CreateCommand())
            {
                var names = new string[ids.Count];
                for (var i = 0; i < ids.Count; i++)
                {
                    names[i] = $"@Id{i}";
                    dialect.AddParameter(delete, names[i], ids[i]);
                }

                delete.CommandText = dialect.Sql($"""DELETE FROM {Logs} WHERE "Id" IN ({string.Join(", ", names)})""");
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            deleted += ids.Count;
            if (ids.Count < Retention.BatchSize)
            {
                return deleted;
            }
        }
    }

    private async Task<(string? Value, DateTime? UpdatedAt)> ReadSettingAsync(string key, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = dialect.Sql($"""SELECT "Value", "UpdatedAt" FROM {Settings} WHERE "Key" = @Key""");
        dialect.AddParameter(cmd, "@Key", key);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? (SqlDialect.ReadStringOrNull(reader, 0), SqlDialect.ReadUtc(reader, 1))
            : (null, null);
    }

    /// <summary>A portable upsert: delete then insert, in one transaction.</summary>
    private async Task WriteSettingAsync(string key, string value, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = dialect.Sql($"""DELETE FROM {Settings} WHERE "Key" = @Key""");
            dialect.AddParameter(delete, "@Key", key);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = dialect.Sql($"""INSERT INTO {Settings} ("Key", "Value", "UpdatedAt") VALUES (@Key, @Value, @UpdatedAt)""");
            dialect.AddParameter(insert, "@Key", key);
            dialect.AddParameter(insert, "@Value", value);
            dialect.AddParameter(insert, "@UpdatedAt", DateTime.UtcNow);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, SettingsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeTraceId(string traceId) => traceId.Trim().ToLowerInvariant();

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
