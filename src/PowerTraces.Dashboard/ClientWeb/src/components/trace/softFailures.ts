import type { LogListItem, ReplyComparisonDto, SpanItem, TraceBaseline } from "@/lib/api"
import { formatDuration } from "@/lib/format"
import { readableMessage, type Exchange } from "./story"
import { spanExceptions, type TraceAnalysis } from "./traceAnalysis"

// Calls that answered with a success status but didn't work. Nothing here looks for particular
// fields in the reply: the evidence is what the trace shows happened around the call — the caller
// failing or complaining right after, or sending the same request again — plus, when a baseline
// is supplied, how the reply compares with this call's usual successful replies.

export type Strength = "strong" | "weak"

export interface Signal {
  id: string
  strength: Strength
  /** The full sentence for the evidence list. */
  text: string
  /** A short clause for the summary ("the caller logged a warning right after"). */
  short: string
}

export interface SoftFailure {
  exchange: Exchange
  signals: Signal[]
  /** Checks that ran and found nothing, so the decision isn't a black box. */
  notSeen: string[]
  /** The best wording of what went wrong, from the app or the reply. */
  reason: string | null
}

/** The comparison of an exchange's recorded reply with the usual ones, if the baseline has it. */
export function comparisonFor(ex: Exchange | undefined, baseline: TraceBaseline | null | undefined): ReplyComparisonDto | null {
  if (!ex || !baseline) return null
  return baseline.replies.find((r) => r.spanId === ex.server?.spanId || r.spanId === ex.client?.spanId) ?? null
}

/** Enough usual replies to say anything about this one's shape. */
export const MIN_COMPARISON_SAMPLE = 5

/** How soon after a reply a reaction still counts as a reaction to it. */
const REACTION_WINDOW_MS = 3_000
const WARN_SEVERITY = 13

const startMs = (s: SpanItem) => new Date(s.startTime).getTime()
const endMs = (s: SpanItem) => startMs(s) + s.durationMs

function isSuccessStatus(ex: Exchange): boolean {
  if (ex.failed) return false
  if (ex.status == null) return true
  const code = Number(ex.status)
  if (!Number.isFinite(code)) return true
  // gRPC 0 is OK; HTTP 1xx–3xx.
  return ex.kind === "rpc" ? code === 0 : code < 400
}

const quote = (text: string, max = 140) => {
  const one = text.replace(/\s+/g, " ").trim()
  return `“${one.length > max ? `${one.slice(0, max - 1)}…` : one}”`
}

export function findSoftFailures(
  analysis: TraceAnalysis,
  exchanges: Map<string, Exchange>,
  logs: LogListItem[],
  baseline?: TraceBaseline | null,
): Map<string, SoftFailure> {
  const result = new Map<string, SoftFailure>()
  const unique = [...new Set(exchanges.values())]
  const warnLogs = logs.filter((l) => l.severityNumber >= WARN_SEVERITY).map((l) => ({ log: l, at: new Date(l.timestamp).getTime() }))

  for (const ex of unique) {
    if (!isSuccessStatus(ex) || ex.kind === "messaging") continue
    const cmp = comparisonFor(ex, baseline)
    // The span in the caller that made the call: the client span's parent, or the span above an
    // untraced caller's server span.
    const callSpan = ex.client ?? ex.server!
    const caller = analysis.spanById.get(callSpan.parentSpanId) ?? null
    const siblings = caller ? (analysis.childrenByParent.get(caller.spanId) ?? []) : []
    const nextCall = siblings.filter((s) => startMs(s) >= ex.endMs && s.spanId !== callSpan.spanId).sort((a, b) => startMs(a) - startMs(b))[0]
    const windowEnd = Math.min(ex.endMs + REACTION_WINDOW_MS, nextCall ? startMs(nextCall) : Infinity)
    const who = ex.caller ?? "The caller"
    const signals: Signal[] = []
    const notSeen: string[] = []
    let reason: string | null = null

    // 1. The caller failed right after the reply — by its own word (MarkFailed) or by throwing.
    if (caller?.hasError) {
      const failingBelow = siblings.some((s) => s.hasError)
      const marked = caller.attributes["powertraces.failure.reason"]
      const thrown = spanExceptions(caller).find((e) => {
        const at = startMs(caller) + e.offsetMs
        return at >= ex.endMs - 1 && at <= windowEnd
      })
      const lastCallBeforeEnd = siblings.filter((s) => endMs(s) <= endMs(caller) + 1).sort((a, b) => endMs(b) - endMs(a))[0]
      if (!failingBelow && marked != null && lastCallBeforeEnd?.spanId === callSpan.spanId) {
        reason = String(marked)
        signals.push({
          id: "caller-marked",
          strength: "strong",
          text: `${who} marked its step failed after reading this reply: ${quote(String(marked))}.`,
          short: `${who} marked the step failed after reading it`,
        })
      } else if (!failingBelow && thrown) {
        reason = thrown.message
        const after = startMs(caller) + thrown.offsetMs - ex.endMs
        signals.push({
          id: "caller-threw",
          strength: "strong",
          text: `${who} threw ${thrown.type ?? "an exception"} ${formatDuration(Math.max(0, after))} after the reply${thrown.message ? `: ${quote(thrown.message)}` : ""}.`,
          short: `${who} threw an error right after`,
        })
      }
    }
    if (!signals.some((s) => s.id.startsWith("caller-"))) notSeen.push(`${who} didn’t fail or throw right after`)

    // 2. The caller logged a warning or an error right after the reply.
    const callerIds = new Set([callSpan.spanId, caller?.spanId].filter(Boolean) as string[])
    const complaint = warnLogs.find(({ log, at }) => at >= ex.endMs - 1 && at <= windowEnd && log.spanId != null && callerIds.has(log.spanId))
    if (complaint) {
      const level = complaint.log.severityNumber >= 17 ? "an error" : "a warning"
      reason ??= complaint.log.body
      signals.push({
        id: "caller-logged",
        strength: "strong",
        text: `${who} logged ${level} ${formatDuration(Math.max(0, complaint.at - ex.endMs))} after the reply${complaint.log.body ? `: ${quote(complaint.log.body)}` : ""}.`,
        short: `${who} logged ${level} right after`,
      })
    } else notSeen.push("No warning or error was logged right after")

    // 3. The same request was sent again right after.
    const retry = unique.find(
      (other) =>
        other !== ex &&
        other.startMs >= ex.endMs - 1 &&
        other.startMs <= ex.endMs + REACTION_WINDOW_MS &&
        other.caller === ex.caller &&
        other.callee === ex.callee &&
        other.method === ex.method &&
        other.target === ex.target &&
        (other.client ?? other.server)!.parentSpanId === callSpan.parentSpanId,
    )
    if (retry) {
      signals.push({
        id: "retried",
        strength: "strong",
        text: `The same request was sent again ${formatDuration(Math.max(0, retry.startMs - ex.endMs))} later — a retry.`,
        short: "the same request was sent again right after",
      })
    } else notSeen.push("No retry of the same request")

    // 4. Compared with this call's usual successful replies.
    if (cmp && cmp.sampleSize >= MIN_COMPARISON_SAMPLE) {
      if (cmp.missing.length > 0 && cmp.sameShapeShare < 0.2) {
        const usual = Math.round((1 - cmp.sameShapeShare) * 100)
        signals.push({
          id: "unusual-shape",
          strength: "strong",
          text: `The reply’s shape differs from ${usual}% of ${cmp.sampleSize} usual successful replies: ${cmp.missing.length} usual ${cmp.missing.length === 1 ? "field is" : "fields are"} missing.`,
          short: "the reply looks nothing like a normal success",
        })
      } else notSeen.push("The reply looks like the usual successful ones")
      if (cmp.bytes != null && cmp.usualBytes != null && cmp.usualBytes > 0 && cmp.bytes < cmp.usualBytes * 0.5) {
        signals.push({
          id: "smaller",
          strength: "weak",
          text: `The reply is much smaller than usual (${cmp.bytes} vs about ${cmp.usualBytes} bytes).`,
          short: "the reply is much smaller than usual",
        })
      }
    }
    if (cmp?.verdict === "failure") {
      signals.push({ id: "confirmed", strength: "strong", text: "Someone confirmed that replies like this one mean failure.", short: "replies like this were confirmed as failures" })
    }

    // Someone said replies like this are fine: only the app's own word overrides that.
    const fine = cmp?.verdict === "fine" && !signals.some((s) => s.id === "caller-marked")
    if (!fine && signals.some((s) => s.strength === "strong")) {
      reason ??= readableMessage(ex.responseBody?.text) ?? (cmp ? textInNewFields(ex.responseBody?.text, cmp.added) : null)
      result.set(ex.id, { exchange: ex, signals, notSeen, reason })
    }
  }

  // 5. The trace stopped early: steps that usually follow never ran. Attached to the last
  // successful call before the gap, when something else already points at it.
  if (baseline && baseline.missingSteps.length > 0 && result.size > 0) {
    const names = baseline.missingSteps.slice(0, 3).map((s) => `“${s.name}”`)
    for (const soft of result.values()) {
      soft.signals.push({
        id: "stopped-early",
        strength: "strong",
        text: `The trace stopped early: ${names.join(", ")} usually ${baseline.missingSteps.length === 1 ? "runs" : "run"} after this and didn’t.`,
        short: "the steps that usually follow never ran",
      })
    }
  }

  return result
}

/** The soft failure that best explains the trace: the earliest one. */
export function primarySoftFailure(soft: Map<string, SoftFailure>): SoftFailure | null {
  return [...soft.values()].sort((a, b) => a.exchange.startMs - b.exchange.startMs)[0] ?? null
}

export const softFailureOf = (soft: Map<string, SoftFailure>, ex: Exchange | undefined) => (ex ? (soft.get(ex.id) ?? null) : null)


/** The most sentence-like text in the reply's fields that the usual replies don't have — where
 * an unusual reply explains itself, whatever those fields are called. */
function textInNewFields(body: string | null | undefined, added: string[]): string | null {
  if (!body) return null
  let parsed: unknown
  try {
    parsed = JSON.parse(body)
  } catch {
    return null
  }
  const texts = added
    .filter((p) => p.endsWith(":string"))
    .map((p) => p.slice(0, -":string".length).split("."))
    .map((parts) =>
      parts.reduce<unknown>((value, part) => {
        if (value == null) return null
        const key = part.endsWith("[]") ? part.slice(0, -2) : part
        const next = (value as Record<string, unknown>)[key]
        return part.endsWith("[]") && Array.isArray(next) ? next[0] : next
      }, parsed),
    )
    .filter((v): v is string => typeof v === "string" && /[a-z]/i.test(v) && v.length <= 240)
  // Words beat codes: "Balance too low" over "E-1042".
  return texts.sort((a, b) => Number(/\s/.test(b)) - Number(/\s/.test(a)) || b.length - a.length)[0] ?? null
}
