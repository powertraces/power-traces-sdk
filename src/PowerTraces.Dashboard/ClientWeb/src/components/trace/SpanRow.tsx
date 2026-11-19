import { useEffect, useRef, type CSSProperties } from "react"
import { ChevronRight, Zap } from "lucide-react"
import clsx from "clsx"
import type { SpanItem } from "@/lib/api"
import { formatDuration } from "@/lib/format"
import { NAME_COLUMN } from "./spanTree"
import type { Exchange } from "./story"
import { CatIcon, detectCategory, CAT_STYLES, HttpStatusBadge, KindBadge, getHttpStatusFromSpan, getStatusColor } from "./traceHelpers"

function countDescendants(spanId: string, childrenByParent: Map<string, SpanItem[]>): number {
  let count = 0
  for (const c of childrenByParent.get(spanId) ?? []) count += 1 + countDescendants(c.spanId, childrenByParent)
  return count
}

export function SpanRow({
  span,
  childrenByParent,
  depth,
  traceStart,
  traceDuration,
  selectedId,
  onSelect,
  criticalSpanIds,
  ignoredSpanNames,
  matches,
  collapsed,
  onToggle,
  exchanges,
  partnerId,
}: {
  span: SpanItem
  childrenByParent: Map<string, SpanItem[]>
  depth: number
  traceStart: number
  traceDuration: number
  selectedId: string | null
  onSelect: (id: string) => void
  criticalSpanIds: Set<string>
  ignoredSpanNames: Set<string>
  /** When the waterfall is filtered: spans that don't match are dimmed, not hidden, so the tree
   * keeps its shape. */
  matches?: (span: SpanItem) => boolean
  /** Spans whose subtree is folded away. Ignored while filtering, so every match stays visible. */
  collapsed: Set<string>
  onToggle: (spanId: string) => void
  /** Calls by span id — both halves of a traced call map to the same exchange. */
  exchanges: Map<string, Exchange>
  /** The other half of the selected span's call, highlighted with it. */
  partnerId: string | null
}) {
  const children = childrenByParent.get(span.spanId) ?? []
  const isCollapsed = !matches && collapsed.has(span.spanId)
  const rowRef = useRef<HTMLDivElement>(null)
  const isCritical = criticalSpanIds.has(span.spanId)
  const start = new Date(span.startTime).getTime()
  const relStart = traceDuration > 0 ? ((start - traceStart) / traceDuration) * 100 : 0
  const widthPct = traceDuration > 0 ? Math.max((span.durationMs / traceDuration) * 100, 0.8) : 2
  const clampedLeft = Math.min(Math.max(relStart, 0), 99)
  const clampedWidth = Math.min(widthPct, 100 - clampedLeft)
  const cat = detectCategory(span)
  const catColor = `var(${CAT_STYLES[cat].color})`
  // Status (did this call succeed?) wins for the bar/swatch when known; category (what kind of
  // call was this?) still always drives the icon, and is the fallback when there's no status.
  const barColor = `var(${getStatusColor(span) ?? CAT_STYLES[cat].color})`
  const httpStatus = getHttpStatusFromSpan(span)
  const selected = selectedId === span.spanId
  const isPartner = partnerId === span.spanId
  const exchange = exchanges.get(span.spanId)
  const pairedRole = exchange?.client && exchange.server ? (exchange.client.spanId === span.spanId ? "sent" : "answer") : null
  const dimmed = matches ? !matches(span) : false

  // A span picked elsewhere (the details pane, the hotspots) may be off screen — bring it into
  // view. Not on first render, so opening a trace doesn't scroll past its header.
  const mounted = useRef(false)
  useEffect(() => {
    if (mounted.current && selected) rowRef.current?.scrollIntoView({ block: "nearest" })
    mounted.current = true
  }, [selected])

  return (
    <div>
      <div
        ref={rowRef}
        role="treeitem"
        aria-selected={selected}
        aria-expanded={children.length > 0 ? !isCollapsed : undefined}
        aria-level={depth + 1}
        className={`group/spanrow flex cursor-pointer items-stretch border-b border-[var(--border)] border-l-2 transition-colors ${
          selected ? "bg-[var(--accent)]/[0.06]" : isPartner ? "bg-[var(--accent)]/[0.03]" : "border-l-transparent hover:bg-[var(--hover)]"
        } ${dimmed ? "opacity-35" : ""}`}
        style={selected ? { borderLeftColor: barColor } : isPartner ? { borderLeftColor: barColor, borderLeftStyle: "dashed" } : undefined}
        onClick={() => onSelect(span.spanId)}
      >
        {/* Left: tree + name */}
        <div className={`flex ${NAME_COLUMN} min-w-0 shrink-0 items-center gap-1 border-r border-[var(--border)] px-2 py-1.5`}>
          {Array.from({ length: depth }).map((_, i) => (
            <div key={i} className="relative w-3.5 shrink-0 self-stretch">
              <div className="absolute top-0 bottom-0 left-[calc(50%-0.5px)] w-px bg-[var(--border)]" />
            </div>
          ))}
          {depth > 0 && (
            <div className="relative -ml-3.5 w-3.5 shrink-0 self-stretch">
              <div className="absolute top-0 left-[calc(50%-0.5px)] h-1/2 w-px bg-[var(--border)]" />
              <div className="absolute top-1/2 left-[calc(50%-0.5px)] h-px w-2 bg-[var(--border)]" />
            </div>
          )}
          {children.length > 0 ? (
            <button
              type="button"
              onClick={(e) => {
                e.stopPropagation()
                onToggle(span.spanId)
              }}
              disabled={!!matches}
              aria-label={isCollapsed ? `Expand ${span.name}` : `Collapse ${span.name}`}
              title={isCollapsed ? `Show ${children.length} child ${children.length === 1 ? "span" : "spans"}` : "Hide child spans"}
              className="flex h-4 w-4 shrink-0 items-center justify-center rounded text-[var(--text-dim)] hover:bg-[var(--hover-strong)] hover:text-[var(--text)] disabled:opacity-40"
            >
              <ChevronRight size={12} className={clsx("transition-transform", !isCollapsed && "rotate-90")} />
            </button>
          ) : (
            <span className="w-4 shrink-0" />
          )}
          <div className="h-4 w-1 shrink-0 rounded-sm" style={{ background: barColor }} />
          {isCritical && (
            <span className="shrink-0 text-[var(--warn)]" title="On the critical path">
              <Zap size={10} />
            </span>
          )}
          <span style={{ color: catColor }} className="shrink-0">
            <CatIcon cat={cat} />
          </span>
          <span
            className={`ml-1 min-w-0 flex-1 truncate font-mono text-xs ${
              span.hasError ? "font-semibold text-[var(--error)]" : "text-[var(--text)]"
            }`}
            title={span.name}
          >
            {span.name}
          </span>
          {isCollapsed && <span className="shrink-0 rounded bg-[var(--hover-strong)] px-1 text-[10px] text-[var(--text-muted)] tabular-nums">+{countDescendants(span.spanId, childrenByParent)}</span>}
          {ignoredSpanNames.has(span.name) && <span title="This span name is ignored for future exports" className="rounded border border-[var(--warn)]/40 px-1 py-0.5 text-[9px] text-[var(--warn)]">Ignored</span>}
          {pairedRole && (
            <span
              title={
                pairedRole === "sent"
                  ? `The request ${exchange!.caller ?? "the caller"} sent; ${exchange!.callee}'s side of it is the step below`
                  : `${exchange!.callee} handling the request from ${exchange!.caller ?? "the caller"}; the sent side is the step above`
              }
              className={clsx(
                "shrink-0 rounded px-1 py-px text-[9px] font-bold tracking-wide",
                pairedRole === "answer" && exchange!.failed ? "bg-[var(--error)]/15 text-[var(--error)]" : "bg-[var(--hover-strong)] text-[var(--text-muted)]",
              )}
            >
              {pairedRole === "sent" ? "SENT" : "ANSWER"}
            </span>
          )}
          <HttpStatusBadge status={httpStatus} />
          <KindBadge kind={span.kind} hasError={span.hasError} />
        </div>

        {/* Right: timeline bar */}
        <div className="flex flex-1 items-center px-3 py-1.5">
          <div className="relative h-5 w-full">
            <div
              className="absolute top-0.5 h-4 min-w-[3px] rounded-sm left-(--bar-left) w-(--bar-w)"
              style={
                {
                  "--bar-left": `${clampedLeft}%`,
                  "--bar-w": `${clampedWidth}%`,
                  background: barColor,
                  opacity: isCritical ? 0.95 : 0.75,
                  outline: isCritical ? `1px solid var(--warn)` : undefined,
                } as CSSProperties
              }
            >
              {clampedWidth > 12 && (
                <span className="absolute inset-0 flex items-center justify-center truncate px-1 font-mono text-[9px] leading-none font-semibold text-[var(--bg)]">
                  {formatDuration(span.durationMs)}
                </span>
              )}
            </div>
            {clampedWidth <= 12 && (
              // Beside the bar — on its left when the bar ends near the right edge, so it isn't clipped.
              <span
                className={`absolute top-0.5 leading-4 font-mono text-[10px] whitespace-nowrap text-[var(--text-dim)] ${clampedLeft + clampedWidth > 90 ? "right-(--label-right)" : "left-(--label-left)"}`}
                style={{ "--label-left": `calc(${clampedLeft + clampedWidth}% + 4px)`, "--label-right": `calc(${100 - clampedLeft}% + 4px)` } as CSSProperties}
              >
                {formatDuration(span.durationMs)}
              </span>
            )}
          </div>
        </div>
      </div>

      {!isCollapsed && children.map((c) => (
        <SpanRow
          key={c.spanId}
          span={c}
          childrenByParent={childrenByParent}
          depth={depth + 1}
          traceStart={traceStart}
          traceDuration={traceDuration}
          selectedId={selectedId}
          onSelect={onSelect}
          criticalSpanIds={criticalSpanIds}
          ignoredSpanNames={ignoredSpanNames}
          matches={matches}
          collapsed={collapsed}
          onToggle={onToggle}
          exchanges={exchanges}
          partnerId={partnerId}
        />
      ))}
    </div>
  )
}
