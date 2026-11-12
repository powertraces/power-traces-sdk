import { useState, type CSSProperties } from "react"
import { Link } from "react-router-dom"
import { Ban, ChevronRight, CircleAlert, Layers, Search } from "lucide-react"
import clsx from "clsx"
import type { SpanItem } from "@/lib/api"
import { formatDuration } from "@/lib/format"
import { CodeBlock } from "@/components/CodeBlock"
import { CopyButton } from "@/components/CopyButton"
import { tracesHref } from "@/lib/traceSearch"
import { getHttpStatusFromSpan, kindLabel } from "./traceHelpers"
import { AttributeList } from "./AttributeList"
import { ExceptionCard } from "./ExceptionCard"
import { pathToSpan, serviceOf, spanExceptions, type TraceAnalysis } from "./traceAnalysis"

/** Attribute values arrive as JSON values — strings as-is, everything else (numbers, booleans,
 * arrays, objects) as its JSON text, which is also what gets copied. */
function valueText(v: unknown): string {
  return typeof v === "string" ? v : JSON.stringify(v)
}

function SectionTitle({ children }: { children: string }) {
  return <h3 className="text-xs font-semibold text-[var(--text-muted)]">{children}</h3>
}

/** Root › … › parent: how the span is reached, each step selectable. Long paths keep the root
 * and the two nearest ancestors. */
function SpanAncestors({ span, analysis, onSelect }: { span: SpanItem; analysis: TraceAnalysis; onSelect: (id: string) => void }) {
  const ancestors = pathToSpan(span, analysis.spanById).slice(0, -1)
  if (ancestors.length === 0) return null
  const shown = ancestors.length > 3 ? [ancestors[0], null, ...ancestors.slice(-2)] : ancestors

  return (
    <nav aria-label="Path to this span" className="flex min-w-0 flex-wrap items-center gap-1 text-[11px] text-[var(--text-dim)]">
      {shown.map((a, i) => (
        <span key={a?.spanId ?? "gap"} className="flex min-w-0 items-center gap-1">
          {i > 0 && <ChevronRight size={11} aria-hidden="true" className="shrink-0" />}
          {a ? (
            <button
              type="button"
              onClick={() => onSelect(a.spanId)}
              title={`${a.name} · ${serviceOf(a)}`}
              className={clsx("max-w-48 truncate rounded px-1 py-0.5 font-mono hover:bg-[var(--hover-strong)] hover:text-[var(--text)]", a.hasError && "text-[var(--error)]")}
            >
              {a.name}
            </button>
          ) : (
            <span title={`${ancestors.length - 3} more`}>…</span>
          )}
        </span>
      ))}
      <ChevronRight size={11} aria-hidden="true" className="shrink-0" />
    </nav>
  )
}

/** The selected span, in full: where it sits in the tree, identity, timing within the trace,
 * exceptions, then every attribute (filterable, each copyable), events and links. */
export function SpanFocus({
  span,
  analysis,
  traceDurationMs,
  ignored,
  onIgnore,
  onSelect,
}: {
  span: SpanItem
  analysis: TraceAnalysis
  traceDurationMs: number
  ignored: boolean
  onIgnore: (name: string) => void
  onSelect: (spanId: string) => void
}) {
  const [filter, setFilter] = useState("")
  const httpStatus = getHttpStatusFromSpan(span)
  const exceptions = spanExceptions(span)
  const offsetMs = Math.max(0, new Date(span.startTime).getTime() - analysis.traceStart)
  const total = traceDurationMs || 1
  const leftPct = Math.min((offsetMs / total) * 100, 99.5)
  const widthPct = Math.max(Math.min((span.durationMs / total) * 100, 100 - leftPct), 0.5)
  const selfMs = analysis.selfTimeMs.get(span.spanId) ?? span.durationMs
  const swatch = `var(${analysis.serviceColor(span)})`

  const attrs = Object.entries(span.attributes).filter(([, v]) => v != null && v !== "")
  const needle = filter.trim().toLowerCase()
  const shown = needle ? attrs.filter(([k, v]) => k.toLowerCase().includes(needle) || valueText(v).toLowerCase().includes(needle)) : attrs
  const otherEvents = span.events.filter((e) => e.name !== "exception")

  return (
    <section aria-label="Selected span" className="min-w-0 overflow-hidden rounded-xl border border-[var(--border)] bg-[var(--surface)] shadow-[var(--shadow-sm)]">
      <div className="flex flex-col gap-4 border-b border-[var(--border)] p-5">
        <div className="flex flex-wrap items-center gap-2 text-xs text-[var(--text-muted)]">
          <span className="h-2 w-2 shrink-0 rounded-sm" style={{ background: swatch }} />
          {serviceOf(span)}
          <span className="rounded bg-[var(--hover-strong)] px-1.5 py-px text-[10px] font-semibold tracking-wide uppercase">{kindLabel(span.kind)}</span>
          <span className="ml-auto flex items-center gap-1 font-mono text-[11px] text-[var(--text-dim)]">
            {span.spanId}
            <CopyButton value={span.spanId} label="Copy span id" />
          </span>
        </div>

        <div className="flex flex-col gap-1.5">
          <SpanAncestors span={span} analysis={analysis} onSelect={onSelect} />
          <div className="flex flex-wrap items-start justify-between gap-3">
            <h2 className="min-w-0 font-mono text-base leading-snug font-semibold break-all">{span.name}</h2>
            <span
              className={clsx(
                "shrink-0 rounded-full px-2.5 py-0.5 text-xs font-medium",
                span.hasError ? "bg-[var(--error)]/12 text-[var(--error)]" : span.status === "OK" ? "bg-[var(--success)]/12 text-[var(--success)]" : "bg-[var(--hover-strong)] text-[var(--text-muted)]",
              )}
            >
              {span.hasError ? (httpStatus ? `Error · ${httpStatus}` : "Error") : span.status === "OK" ? (httpStatus ? `OK · ${httpStatus}` : "OK") : httpStatus ?? "Unset"}
            </span>
          </div>
        </div>

        <div className="flex flex-col gap-1.5">
          <div className="relative h-2.5 overflow-hidden rounded-full bg-[var(--hover-strong)]" aria-hidden="true">
            <div
              className="absolute inset-y-0 left-(--focus-left) w-(--focus-width) rounded-full"
              style={{ "--focus-left": `${leftPct}%`, "--focus-width": `${widthPct}%`, background: span.hasError ? "var(--error)" : swatch } as CSSProperties}
            />
          </div>
          <dl className="flex flex-wrap justify-between gap-x-4 gap-y-1 text-xs text-[var(--text-muted)]">
            <div className="flex gap-1">
              <dt>Starts</dt>
              <dd className="font-semibold text-[var(--text)] tabular-nums">+{formatDuration(offsetMs)}</dd>
            </div>
            <div className="flex gap-1">
              <dt>Duration</dt>
              <dd className="tabular-nums">
                <span className="font-semibold text-[var(--text)]">{formatDuration(span.durationMs)}</span> · {Math.round((span.durationMs / total) * 100)}% of trace
              </dd>
            </div>
            <div className="flex gap-1">
              <dt>Self time</dt>
              <dd className="font-semibold text-[var(--text)] tabular-nums">{formatDuration(selfMs)}</dd>
            </div>
          </dl>
        </div>

        {exceptions.map((ex, i) => (
          <ExceptionCard key={i} exception={ex} at={`+${formatDuration(offsetMs + ex.offsetMs)}`} />
        ))}
        {exceptions.length === 0 && span.statusMessage && (
          <div className="flex gap-2.5 rounded-lg bg-[var(--error)]/8 px-3.5 py-3">
            <CircleAlert size={16} className="mt-px shrink-0 text-[var(--error)]" aria-hidden="true" />
            <div className="min-w-0 flex-1 font-mono text-xs leading-relaxed break-words text-[var(--text)]">{span.statusMessage}</div>
            <CopyButton value={span.statusMessage} label="Copy status message" className="-my-1 self-start" />
          </div>
        )}

        <button
          type="button"
          onClick={() => onIgnore(span.name)}
          disabled={ignored}
          className="inline-flex w-fit items-center gap-1 text-xs text-[var(--text-muted)] hover:text-[var(--warn)] disabled:opacity-60"
          title="Stop exporting future spans with this name. Existing telemetry is kept."
        >
          <Ban size={12} /> {ignored ? "Spans like this are ignored" : "Ignore future spans like this"}
        </button>
        <Link
          to={tracesHref({ filters: [{ key: "span.name", value: span.name, match: "Equals" }] })}
          className="-mt-2 inline-flex w-fit items-center gap-1 text-xs text-[var(--text-muted)] hover:text-[var(--accent)]"
        >
          <Search size={12} /> Find traces with this span
        </Link>
      </div>

      <div className="flex flex-col gap-6 p-5">
        {attrs.length > 0 && (
          <div className="flex flex-col gap-3">
            <div className="flex flex-wrap items-center gap-2">
              <label className="flex h-8 min-w-48 flex-1 items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--input-bg)] px-2.5 text-[var(--text-dim)] focus-within:border-[var(--accent)]">
                <Search size={13} aria-hidden="true" />
                <input
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                  placeholder={`Filter ${attrs.length} attribute${attrs.length === 1 ? "" : "s"}…`}
                  aria-label="Filter attributes"
                  className="min-w-0 flex-1 bg-transparent text-[13px] text-[var(--text)] outline-none placeholder:text-[var(--text-dim)]"
                />
              </label>
              <CopyButton
                value={JSON.stringify(span.attributes, null, 2)}
                label="Copy as JSON"
                showLabel
                className="h-8 border border-[var(--border)] px-2.5 text-[var(--text-muted)]"
              />
            </div>
            {shown.length === 0 ? (
              <p className="py-4 text-center text-xs text-[var(--text-dim)]">No attribute matches “{filter}”.</p>
            ) : (
              <AttributeList attributes={shown} />
            )}
          </div>
        )}

        {otherEvents.length > 0 && (
          <div className="flex flex-col gap-2">
            <SectionTitle>{`Events · ${otherEvents.length}`}</SectionTitle>
            <div className="divide-y divide-[var(--border)] overflow-hidden rounded-lg ring-1 ring-[var(--border)]">
              {otherEvents.map((e, i) => (
                <div key={i} className="px-3 py-2.5">
                  <div className="flex items-center gap-3">
                    <span className="shrink-0 font-mono text-[11px] text-[var(--text-dim)] tabular-nums">
                      +{formatDuration(offsetMs + new Date(e.time).getTime() - new Date(span.startTime).getTime())}
                    </span>
                    <span className="text-xs font-medium">{e.name}</span>
                  </div>
                  {e.attributes && Object.keys(e.attributes).length > 0 && (
                    <div className="mt-1.5">
                      <CodeBlock value={JSON.stringify(e.attributes)} />
                    </div>
                  )}
                </div>
              ))}
            </div>
          </div>
        )}

        {span.links.length > 0 && (
          <div className="flex flex-col gap-2">
            <SectionTitle>{`Links · ${span.links.length}`}</SectionTitle>
            <div className="divide-y divide-[var(--border)] overflow-hidden rounded-lg ring-1 ring-[var(--border)]">
              {span.links.map((l, i) => (
                <div key={i} className="px-3 py-2 font-mono text-[11px] break-all text-[var(--text-muted)]">
                  {l.traceId} / {l.spanId}
                </div>
              ))}
            </div>
          </div>
        )}

        {attrs.length === 0 && otherEvents.length === 0 && span.links.length === 0 && (
          <div className="flex flex-col items-center justify-center gap-2 py-10 text-[var(--text-dim)]">
            <Layers size={24} />
            <p className="text-xs">No attributes recorded for this span</p>
          </div>
        )}
      </div>
    </section>
  )
}
