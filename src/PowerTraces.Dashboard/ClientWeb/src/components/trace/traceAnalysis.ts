import type { SpanItem, TraceDetail } from "@/lib/api"
import { computeCriticalSegments } from "./criticalPath"
import { buildChildrenMap, findRootSpans } from "./spanTree"

const SERVICE_COLORS = ["--svc-1", "--svc-2", "--svc-3", "--svc-4", "--svc-5", "--svc-6"]
export const UNKNOWN_SERVICE = "(unknown service)"

export interface ServiceSummary {
  name: string
  /** CSS var name (no var()) for this service's swatch. */
  color: string
  spanCount: number
  errorCount: number
  /** Milliseconds of the trace's critical path spent in this service's own work. */
  criticalMs: number
}

export interface TraceAnalysis {
  spanById: Map<string, SpanItem>
  childrenByParent: Map<string, SpanItem[]>
  roots: SpanItem[]
  criticalSpanIds: Set<string>
  /** Services ordered by critical-path time, largest first. */
  services: ServiceSummary[]
  serviceColor: (span: SpanItem) => string
  selfTimeMs: Map<string, number>
  maxDepth: number
  traceStart: number
  firstErrorOffsetMs: number | null
}

const startMs = (s: SpanItem) => new Date(s.startTime).getTime()
const endMs = (s: SpanItem) => startMs(s) + s.durationMs
export const serviceOf = (s: SpanItem) => s.serviceName || UNKNOWN_SERVICE

/** Everything the trace page derives from the raw spans, computed once per trace. */
export function analyzeTrace(trace: TraceDetail): TraceAnalysis {
  const spans = trace.spans
  const spanById = new Map(spans.map((s) => [s.spanId, s]))
  const childrenByParent = buildChildrenMap(spans)
  const roots = findRootSpans(spans)
  const segments = computeCriticalSegments(spans)
  const criticalSpanIds = new Set(segments.map((seg) => seg.span.spanId))
  const traceStart = new Date(trace.startTime).getTime()

  // Colors follow first appearance, so the root's service is always the first swatch.
  const colorByService = new Map<string, string>()
  for (const s of [...spans].sort((a, b) => startMs(a) - startMs(b))) {
    const name = serviceOf(s)
    if (!colorByService.has(name)) colorByService.set(name, SERVICE_COLORS[colorByService.size % SERVICE_COLORS.length])
  }

  const criticalByService = new Map<string, number>()
  for (const seg of segments) {
    const name = serviceOf(seg.span)
    criticalByService.set(name, (criticalByService.get(name) ?? 0) + (seg.to - seg.from))
  }

  const services = [...colorByService.entries()]
    .map(([name, color]) => {
      const owned = spans.filter((s) => serviceOf(s) === name)
      return {
        name,
        color,
        spanCount: owned.length,
        errorCount: owned.filter((s) => s.hasError).length,
        criticalMs: criticalByService.get(name) ?? 0,
      }
    })
    .sort((a, b) => b.criticalMs - a.criticalMs || b.spanCount - a.spanCount)

  const selfTimeMs = new Map<string, number>()
  for (const span of spans) {
    selfTimeMs.set(span.spanId, Math.max(0, span.durationMs - coveredMs(span, childrenByParent.get(span.spanId) ?? [])))
  }

  let maxDepth = 0
  const walk = (span: SpanItem, depth: number) => {
    maxDepth = Math.max(maxDepth, depth)
    for (const c of childrenByParent.get(span.spanId) ?? []) walk(c, depth + 1)
  }
  roots.forEach((r) => walk(r, 1))

  // Where things first went wrong: the earliest failing span with no failing children (its
  // ancestors are usually marked failed only because the error propagated up).
  const errorStarts = spans
    .filter((s) => s.hasError && !(childrenByParent.get(s.spanId) ?? []).some((c) => c.hasError))
    .map(startMs)
  return {
    spanById,
    childrenByParent,
    roots,
    criticalSpanIds,
    services,
    serviceColor: (span) => colorByService.get(serviceOf(span)) ?? SERVICE_COLORS[0],
    selfTimeMs,
    maxDepth,
    traceStart,
    firstErrorOffsetMs: errorStarts.length ? Math.min(...errorStarts) - traceStart : null,
  }
}

/** How much of the span's own interval its children cover (overlapping children counted once). */
function coveredMs(span: SpanItem, children: SpanItem[]): number {
  const from = startMs(span)
  const to = endMs(span)
  const intervals = children
    .map((c) => [Math.max(from, startMs(c)), Math.min(to, endMs(c))] as const)
    .filter(([a, b]) => b > a)
    .sort((x, y) => x[0] - y[0])
  let total = 0
  let curStart = -Infinity
  let curEnd = -Infinity
  for (const [a, b] of intervals) {
    if (a > curEnd) {
      if (curEnd > curStart) total += curEnd - curStart
      curStart = a
      curEnd = b
    } else curEnd = Math.max(curEnd, b)
  }
  if (curEnd > curStart) total += curEnd - curStart
  return total
}

/** Root first, the span itself last. */
export function pathToSpan(span: SpanItem, spanById: Map<string, SpanItem>): SpanItem[] {
  const path = [span]
  const seen = new Set([span.spanId])
  let parent = spanById.get(span.parentSpanId)
  while (parent && !seen.has(parent.spanId)) {
    path.unshift(parent)
    seen.add(parent.spanId)
    parent = spanById.get(parent.parentSpanId)
  }
  return path
}

export interface SpanException {
  type: string | null
  message: string | null
  offsetMs: number
  /** `exception.stacktrace`: the exception's full text (`ToString()` in .NET). */
  stackTrace: string | null
  /** The exception and its causes, outermost first — from `exception.details` (recorded by
   * `RecordExceptionWithSource`, or added by the exporter), else parsed from the stack trace
   * text (frames only, no source). Empty when neither is there. */
  chain: ExceptionInfo[]
}

export interface ExceptionFrame {
  method: string
  file: string | null
  line: number | null
  column?: number | null
  /** The app's own code (has a source location, not a framework assembly). */
  user: boolean
  /** Lines `startLine`… of the source around `line`. */
  snapshot?: { startLine: number; lines: string[] } | null
}

export interface ExceptionInfo {
  type: string
  message: string
  frames: ExceptionFrame[]
}

function parseDetails(v: unknown): ExceptionInfo[] | null {
  if (typeof v !== "string") return null
  try {
    const parsed = JSON.parse(v) as unknown
    return Array.isArray(parsed) && parsed.every((e) => e && typeof e === "object" && Array.isArray((e as ExceptionInfo).frames)) ? (parsed as ExceptionInfo[]) : null
  } catch {
    return null
  }
}

// "   at Ns.Type.Method(Args) in /src/App/File.cs:line 57"
const FRAME_LINE = /^\s*at (.+?)(?: in (.+):line (\d+))?\s*$/
const FRAMEWORK = /^(System|Microsoft|OpenTelemetry)\./

/** .NET stack trace text → the outermost exception's frames (what follows the last
 * "End of inner exception stack trace" marker). */
function parseStackTrace(type: string | null, message: string | null, text: string | null): ExceptionInfo[] {
  if (!text) return []
  let frames: ExceptionFrame[] = []
  for (const raw of text.split(/\r?\n/)) {
    if (raw.includes("--- End of inner exception stack trace ---")) {
      frames = []
      continue
    }
    const m = FRAME_LINE.exec(raw)
    if (!m) continue
    const method = m[1].replace(/\(.*$/, "")
    frames.push({ method, file: m[2] ?? null, line: m[3] ? Number(m[3]) : null, user: !!m[2] && !FRAMEWORK.test(method) })
  }
  return frames.length ? [{ type: type ?? "Exception", message: message ?? "", frames }] : []
}

/** The span's recorded exceptions (OpenTelemetry's `exception` event). */
export function spanExceptions(span: SpanItem): SpanException[] {
  return span.events
    .filter((e) => e.name === "exception")
    .map((e) => ({
      type: stringOrNull(e.attributes?.["exception.type"]),
      message: stringOrNull(e.attributes?.["exception.message"]),
      offsetMs: new Date(e.time).getTime() - startMs(span),
      stackTrace: stringOrNull(e.attributes?.["exception.stacktrace"]),
      chain:
        parseDetails(e.attributes?.["exception.details"]) ??
        parseStackTrace(stringOrNull(e.attributes?.["exception.type"]), stringOrNull(e.attributes?.["exception.message"]), stringOrNull(e.attributes?.["exception.stacktrace"])),
    }))
}

function stringOrNull(v: unknown): string | null {
  return v == null || v === "" ? null : String(v)
}
