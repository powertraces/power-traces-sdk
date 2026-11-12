import type { CSSProperties, ReactNode } from "react"
import { Archive, CircleAlert, CircleCheck, Clock, Pin } from "lucide-react"
import type { RetentionSettings, TraceDetail } from "@/lib/api"
import { formatDuration, formatRelative, formatTimestamp } from "@/lib/format"
import { CopyButton } from "@/components/CopyButton"
import type { TraceAnalysis } from "./traceAnalysis"

const DAY_MS = 86_400_000

/** What retention will do with this trace, in a few words — or null when retention is off or
 * unsupported. */
function retentionNote(trace: TraceDetail, retention: RetentionSettings | null): { label: string; hint: string; tone: "kept" | "expiring" } | null {
  if (trace.isPinned) {
    return { label: "Kept forever", hint: "Pinned: retention never deletes this trace or its logs.", tone: "kept" }
  }
  if (!retention?.enabled) return null
  if (trace.hasError && retention.keepErrorTraces) {
    return { label: "Kept · failed trace", hint: "Retention keeps failed traces.", tone: "kept" }
  }
  const deleteAt = new Date(trace.startTime).getTime() + retention.traceRetentionDays * DAY_MS
  const days = Math.ceil((deleteAt - Date.now()) / DAY_MS)
  return {
    label: days <= 0 ? "Due for cleanup" : `Deleted in ${days} ${days === 1 ? "day" : "days"}`,
    hint: `Retention keeps traces for ${retention.traceRetentionDays} days. Keep this one to exempt it.`,
    tone: "expiring",
  }
}

/** Identity, status, actions, the headline numbers, and how the critical path splits across
 * services. */
export function TraceHeader({
  trace,
  analysis,
  retention,
  actions,
}: {
  trace: TraceDetail
  analysis: TraceAnalysis
  retention: RetentionSettings | null
  actions: ReactNode
}) {
  const errorSpans = trace.spans.filter((s) => s.hasError).length
  const criticalTotal = analysis.services.reduce((sum, s) => sum + s.criticalMs, 0)
  const top = analysis.services[0]
  const topShare = top && criticalTotal > 0 ? Math.round((top.criticalMs / criticalTotal) * 100) : null
  const note = retentionNote(trace, retention)

  const stats: { label: string; value: string; hint: string; tone?: "error" }[] = [
    { label: "Duration", value: formatDuration(trace.durationMs), hint: `ended ${formatTimestamp(trace.endTime)}` },
    { label: "Spans", value: trace.spans.length.toLocaleString(), hint: `depth ${analysis.maxDepth}` },
    {
      label: "Services",
      value: analysis.services.length.toLocaleString(),
      hint: analysis.services.map((s) => s.name).join(", ") || "—",
    },
    {
      label: "Errors",
      value: errorSpans.toLocaleString(),
      hint: analysis.firstErrorOffsetMs === null ? "none" : `first at +${formatDuration(analysis.firstErrorOffsetMs)}`,
      tone: errorSpans > 0 ? "error" : undefined,
    },
    { label: "Critical path", value: topShare === null ? "—" : `${topShare}%`, hint: top ? `in ${top.name}` : "—" },
  ]

  return (
    <header className="overflow-hidden rounded-xl border border-[var(--border)] bg-[var(--surface)] shadow-[var(--shadow-sm)]">
      <div className="flex flex-col gap-4 p-5 pb-4">
        <div className="flex flex-wrap items-start justify-between gap-x-6 gap-y-3">
          <div className="flex min-w-0 flex-1 items-start gap-3">
            <span
              className={`mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-lg ${trace.hasError ? "bg-[var(--error)]/12 text-[var(--error)]" : "bg-[var(--success)]/12 text-[var(--success)]"}`}
              title={trace.hasError ? "Failed" : "Succeeded"}
            >
              {trace.hasError ? <CircleAlert size={18} aria-label="Failed" /> : <CircleCheck size={18} aria-label="Succeeded" />}
            </span>
            <div className="flex min-w-0 flex-col gap-1">
              <h1 className="min-w-0 font-mono text-lg leading-snug font-semibold tracking-tight break-all sm:text-xl">{trace.rootName ?? "(unnamed trace)"}</h1>
              <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-[var(--text-muted)]">
                <span className={trace.hasError ? "font-medium text-[var(--error)]" : "font-medium text-[var(--success)]"}>
                  {trace.hasError ? `Failed${errorSpans > 0 ? ` · ${errorSpans} error ${errorSpans === 1 ? "span" : "spans"}` : ""}` : "Succeeded"}
                </span>
                <span aria-hidden="true">·</span>
                <span>{trace.serviceName ?? "—"}</span>
                <span aria-hidden="true">·</span>
                <span title={formatTimestamp(trace.startTime)}>
                  {formatTimestamp(trace.startTime)} ({formatRelative(trace.startTime)})
                </span>
                {note && (
                  <span
                    title={note.hint}
                    className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-medium ${note.tone === "kept" ? "bg-[var(--accent)]/12 text-[var(--accent)]" : "bg-[var(--hover-strong)] text-[var(--text-muted)]"}`}
                  >
                    {trace.isPinned ? <Pin size={11} aria-hidden="true" /> : note.tone === "kept" ? <Archive size={11} aria-hidden="true" /> : <Clock size={11} aria-hidden="true" />}
                    {note.label}
                  </span>
                )}
              </div>
              <p className="flex min-w-0 items-center gap-1 font-mono text-xs text-[var(--text-dim)]">
                <span className="truncate">{trace.traceId}</span>
                <CopyButton value={trace.traceId} label="Copy trace id" />
              </p>
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2">{actions}</div>
        </div>
      </div>

      {/* gap-px over a border-colored background draws the dividers at every column count. */}
      <dl className="grid grid-cols-2 gap-px border-t border-[var(--border)] bg-[var(--border)] sm:grid-cols-5">
        {stats.map((s) => (
          <div key={s.label} className="min-w-0 bg-[var(--surface)] px-5 py-3 last:col-span-2 sm:last:col-span-1">
            <dt className="text-[11px] font-medium tracking-wide text-[var(--text-dim)] uppercase">{s.label}</dt>
            <dd className={`mt-0.5 text-lg font-semibold tabular-nums ${s.tone === "error" ? "text-[var(--error)]" : ""}`}>{s.value}</dd>
            <dd className="truncate text-[11px] text-[var(--text-dim)]" title={s.hint}>
              {s.hint}
            </dd>
          </div>
        ))}
      </dl>

      {criticalTotal > 0 && (
        <div className="flex flex-col gap-2 border-t border-[var(--border)] px-5 py-3">
          <div className="flex h-1.5 gap-0.5 overflow-hidden rounded-full" role="img" aria-label="Critical path time by service">
            {analysis.services
              .filter((s) => s.criticalMs > 0)
              .map((s) => (
                <div
                  key={s.name}
                  className="h-full w-(--share)"
                  title={`${s.name}: ${formatDuration(s.criticalMs)}`}
                  style={{ "--share": `${(s.criticalMs / criticalTotal) * 100}%`, background: `var(${s.color})` } as CSSProperties}
                />
              ))}
          </div>
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-[var(--text-muted)]">
            {analysis.services
              .filter((s) => s.criticalMs > 0)
              .map((s) => (
                <span key={s.name} className="inline-flex items-center gap-1.5">
                  <span className="h-2 w-2 rounded-sm" style={{ background: `var(${s.color})` }} />
                  {s.name}
                  <span className="font-medium text-[var(--text)] tabular-nums">{formatDuration(s.criticalMs)}</span>
                </span>
              ))}
            <span className="ml-auto text-[var(--text-dim)]">Critical path by service</span>
          </div>
        </div>
      )}
    </header>
  )
}
