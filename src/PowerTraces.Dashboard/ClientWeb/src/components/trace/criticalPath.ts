import type { SpanItem } from "@/lib/api"

export interface CriticalSegment {
  span: SpanItem
  /** Epoch milliseconds. */
  from: number
  to: number
}

const startOf = (s: SpanItem) => new Date(s.startTime).getTime()
const endOf = (s: SpanItem) => startOf(s) + s.durationMs

/**
 * The critical path: the stretches of time the trace was actually waiting on, each owned by the
 * span doing the work then — as opposed to spans that ran in parallel and finished early. Walk
 * backward from the end of the span that finishes last; within a span, the child that finished
 * last before the current point is what the span was waiting on, so recurse into it and continue
 * from that child's start; any time no child covers is the span's own work. Unlike following a
 * single child per level, this credits every sequential call (a DB query, then a payment, then a
 * publish) with its share.
 */
export function computeCriticalSegments(spans: SpanItem[]): CriticalSegment[] {
  if (spans.length === 0) return []
  const ids = new Set(spans.map((s) => s.spanId))
  const childrenByParent = new Map<string, SpanItem[]>()
  for (const span of spans) {
    if (!span.parentSpanId || !ids.has(span.parentSpanId)) continue
    const siblings = childrenByParent.get(span.parentSpanId)
    if (siblings) siblings.push(span)
    else childrenByParent.set(span.parentSpanId, [span])
  }

  let last = spans[0]
  for (const span of spans) if (endOf(span) > endOf(last)) last = span
  // Start from the outermost ancestor of the latest-finishing span, so the root's own time counts.
  let top = last
  const byId = new Map(spans.map((s) => [s.spanId, s]))
  const seen = new Set<string>([top.spanId])
  for (let p = byId.get(top.parentSpanId); p && !seen.has(p.spanId); p = byId.get(p.parentSpanId)) {
    seen.add(p.spanId)
    top = p
  }

  const segments: CriticalSegment[] = []
  const visited = new Set<string>()
  const walk = (span: SpanItem, until: number) => {
    if (visited.has(span.spanId)) return
    visited.add(span.spanId)
    const start = startOf(span)
    let point = Math.min(until, endOf(span))
    const children = [...(childrenByParent.get(span.spanId) ?? [])].sort((a, b) => endOf(b) - endOf(a))
    for (const child of children) {
      const childEnd = Math.min(endOf(child), point)
      // Skip children that started after the point we've walked back to (they ran in parallel
      // with a later call) and children that ended before this span began.
      if (startOf(child) >= point || childEnd <= start) continue
      if (point > childEnd) segments.push({ span, from: childEnd, to: point })
      walk(child, childEnd)
      point = Math.max(start, startOf(child))
    }
    if (point > start) segments.push({ span, from: start, to: point })
  }
  walk(top, endOf(top))
  return segments
}

export function computeCriticalPath(spans: SpanItem[]): Set<string> {
  return new Set(computeCriticalSegments(spans).map((s) => s.span.spanId))
}
