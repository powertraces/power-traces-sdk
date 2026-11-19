import type { SpanItem, TraceDetail } from "@/lib/api"
import { formatDuration } from "@/lib/format"
import { grpcStatusText, httpStatusInfo, statusBlame } from "./glossary"
import { getHttpStatusFromSpan } from "./traceHelpers"
import { serviceOf, spanExceptions, type TraceAnalysis } from "./traceAnalysis"
import type { Signal, SoftFailure } from "./softFailures"

// The trace told as a story: which calls were made between whom (an exchange pairs the caller's
// CLIENT span with the receiver's SERVER span, wherever each recorded its half of the request and
// the reply), what went wrong first, and the path the request took through the services.

const startMs = (s: SpanItem) => new Date(s.startTime).getTime()
const endMs = (s: SpanItem) => startMs(s) + s.durationMs

const kindOf = (s: SpanItem) => (s.kind ?? "").toUpperCase().replace("SPAN_KIND_", "")
const isCallerKind = (s: SpanItem) => kindOf(s) === "CLIENT" || kindOf(s) === "PRODUCER"
const isReceiverKind = (s: SpanItem) => kindOf(s) === "SERVER" || kindOf(s) === "CONSUMER"

function attr(span: SpanItem | null | undefined, ...keys: string[]): string | null {
  if (!span) return null
  for (const k of keys) {
    const v = span.attributes[k]
    if (v != null && v !== "") return typeof v === "string" ? v : JSON.stringify(v)
  }
  return null
}

// ── Bodies and headers ──────────────────────────────────────────────────────

export interface RecordedValue {
  key: string
  text: string
  /** The span the value was recorded on. */
  span: SpanItem
}

const compact = (key: string) => key.toLowerCase().replace(/[^a-z]/g, "")

/** A request or response body recorded as an attribute — `http.request.body`, `request_body`,
 * `RequestBody`… whatever the app called it, as long as the key ends that way. */
function findBody(span: SpanItem | null, which: "request" | "response"): RecordedValue | null {
  if (!span) return null
  const suffix = which === "request" ? "requestbody" : "responsebody"
  for (const [key, value] of Object.entries(span.attributes)) {
    if (value == null || value === "") continue
    if (compact(key).endsWith(suffix)) return { key, text: typeof value === "string" ? value : JSON.stringify(value, null, 2), span }
  }
  return null
}

function findHeaders(span: SpanItem | null, which: "request" | "response"): RecordedValue[] {
  if (!span) return []
  const prefix = `http.${which}.header.`
  return Object.entries(span.attributes)
    .filter(([k, v]) => k.toLowerCase().startsWith(prefix) && v != null && v !== "")
    .map(([key, value]) => ({
      key: key.slice(prefix.length),
      text: Array.isArray(value) ? value.join(", ") : typeof value === "string" ? value : JSON.stringify(value),
      span,
    }))
}

/** The most human sentence inside a body: a JSON string value under a key like `message` or
 * `error`, or the body itself when it's short plain text. Used only to word the reason; nothing is
 * decided from it. */
export function readableMessage(text: string | null | undefined): string | null {
  if (!text) return null
  const trimmed = text.trim()
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return trimmed.length > 0 && trimmed.length <= 200 && !/[{}<>]/.test(trimmed) ? trimmed : null
  }
  let best: { text: string; score: number } | null = null
  const visit = (value: unknown, key: string, depth: number) => {
    if (depth > 6) return
    if (typeof value === "string") {
      const v = value.trim()
      if (v.length < 3 || v.length > 240 || /^https?:\/\//.test(v) || !/[a-z]/i.test(v)) return
      let score = 0
      if (/message|error|detail|reason|title|description|msg|text/i.test(key)) score += 2
      if (/\s/.test(v)) score += 1
      if (score >= 2 && (!best || score > best.score)) best = { text: v, score }
    } else if (Array.isArray(value)) {
      value.slice(0, 20).forEach((v) => visit(v, key, depth + 1))
    } else if (value && typeof value === "object") {
      for (const [k, v] of Object.entries(value)) visit(v, k, depth + 1)
    }
  }
  visit(parsed, "", 0)
  return (best as { text: string } | null)?.text ?? null
}

// ── Exchanges ───────────────────────────────────────────────────────────────

export type ExchangeKind = "http" | "rpc" | "db" | "messaging" | "call"

export interface Exchange {
  id: string
  kind: ExchangeKind
  /** The caller's half (CLIENT/PRODUCER), when the caller is traced. */
  client: SpanItem | null
  /** The receiver's half (SERVER/CONSUMER), when the receiver is traced. */
  server: SpanItem | null
  /** Who sent the request — a service name, or null when the caller isn't traced. */
  caller: string | null
  /** Who answered — a service name, or the peer (host, database) the caller named. */
  callee: string
  method: string | null
  target: string | null
  /** HTTP status, or the gRPC status code. */
  status: string | null
  statusText: string | null
  durationMs: number
  startMs: number
  endMs: number
  failed: boolean
  query: RecordedValue | null
  requestBody: RecordedValue | null
  responseBody: RecordedValue | null
  requestHeaders: RecordedValue[]
  responseHeaders: RecordedValue[]
  rows: string | null
}

function exchangeKind(span: SpanItem): ExchangeKind {
  const a = span.attributes
  if (a["db.system"] || a["db.system.name"]) return "db"
  if (a["rpc.system"]) return "rpc"
  if (a["messaging.system"]) return "messaging"
  if (a["http.request.method"] || a["http.method"] || a["url.full"] || a["http.url"] || a["http.route"]) return "http"
  return "call"
}

function hostOf(url: string | null): string | null {
  if (!url) return null
  try {
    return new URL(url).host || null
  } catch {
    return null
  }
}

/** What a caller span says it called: the service, host, or database. */
export function peerName(span: SpanItem): string {
  const db = attr(span, "db.system.name", "db.system")
  if (db) {
    const name = attr(span, "db.namespace", "db.name")
    return name ? `${dbLabel(db)} · ${name}` : dbLabel(db)
  }
  return (
    attr(span, "peer.service", "rpc.service", "messaging.destination.name", "server.address", "net.peer.name") ??
    hostOf(attr(span, "url.full", "http.url")) ??
    "an outside service"
  )
}

const DB_LABELS: Record<string, string> = {
  postgresql: "PostgreSQL",
  mssql: "SQL Server",
  "microsoft.sql_server": "SQL Server",
  mysql: "MySQL",
  mariadb: "MariaDB",
  sqlite: "SQLite",
  mongodb: "MongoDB",
  redis: "Redis",
  oracle: "Oracle",
  elasticsearch: "Elasticsearch",
  cosmosdb: "Cosmos DB",
}
const dbLabel = (system: string) => DB_LABELS[system.toLowerCase()] ?? system

function targetOf(span: SpanItem, kind: ExchangeKind): string | null {
  if (kind === "db") {
    const op = attr(span, "db.operation.name", "db.operation")
    const table = attr(span, "db.collection.name", "db.sql.table", "db.mongodb.collection")
    return [op, table].filter(Boolean).join(" ") || null
  }
  if (kind === "rpc") return [attr(span, "rpc.service"), attr(span, "rpc.method")].filter(Boolean).join("/") || null
  if (kind === "messaging") return attr(span, "messaging.destination.name")
  return attr(span, "http.route", "url.full", "http.url", "url.path", "http.target")
}

function statusOf(span: SpanItem | null): string | null {
  if (!span) return null
  return getHttpStatusFromSpan(span) ?? attr(span, "rpc.grpc.status_code")
}

function makeExchange(client: SpanItem | null, server: SpanItem | null, spanById: Map<string, SpanItem>): Exchange {
  const main = (client ?? server)!
  const kind = exchangeKind(client ?? main) === "call" && server ? exchangeKind(server) : exchangeKind(client ?? main)
  const status = statusOf(server) ?? statusOf(client)
  const isGrpc = status != null && !getHttpStatusFromSpan(server ?? main) && !getHttpStatusFromSpan(client ?? main)
  const statusText = status == null ? null : isGrpc ? grpcStatusText(status) : (httpStatusInfo(status)?.phrase ?? null)
  const parentOfServer = server && !client ? spanById.get(server.parentSpanId) : undefined
  const from = Math.min(...[client, server].filter((s): s is SpanItem => !!s).map(startMs))
  const to = Math.max(...[client, server].filter((s): s is SpanItem => !!s).map(endMs))
  return {
    id: main.spanId,
    kind,
    client,
    server,
    caller: client ? serviceOf(client) : parentOfServer ? serviceOf(parentOfServer) : null,
    callee: server ? serviceOf(server) : peerName(main),
    method: attr(client, "http.request.method", "http.method", "rpc.method") ?? attr(server, "http.request.method", "http.method", "rpc.method"),
    // The receiver's route ("/charges") reads better than the caller's full URL.
    target: (server ? targetOf(server, kind) : null) ?? targetOf(client ?? main, kind),
    status,
    statusText,
    durationMs: client?.durationMs ?? server!.durationMs,
    startMs: from,
    endMs: to,
    failed: !!(client?.hasError || server?.hasError),
    query: (() => {
      const s = client ?? server
      const key = s && ["db.query.text", "db.statement"].find((k) => s.attributes[k] != null && s.attributes[k] !== "")
      return s && key ? { key, text: String(s.attributes[key]), span: s } : null
    })(),
    requestBody: findBody(client, "request") ?? findBody(server, "request"),
    responseBody: findBody(server, "response") ?? findBody(client, "response"),
    requestHeaders: [...findHeaders(client, "request"), ...findHeaders(server, "request").filter((h) => !findHeaders(client, "request").some((c) => c.key === h.key))],
    responseHeaders: [...findHeaders(server, "response"), ...findHeaders(client, "response").filter((h) => !findHeaders(server, "response").some((c) => c.key === h.key))],
    rows: attr(client ?? server, "db.response.returned_rows"),
  }
}

/** Every call in the trace: a caller span paired with the receiver span under it (context
 * propagation makes the receiver a child of the caller), plus calls whose other side isn't traced. */
export function buildExchanges(trace: TraceDetail, analysis: TraceAnalysis): Map<string, Exchange> {
  const bySpan = new Map<string, Exchange>()
  for (const span of trace.spans) {
    if (!isCallerKind(span)) continue
    const method = attr(span, "http.request.method", "http.method")
    const receivers = (analysis.childrenByParent.get(span.spanId) ?? []).filter(isReceiverKind)
    const server = receivers.find((r) => !method || attr(r, "http.request.method", "http.method") === method) ?? receivers[0] ?? null
    const ex = makeExchange(span, server, analysis.spanById)
    bySpan.set(span.spanId, ex)
    if (server) bySpan.set(server.spanId, ex)
  }
  for (const span of trace.spans) {
    if (isReceiverKind(span) && !bySpan.has(span.spanId)) bySpan.set(span.spanId, makeExchange(null, span, analysis.spanById))
  }
  return bySpan
}

/** The other half of a span's exchange, if both halves are traced. */
export function partnerOf(span: SpanItem, exchange: Exchange | undefined): SpanItem | null {
  if (!exchange?.client || !exchange.server) return null
  return exchange.client.spanId === span.spanId ? exchange.server : exchange.client
}

// ── What went wrong ─────────────────────────────────────────────────────────

/** Where things first went wrong: failing spans with nothing failing anywhere below them,
 * earliest first. Their ancestors are usually marked failed only because the error propagated up —
 * and not always through every level: a server answering 4xx isn't an error on its side, while
 * the client span above it is, so the check looks at all descendants, not just children. */
export function rootCauses(trace: TraceDetail, analysis: TraceAnalysis): SpanItem[] {
  const failsBelow = new Map<string, boolean>()
  const check = (id: string, seen: Set<string>): boolean => {
    if (failsBelow.has(id)) return failsBelow.get(id)!
    seen.add(id)
    const result = (analysis.childrenByParent.get(id) ?? []).some((c) => !seen.has(c.spanId) && (c.hasError || check(c.spanId, seen)))
    failsBelow.set(id, result)
    return result
  }
  return trace.spans.filter((s) => s.hasError && !check(s.spanId, new Set())).sort((a, b) => startMs(a) - startMs(b))
}

/** Why a span failed, in the app's own words when it gave any. */
export function failureReason(span: SpanItem, exchange?: Exchange): string | null {
  const spans = [span, ...(exchange ? [exchange.server, exchange.client] : [])].filter((s): s is SpanItem => !!s)
  for (const s of spans) {
    const ex = spanExceptions(s).find((e) => e.message)
    if (ex?.message) return clip(ex.message)
  }
  for (const s of spans) if (s.statusMessage) return clip(s.statusMessage)
  const fromBody = readableMessage(exchange?.responseBody?.text)
  if (fromBody) return clip(fromBody)
  const status = exchange?.status ?? statusOf(span)
  return httpStatusInfo(status)?.meaning ?? null
}

const clip = (text: string, max = 220) => {
  const one = text.replace(/\s+/g, " ").trim()
  return one.length > max ? `${one.slice(0, max - 1)}…` : one
}

/** `soft`: nothing reported an error, but a call that answered with a success status didn't work. */
export type Outcome = "ok" | "failed" | "soft"

export interface Verdict {
  outcome: Outcome
  /** The step everyone should look at first. */
  focus: SpanItem | null
  /** Who's responsible — a service, database or outside host. */
  culprit: string | null
  /** What the culprit did, as a verb phrase ("refused the request"). */
  happened: string | null
  reason: string | null
  exchange: Exchange | null
  /** One or two sentences of context after the headline. */
  details: string[]
  /** Set when the culprit answered with a success status: why we think it failed anyway. */
  soft: SoftFailure | null
}

/** "answered 200 OK" for a success status, or just "answered" when none was recorded. */
const answeredWith = (ex: Exchange) => (ex.status ? `answered ${ex.status}${ex.statusText ? ` ${ex.statusText}` : ""}` : "answered")

const signalSummary = (signals: Signal[]) => joinNames(signals.filter((s) => s.strength === "strong").map((s) => s.short))

function describeFailure(span: SpanItem, exchange: Exchange | undefined): { culprit: string; happened: string } {
  if (exchange) {
    const blame = statusBlame(exchange.status)
    const answered = exchange.server != null || exchange.status != null
    if (exchange.kind === "db") return { culprit: exchange.callee, happened: "returned an error" }
    if (!answered) return { culprit: exchange.callee, happened: "didn't answer" }
    if (blame === "request") return { culprit: exchange.callee, happened: "refused the request" }
    return { culprit: exchange.callee, happened: "ran into an error" }
  }
  return { culprit: serviceOf(span), happened: `failed while doing “${span.name}”` }
}

/** Joins names as "a", "a and b", "a, b and c". */
export function joinNames(names: string[]): string {
  if (names.length <= 1) return names[0] ?? ""
  return `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`
}

export function buildVerdict(trace: TraceDetail, analysis: TraceAnalysis, exchanges: Map<string, Exchange>, softFailures: Map<string, SoftFailure> = new Map()): Verdict {
  const causes = rootCauses(trace, analysis)
  const firstCause = causes[0] ?? null
  const softList = [...softFailures.values()].sort((a, b) => a.exchange.startMs - b.exchange.startMs)

  // A step that failed right after a call answered "OK" failed because of that reply: blame the
  // service that sent it.
  const softBehindCause = firstCause && softList.find((sf) => (sf.exchange.client ?? sf.exchange.server)!.parentSpanId === firstCause.spanId)
  const soft = softBehindCause ?? (firstCause ? null : (softList[0] ?? null))
  if (soft) {
    const ex = soft.exchange
    const details = [
      `${ex.caller ?? "The caller"} asked ${ex.callee} to handle ${[ex.method, ex.target].filter(Boolean).join(" ") || "a request"}, and the status code said it worked.`,
      `We think it didn’t because ${signalSummary(soft.signals)}.`,
    ]
    return {
      outcome: firstCause ? "failed" : "soft",
      focus: ex.client ?? ex.server,
      culprit: ex.callee,
      happened: `${answeredWith(ex)}, but the reply meant failure`,
      reason: soft.reason ? clip(soft.reason) : null,
      exchange: ex,
      details,
      soft,
    }
  }

  const focus = firstCause
  if (!focus) {
    const total = analysis.services.reduce((sum, s) => sum + s.criticalMs, 0)
    const top = analysis.services[0]
    const details = [`Finished in ${formatDuration(trace.durationMs)} across ${analysis.services.length} ${analysis.services.length === 1 ? "service" : "services"}.`]
    if (top && total > 0 && analysis.services.length > 1) details.push(`Most of the time (${Math.round((top.criticalMs / total) * 100)}%) was spent in ${top.name}.`)
    return { outcome: "ok", focus: null, culprit: null, happened: null, reason: null, exchange: null, details, soft: null }
  }

  const exchange = exchanges.get(focus.spanId)
  const { culprit, happened } = describeFailure(focus, exchange)
  const reason = failureReason(focus, exchange)
  const details: string[] = []

  // The services the request passed through before reaching the culprit, which worked.
  const firstStart = new Map<string, number>()
  for (const s of trace.spans) {
    const name = serviceOf(s)
    firstStart.set(name, Math.min(firstStart.get(name) ?? Infinity, startMs(s)))
  }
  const failingServices = new Set(causes.map(serviceOf))
  const before = [...firstStart.entries()]
    .filter(([name, at]) => at <= startMs(focus) && name !== culprit && !failingServices.has(name))
    .sort((a, b) => a[1] - b[1])
    .map(([name]) => name)
  if (before.length) details.push(`The request passed through ${joinNames(before)} without problems.`)

  if (exchange && exchange.caller) {
    const what = exchange.kind === "db" ? "to run a query" : `to handle ${[exchange.method, exchange.target].filter(Boolean).join(" ") || "a request"}`
    const answer = exchange.status
      ? `${exchange.callee} answered ${exchange.status}${exchange.statusText ? ` ${exchange.statusText}` : ""} after ${formatDuration(exchange.durationMs)}.`
      : `After ${formatDuration(exchange.durationMs)} it gave up.`
    details.push(`${exchange.caller} asked ${exchange.callee} ${what}. ${answer}`)
  }
  if (causes.length > 1) details.push(`${causes.length - 1} other ${causes.length === 2 ? "step" : "steps"} failed too.`)

  return { outcome: "failed", focus, culprit, happened, reason, exchange: exchange ?? null, details, soft: null }
}

/** The verdict as one line of text, for copying into a chat or ticket. */
export function verdictText(trace: TraceDetail, verdict: Verdict): string {
  const name = trace.rootName ?? "The request"
  const why = `${verdict.culprit} ${verdict.happened}${verdict.reason ? `: ${verdict.reason}` : ""}`
  const head = verdict.outcome === "ok" ? `${name} worked.` : verdict.outcome === "soft" ? `${name} probably didn't work: ${why}.` : `${name} failed because ${why}.`
  return [head, ...verdict.details].join(" ")
}

// ── The journey ─────────────────────────────────────────────────────────────

export type StepState = "ok" | "problem" | "soft" | "affected"

export interface JourneyStep {
  key: string
  name: string
  /** A database or outside service the trace only saw from the caller's side. */
  external: boolean
  state: StepState
  note: string
  /** The span to open when the step is clicked. */
  spanId: string
}

/** The services (and untraced peers) in the order the request reached them. */
export function buildJourney(trace: TraceDetail, exchanges: Map<string, Exchange>, verdict: Verdict): JourneyStep[] {
  const steps = new Map<string, JourneyStep & { at: number; spans: SpanItem[] }>()

  for (const span of [...trace.spans].sort((a, b) => startMs(a) - startMs(b))) {
    const name = serviceOf(span)
    const key = `svc:${name}`
    if (!steps.has(key)) steps.set(key, { key, name, external: false, state: "ok", note: "", spanId: span.spanId, at: startMs(span), spans: [] })
    steps.get(key)!.spans.push(span)

    const ex = exchanges.get(span.spanId)
    if (ex && ex.client?.spanId === span.spanId && !ex.server) {
      const peerKey = `peer:${ex.callee}`
      if (!steps.has(peerKey)) steps.set(peerKey, { key: peerKey, name: ex.callee, external: true, state: "ok", note: "", spanId: span.spanId, at: startMs(span) + 0.001, spans: [] })
      steps.get(peerKey)!.spans.push(span)
    }
  }

  // The step to blame is whoever answered the failing call (or, for a failure inside a service,
  // that service) — not the caller that recorded the error.
  const ex = verdict.exchange
  const culpritKey =
    verdict.outcome === "ok" || !verdict.focus
      ? null
      : ex?.server
        ? `svc:${serviceOf(ex.server)}`
        : ex
          ? `peer:${ex.callee}`
          : `svc:${serviceOf(verdict.focus)}`
  for (const step of steps.values()) {
    const owned = step.spans
    const failing = owned.filter((s) => s.hasError)
    if (step.key === culpritKey) {
      const status = verdict.exchange?.status
      step.state = verdict.soft ? "soft" : "problem"
      step.note = verdict.soft ? `Said ${status ?? "OK"}, but failed` : status ? `Problem here · ${status}` : "Problem here"
      step.spanId = verdict.focus?.spanId ?? step.spanId
    } else if (failing.length > 0) {
      step.state = "affected"
      step.note = "Passed the error on"
      step.spanId = failing[0].spanId
    } else {
      // Time in the service: its spans that aren't nested in another of its own spans.
      const ids = new Set(owned.map((s) => s.spanId))
      const ms = step.external ? Math.max(...owned.map((s) => s.durationMs)) : owned.filter((s) => !ids.has(s.parentSpanId)).reduce((sum, s) => sum + s.durationMs, 0)
      step.note = `Worked · ${formatDuration(ms)}`
    }
  }

  return [...steps.values()].sort((a, b) => a.at - b.at).map(({ key, name, external, state, note, spanId }) => ({ key, name, external, state, note, spanId }))
}

// ── One span, in a sentence ─────────────────────────────────────────────────

/** What a span did, for the details pane: who asked whom for what, and how it ended. */
export function describeSpan(span: SpanItem, exchange: Exchange | undefined, soft?: SoftFailure | null): string {
  const base = describeCall(span, exchange, !!soft)
  return soft ? `${base} The status code says it worked, but it didn’t: ${signalSummary(soft.signals)}.` : base
}

function describeCall(span: SpanItem, exchange: Exchange | undefined, soft: boolean): string {
  const took = formatDuration(span.durationMs)
  if (exchange) {
    const caller = exchange.caller ?? "A caller"
    const what = [exchange.method, exchange.target].filter(Boolean).join(" ")
    if (exchange.kind === "db") {
      const result = exchange.failed ? "It returned an error" : exchange.rows != null ? `It returned ${exchange.rows} ${exchange.rows === "1" ? "row" : "rows"}` : "It answered"
      return `${caller} asked the database (${exchange.callee}) to run ${what ? `a ${what} query` : "a query"}. ${result} after ${formatDuration(exchange.durationMs)}.`
    }
    const asked = `${caller} sent ${what ? `${what} ` : "a request "}to ${exchange.callee}.`
    if (exchange.status) {
      const info = httpStatusInfo(exchange.status)
      const meaning = info && exchange.kind === "http" && !soft ? `: ${info.meaning}` : ""
      return `${asked} ${exchange.callee} answered ${exchange.status}${exchange.statusText ? ` ${exchange.statusText}` : ""}${meaning}, after ${formatDuration(exchange.durationMs)}.`
    }
    if (exchange.failed) return `${asked} No answer came back; it failed after ${formatDuration(exchange.durationMs)}.`
    return `${asked} It took ${formatDuration(exchange.durationMs)}.`
  }
  return `Work inside ${serviceOf(span)}: “${span.name}”. ${span.hasError ? `It failed after ${took}.` : `It took ${took}.`}`
}
