import type { CSSProperties, ReactNode } from "react"
import { CircleAlert } from "lucide-react"
import clsx from "clsx"
import type { SpanItem } from "@/lib/api"
import { formatDuration } from "@/lib/format"
import { serviceOf, spanExceptions, type TraceAnalysis } from "./traceAnalysis"

function Card({ title, label, className, children }: { title: string; label: string; className?: string; children: ReactNode }) {
  return (
    <section aria-label={label} className={clsx("flex min-w-0 flex-col gap-3 rounded-xl bg-[var(--subtle)] p-4 ring-1 ring-[var(--border)]", className)}>
      <h3 className="text-xs font-semibold text-[var(--text-muted)]">{title}</h3>
      {children}
    </section>
  )
}

function SpanLink({ span, analysis, selected, meta, detail, onSelect }: { span: SpanItem; analysis: TraceAnalysis; selected?: boolean; meta?: string; detail?: string | null; onSelect: (id: string) => void }) {
  return (
    <button
      type="button"
      onClick={() => onSelect(span.spanId)}
      aria-current={selected ? "true" : undefined}
      className={clsx(
        "flex w-full min-w-0 flex-col gap-0.5 rounded-lg px-2 py-1.5 text-left text-[13px] transition-colors",
        selected ? "bg-[var(--accent)]/12 text-[var(--text)]" : "hover:bg-[var(--hover-strong)]",
      )}
    >
      <span className="flex w-full min-w-0 items-center gap-2">
        <span className="h-2 w-2 shrink-0 rounded-sm" style={{ background: `var(${analysis.serviceColor(span)})` }} />
        <span className={clsx("min-w-0 flex-1 truncate font-mono text-xs", span.hasError && "text-[var(--error)]")} title={span.name}>
          {span.name}
        </span>
        <span className={clsx("shrink-0 font-mono text-[11px] tabular-nums", span.hasError ? "text-[var(--error)]" : "text-[var(--text-dim)]")}>{meta ?? formatDuration(span.durationMs)}</span>
      </span>
      {detail && <span className="truncate pl-4 font-mono text-[11px] text-[var(--text-muted)]" title={detail}>{detail}</span>}
    </button>
  )
}

/** Where the trace went wrong and where its time went: failing spans, time by service, and the
 * spans with the most self time. Every span links back into the details pane. */
export function TraceHotspots({
  analysis,
  spans,
  selectedSpanId,
  onSelect,
}: {
  analysis: TraceAnalysis
  spans: SpanItem[]
  selectedSpanId: string | null
  onSelect: (id: string) => void
}) {
  const criticalTotal = analysis.services.reduce((sum, s) => sum + s.criticalMs, 0) || 1
  const slowest = [...spans].sort((a, b) => (analysis.selfTimeMs.get(b.spanId) ?? 0) - (analysis.selfTimeMs.get(a.spanId) ?? 0)).slice(0, 8)
  // Where errors originated: failing spans none of whose children failed (their ancestors are
  // usually marked failed only because the error propagated up).
  const failures = spans
    .filter((s) => s.hasError && !(analysis.childrenByParent.get(s.spanId) ?? []).some((c) => c.hasError))
    .sort((a, b) => a.startTime.localeCompare(b.startTime))

  return (
    <div className="grid gap-3 p-3 lg:grid-cols-2">
      {failures.length > 0 && (
        <Card title={`Where it failed · ${failures.length}`} label="Failing spans" className="lg:col-span-2">
          <div className="flex flex-col">
            {failures.map((s) => {
              const ex = spanExceptions(s)[0]
              return (
                <SpanLink
                  key={s.spanId}
                  span={s}
                  analysis={analysis}
                  selected={s.spanId === selectedSpanId}
                  meta={`+${formatDuration(Math.max(0, new Date(s.startTime).getTime() - analysis.traceStart))}`}
                  detail={ex ? [ex.type, ex.message].filter(Boolean).join(": ") : s.statusMessage}
                  onSelect={onSelect}
                />
              )
            })}
          </div>
        </Card>
      )}

      <Card title="Time by service" label="Services">
        <ul className="flex flex-col gap-3">
          {analysis.services.map((s) => (
            <li key={s.name} className="flex flex-col gap-1.5">
              <div className="flex items-center gap-2 text-[13px]">
                <span className="h-2 w-2 shrink-0 rounded-sm" style={{ background: `var(${s.color})` }} />
                <span className="min-w-0 flex-1 truncate" title={s.name}>
                  {s.name}
                </span>
                <span className="text-xs text-[var(--text-dim)]">
                  {s.spanCount} {s.spanCount === 1 ? "span" : "spans"}
                </span>
                {s.errorCount > 0 && (
                  <span className="inline-flex items-center gap-0.5 text-xs text-[var(--error)]">
                    <CircleAlert size={11} aria-hidden="true" />
                    {s.errorCount}
                  </span>
                )}
              </div>
              <div className="h-1.5 overflow-hidden rounded-full bg-[var(--hover-strong)]">
                <div className="h-full w-(--share) rounded-full" style={{ "--share": `${(s.criticalMs / criticalTotal) * 100}%`, background: `var(${s.color})` } as CSSProperties} />
              </div>
              <div className="text-[11px] text-[var(--text-dim)]">{formatDuration(s.criticalMs)} on the critical path</div>
            </li>
          ))}
        </ul>
      </Card>

      <Card title="Slowest spans (self time)" label="Slowest spans">
        <div className="flex flex-col">
          {slowest.map((s) => (
            <SpanLink
              key={s.spanId}
              span={s}
              analysis={analysis}
              selected={s.spanId === selectedSpanId}
              meta={formatDuration(analysis.selfTimeMs.get(s.spanId) ?? 0)}
              detail={`${serviceOf(s)} · ${formatDuration(s.durationMs)} total`}
              onSelect={onSelect}
            />
          ))}
        </div>
      </Card>
    </div>
  )
}
