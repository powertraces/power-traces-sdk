import { useState, type CSSProperties, type ReactNode } from "react"
import { ChevronsDownUp, ChevronsUpDown, GitBranch, Search, Zap } from "lucide-react"
import clsx from "clsx"
import type { SpanItem, TraceDetail as TraceDetailDto } from "@/lib/api"
import { formatDuration } from "@/lib/format"
import { Button } from "@/components/ui"
import { SpanRow } from "./SpanRow"
import { NAME_COLUMN } from "./spanTree"
import type { TraceAnalysis } from "./traceAnalysis"

const MARKER_PCTS = [0, 25, 50, 75, 100]

/** The span tree on a shared time axis. Subtrees fold; finding or filtering to errors dims what
 * doesn't match (and unfolds everything, so no match is hidden). Picking a span hands it to the
 * page, which shows it in the details pane. */
export function TraceWaterfall({
  trace,
  analysis,
  ignoredSpanNames = new Set(),
  selectedSpanId,
  onSelectSpan,
  collapsed,
  onCollapsedChange,
  toolbarStart,
}: {
  trace: TraceDetailDto
  analysis: TraceAnalysis
  ignoredSpanNames?: Set<string>
  selectedSpanId: string | null
  onSelectSpan: (spanId: string) => void
  collapsed: Set<string>
  onCollapsedChange: (next: Set<string>) => void
  /** Rendered first in the toolbar row — the page's view switch. */
  toolbarStart?: ReactNode
}) {
  const [find, setFind] = useState("")
  const [errorsOnly, setErrorsOnly] = useState(false)
  const dur = trace.durationMs || 1
  const needle = find.trim().toLowerCase()
  const filtering = needle !== "" || errorsOnly
  const matches = filtering
    ? (s: SpanItem) => (!errorsOnly || s.hasError) && (!needle || s.name.toLowerCase().includes(needle) || (s.serviceName ?? "").toLowerCase().includes(needle))
    : undefined
  const matchCount = matches ? trace.spans.filter(matches).length : trace.spans.length
  const errorCount = trace.spans.filter((s) => s.hasError).length

  const rootIds = new Set(analysis.roots.map((r) => r.spanId))
  // Folding everything still shows each root's direct children — one level of structure.
  const foldable = trace.spans.filter((s) => !rootIds.has(s.spanId) && (analysis.childrenByParent.get(s.spanId)?.length ?? 0) > 0)
  const toggle = (spanId: string) => {
    const next = new Set(collapsed)
    if (!next.delete(spanId)) next.add(spanId)
    onCollapsedChange(next)
  }

  return (
    <div>
      <div className="flex flex-wrap items-center gap-2 border-b border-[var(--border)] px-3 py-2.5">
        {toolbarStart}
        <label className="flex h-8 min-w-40 flex-1 items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--input-bg)] px-2.5 text-[var(--text-dim)] focus-within:border-[var(--accent)] sm:max-w-72">
          <Search size={13} aria-hidden="true" />
          <input
            value={find}
            onChange={(e) => setFind(e.target.value)}
            placeholder="Find span or service…"
            aria-label="Find span"
            className="min-w-0 flex-1 bg-transparent text-[13px] text-[var(--text)] outline-none placeholder:text-[var(--text-dim)]"
          />
        </label>
        <button
          type="button"
          aria-pressed={errorsOnly}
          disabled={errorCount === 0}
          onClick={() => setErrorsOnly((v) => !v)}
          className={clsx(
            "h-8 rounded-lg border px-2.5 text-xs font-medium transition-colors disabled:opacity-40",
            errorsOnly ? "border-[var(--error)]/40 bg-[var(--error)]/10 text-[var(--error)]" : "border-[var(--border)] text-[var(--text-muted)] hover:bg-[var(--hover)]",
          )}
        >
          Errors only{errorCount > 0 && ` · ${errorCount}`}
        </button>
        {foldable.length > 0 && (
          <Button
            variant="ghost"
            size="sm"
            disabled={filtering}
            onClick={() => onCollapsedChange(collapsed.size > 0 ? new Set() : new Set(foldable.map((s) => s.spanId)))}
            title={collapsed.size > 0 ? "Show every span" : "Fold every subtree below the first level"}
          >
            {collapsed.size > 0 ? <ChevronsUpDown size={13} /> : <ChevronsDownUp size={13} />}
            {collapsed.size > 0 ? "Expand all" : "Collapse all"}
          </Button>
        )}
        {filtering && (
          <span className="text-xs text-[var(--text-dim)]" aria-live="polite">
            {matchCount} of {trace.spans.length} match
          </span>
        )}
        <span className="ml-auto flex items-center gap-1 text-[11px] text-[var(--warn)]" title="The chain of spans that determined this trace's total duration">
          <Zap size={10} /> Critical path
        </span>
      </div>

      <div className="flex items-stretch border-b border-[var(--border)] bg-[var(--subtle)] text-xs select-none">
        <div className={`flex ${NAME_COLUMN} shrink-0 items-center gap-1.5 border-r border-[var(--border)] px-3 py-2 text-[var(--text-dim)]`}>
          <GitBranch size={11} /> Span
        </div>
        <div className="relative flex-1 overflow-hidden px-3 py-2">
          <div className="relative h-4">
            {MARKER_PCTS.map((p) => (
              <span
                key={p}
                className={`absolute top-0 font-mono text-[10px] text-[var(--text-dim)] left-(--marker-pos) ${p === 0 ? "" : p === 100 ? "-translate-x-full" : "-translate-x-1/2"}`}
                style={{ "--marker-pos": `${p}%` } as CSSProperties}
              >
                {p === 0 ? "0" : formatDuration(Math.round((dur * p) / 100))}
              </span>
            ))}
          </div>
        </div>
      </div>

      <div role="tree" aria-label="Spans">
        {analysis.roots.map((r) => (
          <SpanRow
            key={r.spanId}
            span={r}
            childrenByParent={analysis.childrenByParent}
            depth={0}
            traceStart={analysis.traceStart}
            traceDuration={dur}
            selectedId={selectedSpanId}
            onSelect={onSelectSpan}
            criticalSpanIds={analysis.criticalSpanIds}
            ignoredSpanNames={ignoredSpanNames}
            matches={matches}
            collapsed={collapsed}
            onToggle={toggle}
          />
        ))}
      </div>
    </div>
  )
}
