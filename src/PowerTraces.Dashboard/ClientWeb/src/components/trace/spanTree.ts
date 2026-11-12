import type { SpanItem } from "@/lib/api"

/** Width of the waterfall's tree/name column, shared by its rows and its time-axis header. */
export const NAME_COLUMN = "w-[clamp(220px,44%,460px)]"

/**
 * A span whose parentSpanId doesn't resolve to another span within this same trace is treated as
 * a root too, not just ones with an empty parentSpanId — a partial trace (an unexported or
 * not-yet-arrived parent) would otherwise leave it looking like a child waiting for a parent that
 * will never show up, and get silently dropped from the tree.
 */
export function findRootSpans(spans: SpanItem[]): SpanItem[] {
  const spanIds = new Set(spans.map((s) => s.spanId))
  return spans.filter((s) => !s.parentSpanId || !spanIds.has(s.parentSpanId))
}

export function buildChildrenMap(spans: SpanItem[]): Map<string, SpanItem[]> {
  const roots = findRootSpans(spans)
  const rootIds = new Set(roots.map((r) => r.spanId))
  const map = new Map<string, SpanItem[]>()
  for (const span of spans) {
    if (rootIds.has(span.spanId)) continue
    const list = map.get(span.parentSpanId)
    if (list) list.push(span)
    else map.set(span.parentSpanId, [span])
  }
  for (const list of map.values()) list.sort((a, b) => a.startTime.localeCompare(b.startTime))
  return map
}
