namespace PowerTraces.Dashboard.Query;

/// <summary>Shared plumbing for <see cref="IDashboardStore.GetOverviewAsync"/>: sizing the buckets,
/// and assembling a dense bucket list (empty buckets included) from whatever sparse per-bucket rows
/// a provider's grouping query returns.</summary>
public static class DashboardOverview
{
    public const int TopOperationCount = 8;

    public static int BucketSeconds(DateTime from, DateTime to, int bucketCount) =>
        (int)Math.Max(1, Math.Ceiling((to - from).TotalSeconds / Math.Clamp(bucketCount, 1, 500)));

    public static DashboardOverviewDto Assemble(
        DateTime from, DateTime to, int bucketSeconds,
        IEnumerable<(long Bucket, int Traces, int Errors, double AvgDurationMs)> traceRows,
        IEnumerable<(long Bucket, int Logs, int Warnings, int Errors)> logRows,
        IReadOnlyList<OperationStatsDto> topOperations)
    {
        var count = (int)Math.Ceiling((to - from).TotalSeconds / bucketSeconds);
        var traces = new (int Traces, int Errors, double Avg)[count];
        var logs = new (int Logs, int Warnings, int Errors)[count];

        foreach (var row in traceRows)
        {
            if (row.Bucket >= 0 && row.Bucket < count)
            {
                traces[row.Bucket] = (row.Traces, row.Errors, row.AvgDurationMs);
            }
        }

        foreach (var row in logRows)
        {
            if (row.Bucket >= 0 && row.Bucket < count)
            {
                logs[row.Bucket] = (row.Logs, row.Warnings, row.Errors);
            }
        }

        var buckets = new List<TimeBucketDto>(count);
        for (var i = 0; i < count; i++)
        {
            buckets.Add(new TimeBucketDto(
                DateTime.SpecifyKind(from.AddSeconds((double)i * bucketSeconds), DateTimeKind.Utc),
                traces[i].Traces, traces[i].Errors, Math.Round(traces[i].Avg, 1),
                logs[i].Logs, logs[i].Warnings, logs[i].Errors));
        }

        return new DashboardOverviewDto(
            DateTime.SpecifyKind(from, DateTimeKind.Utc), DateTime.SpecifyKind(to, DateTimeKind.Utc),
            bucketSeconds, buckets, topOperations);
    }
}
