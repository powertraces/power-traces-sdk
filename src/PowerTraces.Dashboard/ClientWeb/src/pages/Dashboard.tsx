import { useQuery } from "@tanstack/react-query"
import { Link } from "react-router-dom"
import { Activity, AlertTriangle, ArrowRight, CircleAlert, Gauge, GitBranch, RefreshCw, ScrollText, Timer } from "lucide-react"
import { getDashboard, getOverview, getTraces, type OverviewRange } from "@/lib/api"
import { StatTile } from "@/components/StatTile"
import { Panel, EmptyState, Loading } from "@/components/Panel"
import { PageHeader } from "@/components/PageHeader"
import { SeverityBadge } from "@/components/SeverityBadge"
import { Segmented } from "@/components/ui"
import { Legend, TimeSeriesChart } from "@/components/charts/TimeSeriesChart"
import { formatCount, formatDuration, formatInterval, formatRelative } from "@/lib/format"
import { useLocalStorageState } from "@/lib/useLocalStorageState"
import { useUrlState } from "@/lib/urlState"

const RANGES: { value: OverviewRange; label: string; buckets: number; minutes: number }[] = [
  { value: "1h", label: "1h", buckets: 60, minutes: 60 },
  { value: "6h", label: "6h", buckets: 72, minutes: 360 },
  { value: "24h", label: "24h", buckets: 96, minutes: 1440 },
  { value: "7d", label: "7d", buckets: 84, minutes: 10080 },
]

export function Dashboard() {
  // `?range=` (shareable) wins; otherwise the viewer's last choice.
  const [storedRange, setStoredRange] = useLocalStorageState<OverviewRange>("powertraces.overview.range", "24h")
  const { params, update } = useUrlState()
  const urlRange = params.get("range") as OverviewRange | null
  const range = urlRange && RANGES.some((r) => r.value === urlRange) ? urlRange : storedRange
  const setRange = (next: OverviewRange) => {
    setStoredRange(next)
    update({ range: next }, { replace: true })
  }
  const rangeInfo = RANGES.find((r) => r.value === range) ?? RANGES[2]

  const overview = useQuery({
    queryKey: ["overview", range],
    queryFn: ({ signal }) => getOverview(range, rangeInfo.buckets, { signal }),
    refetchInterval: 15_000,
  })
  const summary = useQuery({ queryKey: ["dashboard"], queryFn: getDashboard, refetchInterval: 15_000 })
  const failing = useQuery({
    queryKey: ["traces", "recent-errors"],
    queryFn: ({ signal }) => getTraces({ hasError: true, page: 1, pageSize: 6 }, { signal }),
    refetchInterval: 15_000,
  })

  const buckets = overview.data?.buckets ?? []
  const times = buckets.map((b) => b.start)
  const totalTraces = buckets.reduce((s, b) => s + b.traces, 0)
  const totalErrors = buckets.reduce((s, b) => s + b.errorTraces, 0)
  const totalLogs = buckets.reduce((s, b) => s + b.logs, 0)
  const errorLogs = buckets.reduce((s, b) => s + b.errorLogs, 0)
  const errorRate = totalTraces === 0 ? 0 : (100 * totalErrors) / totalTraces
  const avgLatency = totalTraces === 0 ? 0 : buckets.reduce((s, b) => s + b.avgDurationMs * b.traces, 0) / totalTraces
  const perMinute = totalTraces / rangeInfo.minutes
  const loading = overview.isLoading
  const dash = (v: string) => (loading ? "…" : v)

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title="Overview"
        subtitle="Traffic, errors and latency across every instrumented service."
        actions={
          <>
            <span className="hidden items-center gap-1.5 text-xs text-[var(--text-dim)] sm:flex">
              <RefreshCw size={12} className={overview.isFetching ? "animate-spin" : ""} />
              {overview.dataUpdatedAt ? `Updated ${formatRelative(new Date(overview.dataUpdatedAt).toISOString())}` : "Loading"}
            </span>
            <Segmented label="Time range" value={range} onChange={setRange} options={RANGES.map(({ value, label }) => ({ value, label }))} />
          </>
        }
      />

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatTile
          label="Requests"
          icon={Activity}
          value={dash(formatCount(totalTraces))}
          note={`${perMinute >= 10 ? Math.round(perMinute).toLocaleString() : perMinute.toFixed(1)} per minute · last ${rangeInfo.label}`}
          trend={buckets.map((b) => b.traces)}
        />
        <StatTile
          label="Error rate"
          icon={AlertTriangle}
          value={dash(`${errorRate.toFixed(errorRate > 0 && errorRate < 1 ? 2 : 1)}%`)}
          note={`${totalErrors.toLocaleString()} failed trace${totalErrors === 1 ? "" : "s"}`}
          tone={errorRate >= 5 ? "error" : errorRate > 0 ? "warn" : "good"}
          trend={buckets.map((b) => (b.traces ? (100 * b.errorTraces) / b.traces : 0))}
        />
        <StatTile
          label="Avg latency"
          icon={Timer}
          value={dash(formatDuration(avgLatency))}
          note="Mean trace duration"
          trend={buckets.map((b) => b.avgDurationMs)}
        />
        <StatTile
          label="Logs"
          icon={ScrollText}
          value={dash(formatCount(totalLogs))}
          note={`${errorLogs.toLocaleString()} error${errorLogs === 1 ? "" : "s"} · ${buckets.reduce((s, b) => s + b.warningLogs, 0).toLocaleString()} warnings`}
          tone={errorLogs > 0 ? "warn" : "default"}
          trend={buckets.map((b) => b.logs)}
        />
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
        <Panel
          title="Throughput"
          subtitle={`Traces started per ${formatInterval(overview.data?.bucketSeconds ?? 60)}`}
          icon={GitBranch}
          className="xl:col-span-2"
          action={<Legend items={[{ label: "Succeeded", color: "var(--series-1)" }, { label: "Failed", color: "var(--series-critical)" }]} />}
        >
          <div className="px-3 pb-3">
            {loading ? (
              <Loading />
            ) : (
              <TimeSeriesChart
                ariaLabel="Traces per interval, succeeded and failed"
                kind="stacked"
                times={times}
                height={220}
                series={[
                  { key: "ok", label: "Succeeded", color: "var(--series-1)", values: buckets.map((b) => b.traces - b.errorTraces) },
                  { key: "err", label: "Failed", color: "var(--series-critical)", values: buckets.map((b) => b.errorTraces) },
                ]}
              />
            )}
          </div>
        </Panel>

        <Panel title="Latency" subtitle="Mean trace duration" icon={Gauge}>
          <div className="px-3 pb-3">
            {loading ? (
              <Loading />
            ) : (
              <TimeSeriesChart
                ariaLabel="Mean trace duration per interval"
                kind="line"
                times={times}
                height={220}
                formatValue={(v) => formatDuration(v)}
                series={[{ key: "avg", label: "Mean", color: "var(--series-1)", values: buckets.map((b) => (b.traces ? b.avgDurationMs : null)) }]}
              />
            )}
          </div>
        </Panel>
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
        <Panel
          title="Top operations"
          subtitle="Busiest root spans in the selected range"
          icon={Activity}
          className="xl:col-span-2"
          action={
            <Link to="/traces" className="inline-flex items-center gap-1 text-xs font-medium text-[var(--accent)] hover:underline">
              All traces <ArrowRight size={12} />
            </Link>
          }
        >
          {loading ? (
            <Loading />
          ) : !overview.data?.topOperations.length ? (
            <EmptyState label="No traces in this range." hint="Requests appear here as soon as the exporter writes them." icon={GitBranch} />
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-y border-[var(--border)] bg-[var(--subtle)] text-left text-[11px] font-medium tracking-wide text-[var(--text-dim)] uppercase">
                    <th className="px-4 py-2 font-medium">Operation</th>
                    <th className="px-4 py-2 text-right font-medium">Requests</th>
                    <th className="px-4 py-2 text-right font-medium">Errors</th>
                    <th className="px-4 py-2 text-right font-medium">Avg</th>
                    <th className="px-4 py-2 text-right font-medium">Max</th>
                  </tr>
                </thead>
                <tbody>
                  {overview.data.topOperations.map((op) => {
                    const share = op.count / (overview.data.topOperations[0]?.count || 1)
                    const errPct = op.count ? (100 * op.errorCount) / op.count : 0
                    return (
                      <tr key={`${op.name}|${op.serviceName}`} className="border-b border-[var(--border)] last:border-0 hover:bg-[var(--hover)]">
                        <td className="max-w-[340px] px-4 py-2.5">
                          <Link
                            to={`/traces?endpoint=${encodeURIComponent(op.name)}&range=${range}`}
                            className="block truncate font-mono text-[13px] text-[var(--text)] hover:text-[var(--accent)]"
                            title={op.name}
                          >
                            {op.name}
                          </Link>
                          <div className="mt-1 flex items-center gap-2">
                            <div className="h-1 w-24 overflow-hidden rounded-full bg-[var(--grid)]">
                              <div className="h-full rounded-full bg-[var(--series-1)]" style={{ width: `${Math.max(3, share * 100)}%` }} />
                            </div>
                            <span className="truncate text-[11px] text-[var(--text-dim)]">{op.serviceName ?? "unknown service"}</span>
                          </div>
                        </td>
                        <td className="px-4 py-2.5 text-right tabular-nums">{op.count.toLocaleString()}</td>
                        <td className="px-4 py-2.5 text-right tabular-nums">
                          {op.errorCount > 0 ? (
                            <span className="inline-flex items-center gap-1 text-[var(--error)]">
                              <CircleAlert size={12} />
                              {errPct.toFixed(1)}%
                            </span>
                          ) : (
                            <span className="text-[var(--text-dim)]">0%</span>
                          )}
                        </td>
                        <td className="px-4 py-2.5 text-right font-mono text-xs tabular-nums">{formatDuration(op.avgDurationMs)}</td>
                        <td className="px-4 py-2.5 text-right font-mono text-xs text-[var(--text-muted)] tabular-nums">{formatDuration(op.maxDurationMs)}</td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </Panel>

        <Panel
          title="Log volume"
          subtitle="Records per interval"
          icon={ScrollText}
          action={<Legend items={[{ label: "Logs", color: "var(--series-1)" }, { label: "Errors", color: "var(--series-critical)" }]} />}
        >
          <div className="px-3 pb-3">
            {loading ? (
              <Loading />
            ) : (
              <TimeSeriesChart
                ariaLabel="Log records per interval, with errors"
                kind="stacked"
                times={times}
                height={220}
                series={[
                  { key: "logs", label: "Logs", color: "var(--series-1)", values: buckets.map((b) => b.logs - b.errorLogs) },
                  { key: "errors", label: "Errors", color: "var(--series-critical)", values: buckets.map((b) => b.errorLogs) },
                ]}
                extraTooltipRows={(i) => [{ label: "of which warnings", value: (buckets[i]?.warningLogs ?? 0).toLocaleString() }]}
              />
            )}
          </div>
        </Panel>
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <Panel
          title="Recent failures"
          icon={AlertTriangle}
          action={
            <Link to="/traces?errors=1" className="inline-flex items-center gap-1 text-xs font-medium text-[var(--accent)] hover:underline">
              View all <ArrowRight size={12} />
            </Link>
          }
        >
          {failing.isLoading ? (
            <Loading />
          ) : !failing.data?.items.length ? (
            <EmptyState label="No failed traces." hint="Nice. Traces whose spans report an error status show up here." icon={AlertTriangle} />
          ) : (
            <ul className="border-t border-[var(--border)]">
              {failing.data.items.map((trace) => (
                <li key={trace.traceId} className="border-b border-[var(--border)] last:border-0">
                  <Link to={`/traces/${trace.traceId}`} className="flex items-center gap-3 px-4 py-2.5 hover:bg-[var(--hover)]">
                    <CircleAlert size={14} className="shrink-0 text-[var(--error)]" aria-label="Failed" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-mono text-[13px] text-[var(--text)]">{trace.rootName ?? "(unnamed)"}</span>
                      <span className="block truncate text-[11px] text-[var(--text-dim)]">
                        {trace.serviceName ?? "unknown service"} · {trace.spanCount} spans
                      </span>
                    </span>
                    <span className="shrink-0 text-right">
                      <span className="block font-mono text-xs text-[var(--text)]">{formatDuration(trace.durationMs)}</span>
                      <span className="block text-[11px] text-[var(--text-dim)]">{formatRelative(trace.startTime)}</span>
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel
          title="Latest logs"
          icon={ScrollText}
          action={
            <Link to="/logs" className="inline-flex items-center gap-1 text-xs font-medium text-[var(--accent)] hover:underline">
              View all <ArrowRight size={12} />
            </Link>
          }
        >
          {summary.isLoading ? (
            <Loading />
          ) : !summary.data?.recentLogs.length ? (
            <EmptyState label="No logs yet." icon={ScrollText} />
          ) : (
            <ul className="border-t border-[var(--border)]">
              {summary.data.recentLogs.slice(0, 6).map((log) => (
                <li key={log.id} className="flex items-center gap-3 border-b border-[var(--border)] px-4 py-2.5 last:border-0">
                  <SeverityBadge severityNumber={log.severityNumber} text={log.severityText} />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-mono text-xs text-[var(--text)]" title={log.body ?? undefined}>
                      {log.body ?? "—"}
                    </span>
                    <span className="block truncate text-[11px] text-[var(--text-dim)]">{log.serviceName ?? "unknown service"}</span>
                  </span>
                  <span className="shrink-0 text-[11px] text-[var(--text-dim)]">{formatRelative(log.timestamp)}</span>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  )
}
