import axios from "axios"

// Deliberately relative (not "/"): requests must resolve against the page's <base href>, which
// PowerTracesDashboardEndpointRouteBuilderExtensions sets to wherever the host mounted the dashboard.
export const api = axios.create({ baseURL: "" })

/** Every read takes React Query's abort signal (functions passed directly as `queryFn` receive it
 * through the query context, which has a `signal` property). When a query is superseded — a new
 * keystroke, a changed filter, leaving the page — the browser aborts the request, ASP.NET cancels
 * the request's token, and the SQL command is cancelled, returning its connection to the pool
 * instead of holding it until the query finishes. */
export interface RequestOptions {
  signal?: AbortSignal
}

export interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
  /** The backend stopped counting at a limit — `totalCount` is a lower bound ("10,000+"). */
  totalCountCapped?: boolean
}

export interface LogListItem {
  id: number
  timestamp: string
  severityNumber: number
  severityText: string | null
  body: string | null
  serviceName: string | null
  traceId: string | null
  spanId: string | null
  attributes: Record<string, unknown>
}

export interface LogDetail extends LogListItem {
  observedTimestamp: string | null
  scopeLibraryName: string | null
  scopeLibraryVersion: string | null
  schemaUrl: string | null
  attributes: Record<string, unknown>
}

/** Why a trace is in search results: the field that matched, and where in its (possibly
 * excerpted) value. */
export interface TraceMatch {
  key: string
  value: string
  highlightStart: number
  highlightLength: number
}

export interface TraceListItem {
  traceId: string
  rootName: string | null
  serviceName: string | null
  startTime: string
  durationMs: number
  hasError: boolean
  spanCount: number
  attributes: Record<string, unknown>
  match?: TraceMatch | null
}

export interface SpanEvent {
  name: string
  time: string
  attributes: Record<string, unknown>
}

export interface SpanLink {
  traceId: string
  spanId: string
  attributes: Record<string, unknown>
}

export interface SpanItem {
  spanId: string
  parentSpanId: string
  name: string
  serviceName: string | null
  kind: string | null
  startTime: string
  endTime: string | null
  durationMs: number
  status: string | null
  statusMessage: string | null
  hasError: boolean
  httpStatus: string | null
  attributes: Record<string, unknown>
  events: SpanEvent[]
  links: SpanLink[]
}

export interface TraceDetail {
  traceId: string
  rootName: string | null
  serviceName: string | null
  startTime: string
  endTime: string
  durationMs: number
  hasError: boolean
  spans: SpanItem[]
  /** Kept out of retention (see `pinTrace`). */
  isPinned: boolean
}

export interface DashboardSummary {
  logsLast24h: number
  tracesLast24h: number
  errorTracesLast24h: number
  errorRatePercent: number
  recentLogs: LogListItem[]
  recentTraces: TraceListItem[]
}

export const getDashboard = (opts?: RequestOptions) => api.get<DashboardSummary>("api/dashboard", { signal: opts?.signal }).then((r) => r.data)

export interface TimeBucket {
  start: string
  traces: number
  errorTraces: number
  avgDurationMs: number
  logs: number
  warningLogs: number
  errorLogs: number
}

export interface OperationStats {
  name: string
  serviceName: string | null
  count: number
  errorCount: number
  avgDurationMs: number
  maxDurationMs: number
}

export interface DashboardOverview {
  from: string
  to: string
  bucketSeconds: number
  buckets: TimeBucket[]
  topOperations: OperationStats[]
}

export type OverviewRange = "1h" | "6h" | "24h" | "7d"

export const getOverview = (range: OverviewRange, buckets: number, opts?: RequestOptions) =>
  api.get<DashboardOverview>("api/overview", { params: { range, buckets }, signal: opts?.signal }).then((r) => r.data)

/** One attribute constraint — mirrors the backend's `PowerTraces.Dashboard.Query.AttributeFilter`.
 * A row must satisfy every filter in a list (AND), each independently equals or contains. */
export interface AttributeFilter {
  key: string
  value: string
  contains: boolean
}

/** The backend takes the whole filter list as one JSON-encoded `attributes` query param (see
 * `QueryEndpoints.ParseAttributeFilters`) rather than several parallel array params. */
function toQueryParams<T extends { attributes?: AttributeFilter[] }>({ attributes, ...rest }: T) {
  return { ...rest, attributes: attributes && attributes.length > 0 ? JSON.stringify(attributes) : undefined }
}

export interface LogFilters {
  from?: string
  to?: string
  service?: string
  severity?: number
  search?: string
  attributes?: AttributeFilter[]
  page?: number
  pageSize?: number
}

export const getLogs = (filters: LogFilters, opts?: RequestOptions) =>
  api.get<PagedResult<LogListItem>>("api/logs", { params: toQueryParams(filters), signal: opts?.signal }).then((r) => r.data)

/** Mirrors the backend's `LogSearchQuery` (see `QueryEndpoints.ParseLogSearch`). Each `text`
 * entry is one condition on the message (contains it; `-text` must not). Severities are levels by
 * their lowest number: 1 Trace, 5 Debug, 9 Info, 13 Warn, 17 Error, 21 Fatal, 0 unspecified. */
export interface LogSearch {
  text?: string[]
  range?: string
  from?: string
  to?: string
  services?: string[]
  severities?: number[]
  filters?: FieldFilter[]
  page?: number
  pageSize?: number
}

function logSearchParams(s: LogSearch): URLSearchParams {
  const p = new URLSearchParams()
  const set = (k: string, v: string | number | undefined) => {
    if (v !== undefined && v !== "") p.set(k, String(v))
  }
  s.text?.forEach((t) => t.trim() && p.append("q", t.trim()))
  if (!s.from && !s.to) set("range", s.range)
  set("from", s.from)
  set("to", s.to)
  s.services?.forEach((v) => p.append("service", v))
  s.severities?.forEach((v) => p.append("severity", String(v)))
  if (s.filters?.length) p.set("filters", JSON.stringify(s.filters))
  set("page", s.page)
  set("pageSize", s.pageSize)
  return p
}

export const searchLogs = (search: LogSearch, opts?: RequestOptions) =>
  api.get<PagedResult<LogListItem>>("api/logs/search", { params: logSearchParams(search), signal: opts?.signal }).then((r) => r.data)

/** Counts over the newest matches, each facet without its own selection (`sampled` when there
 * were more than the backend looked at). */
export interface LogFacets {
  severities: { severity: number; count: number }[]
  services: FacetValue[]
  sampled: boolean
}

export const getLogFacets = (search: LogSearch, opts?: RequestOptions) =>
  api.get<LogFacets>("api/logs/facets", { params: logSearchParams(search), signal: opts?.signal }).then((r) => r.data)

export const getLog = (id: number, opts?: RequestOptions) => api.get<LogDetail>(`api/logs/${id}`, { signal: opts?.signal }).then((r) => r.data)

export interface TraceFilters {
  from?: string
  to?: string
  service?: string
  hasError?: boolean
  search?: string
  attributes?: AttributeFilter[]
  page?: number
  pageSize?: number
}

export const getTraces = (filters: TraceFilters, opts?: RequestOptions) =>
  api.get<PagedResult<TraceListItem>>("api/traces", { params: toQueryParams(filters), signal: opts?.signal }).then((r) => r.data)

/** Mirrors the backend's `FieldMatch`: `Equals` (whole value, case-insensitive), `NotEquals`, or
 * `HasWord` (the trace has the field, and each word). */
export type FieldMatch = "Equals" | "NotEquals" | "HasWord"

/** One field condition of a trace search — `key` is an attribute key, `span.name`, or a path inside
 * a JSON attribute such as `http.request.body.customer.email`. */
export interface FieldFilter {
  key: string
  value: string
  match: FieldMatch
}

/** Mirrors the backend's `TraceSearchQuery` (see `QueryEndpoints.ParseTraceSearch`). */
export interface TraceSearch {
  q?: string
  range?: string
  from?: string
  to?: string
  endpoints?: string[]
  services?: string[]
  errors?: boolean
  minMs?: number
  maxMs?: number
  filters?: FieldFilter[]
  page?: number
  pageSize?: number
}

function traceSearchParams(s: TraceSearch): URLSearchParams {
  const p = new URLSearchParams()
  const set = (k: string, v: string | number | boolean | undefined) => {
    if (v !== undefined && v !== "") p.set(k, String(v))
  }
  set("q", s.q?.trim())
  if (!s.from && !s.to) set("range", s.range)
  set("from", s.from)
  set("to", s.to)
  s.endpoints?.forEach((e) => p.append("endpoint", e))
  s.services?.forEach((e) => p.append("service", e))
  set("errors", s.errors)
  set("minMs", s.minMs)
  set("maxMs", s.maxMs)
  if (s.filters?.length) p.set("filters", JSON.stringify(s.filters))
  set("page", s.page)
  set("pageSize", s.pageSize)
  return p
}

export const searchTraces = (search: TraceSearch, opts?: RequestOptions) =>
  api.get<PagedResult<TraceListItem>>("api/traces/search", { params: traceSearchParams(search), signal: opts?.signal }).then((r) => r.data)

export interface FacetValue {
  value: string
  count: number
}

/** Counts over the newest matches (`sampled` when there were more than the backend looked at). */
export interface TraceFacets {
  endpoints: FacetValue[]
  errorCount: number
  okCount: number
  sampled: boolean
}

export const getTraceFacets = (search: TraceSearch, opts?: RequestOptions) =>
  api.get<TraceFacets>("api/traces/facets", { params: traceSearchParams(search), signal: opts?.signal }).then((r) => r.data)

/** The most common values of a trace field, lowercase as the search index keeps them. */
export const getFieldValues = (key: string, prefix: string, opts?: RequestOptions) =>
  api.get<FacetValue[]>("api/attributes/traces/values", { params: { key, prefix: prefix || undefined }, signal: opts?.signal }).then((r) => r.data)

export const getTrace = (traceId: string, opts?: RequestOptions) =>
  api.get<TraceDetail>(`api/traces/${traceId}`, { signal: opts?.signal }).then((r) => r.data)

export type IgnoreRuleKind = "span" | "trace"
export interface IgnoreRule {
  id: string
  kind: IgnoreRuleKind
  name: string
  createdAt: string
}
export const createIgnoreRule = (kind: IgnoreRuleKind, name: string) =>
  api.post("api/ignore-rules", { kind, name })
export const getIgnoreRules = (opts?: RequestOptions) => api.get<IgnoreRule[]>("api/ignore-rules", { signal: opts?.signal }).then((r) => r.data)
export const deleteIgnoreRule = (id: string) => api.delete(`api/ignore-rules/${id}`)

export const pinTrace = (traceId: string) => api.put(`api/traces/${traceId}/pin`)
export const unpinTrace = (traceId: string) => api.delete(`api/traces/${traceId}/pin`)

/** Mirrors the backend's `RetentionSettingsDto`. */
export interface RetentionSettings {
  enabled: boolean
  traceRetentionDays: number
  logRetentionDays: number
  keepErrorTraces: boolean
  keepErrorLogs: boolean
}

export interface RetentionRun {
  startedAt: string
  finishedAt: string
  tracesDeleted: number
  logsDeleted: number
  error: string | null
}

export interface RetentionState {
  settings: RetentionSettings
  updatedAt: string | null
  lastRun: RetentionRun | null
}

export interface PinnedTrace {
  traceId: string
  rootName: string | null
  serviceName: string | null
  startTime: string | null
  hasError: boolean
  pinnedAt: string
}

export const getRetention = (opts?: RequestOptions) => api.get<RetentionState>("api/retention", { signal: opts?.signal }).then((r) => r.data)
export const saveRetention = (settings: RetentionSettings) => api.put<RetentionState>("api/retention", settings).then((r) => r.data)
export const runRetention = () => api.post<RetentionRun>("api/retention/run").then((r) => r.data)
export const getPinnedTraces = (opts?: RequestOptions) => api.get<PinnedTrace[]>("api/retention/pinned", { signal: opts?.signal }).then((r) => r.data)

export const getResources = (opts?: RequestOptions) => api.get<string[]>("api/resources", { signal: opts?.signal }).then((r) => r.data)

export type AttributeScope = "traces" | "logs"

export const getAttributeKeys = (scope: AttributeScope, opts?: RequestOptions) =>
  api.get<string[]>(`api/attributes/${scope}`, { signal: opts?.signal }).then((r) => r.data)
