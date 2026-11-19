// Plain-language names for what OpenTelemetry records, so a span reads as sentences and labels a
// non-developer understands. Only well-known keys and codes are translated; anything else is
// shown as recorded, under "Technical".

interface StatusInfo {
  /** The standard reason phrase ("Payment Required"). */
  phrase: string
  /** What it means, in a few everyday words ("the payment itself failed"). */
  meaning: string
}

const HTTP_STATUS: Record<number, StatusInfo> = {
  200: { phrase: "OK", meaning: "it worked" },
  201: { phrase: "Created", meaning: "it worked and something was created" },
  202: { phrase: "Accepted", meaning: "it was accepted and will be handled later" },
  204: { phrase: "No Content", meaning: "it worked, with nothing to send back" },
  301: { phrase: "Moved Permanently", meaning: "it lives at another address now" },
  302: { phrase: "Found", meaning: "it was sent to another address" },
  304: { phrase: "Not Modified", meaning: "nothing changed since the last time" },
  400: { phrase: "Bad Request", meaning: "the request had wrong or missing data" },
  401: { phrase: "Unauthorized", meaning: "the caller isn't signed in" },
  402: { phrase: "Payment Required", meaning: "the payment itself failed" },
  403: { phrase: "Forbidden", meaning: "signed in, but not allowed to do this" },
  404: { phrase: "Not Found", meaning: "the thing asked for doesn't exist" },
  405: { phrase: "Method Not Allowed", meaning: "this kind of request isn't supported here" },
  408: { phrase: "Request Timeout", meaning: "the request took too long to arrive" },
  409: { phrase: "Conflict", meaning: "it conflicts with the current data" },
  410: { phrase: "Gone", meaning: "it was removed" },
  413: { phrase: "Payload Too Large", meaning: "the request was too big" },
  415: { phrase: "Unsupported Media Type", meaning: "the request was in a format it doesn't accept" },
  422: { phrase: "Unprocessable Entity", meaning: "the data was understood but isn't valid" },
  429: { phrase: "Too Many Requests", meaning: "too many requests, so it was slowed down" },
  500: { phrase: "Internal Server Error", meaning: "the server crashed while handling it" },
  501: { phrase: "Not Implemented", meaning: "the server doesn't support this yet" },
  502: { phrase: "Bad Gateway", meaning: "a server further along gave a bad answer" },
  503: { phrase: "Service Unavailable", meaning: "the server is down or too busy" },
  504: { phrase: "Gateway Timeout", meaning: "a server further along took too long to answer" },
}

/** "402" → { phrase: "Payment Required", meaning: "the payment itself failed" }; unknown codes
 * fall back to their class. */
export function httpStatusInfo(status: string | number | null | undefined): StatusInfo | null {
  if (status == null || status === "") return null
  const code = Number(status)
  if (!Number.isFinite(code)) return null
  const known = HTTP_STATUS[code]
  if (known) return known
  if (code >= 500) return { phrase: "Server Error", meaning: "the receiving service failed" }
  if (code >= 400) return { phrase: "Client Error", meaning: "the request was refused" }
  if (code >= 300) return { phrase: "Redirect", meaning: "it was sent somewhere else" }
  if (code >= 200) return { phrase: "Success", meaning: "it worked" }
  return null
}

/** Who the status code blames: the request (4xx) or the receiving service (5xx). */
export function statusBlame(status: string | number | null | undefined): "request" | "service" | null {
  const code = Number(status)
  if (!Number.isFinite(code)) return null
  if (code >= 500) return "service"
  if (code >= 400) return "request"
  return null
}

const GRPC_STATUS: Record<number, string> = {
  0: "OK",
  1: "Cancelled",
  2: "Unknown error",
  3: "Invalid argument",
  4: "Deadline exceeded",
  5: "Not found",
  6: "Already exists",
  7: "Permission denied",
  8: "Resource exhausted",
  9: "Failed precondition",
  10: "Aborted",
  11: "Out of range",
  12: "Unimplemented",
  13: "Internal error",
  14: "Unavailable",
  15: "Data loss",
  16: "Unauthenticated",
}

export function grpcStatusText(code: unknown): string | null {
  const n = Number(code)
  return Number.isFinite(n) ? (GRPC_STATUS[n] ?? null) : null
}

/** Everyday names for the attribute keys people most often need. */
const ATTRIBUTE_LABELS: Record<string, string> = {
  "http.request.method": "Request type",
  "http.method": "Request type",
  "url.full": "Address",
  "http.url": "Address",
  "url.path": "Path",
  "http.target": "Path",
  "http.route": "Endpoint",
  "url.query": "Query string",
  "http.response.status_code": "Result code",
  "http.status_code": "Result code",
  "server.address": "Server",
  "server.port": "Server port",
  "client.address": "Caller address",
  "peer.service": "Called service",
  "net.peer.name": "Server",
  "user_agent.original": "Browser / client",
  "db.system": "Database",
  "db.system.name": "Database",
  "db.namespace": "Database name",
  "db.name": "Database name",
  "db.query.text": "Query",
  "db.statement": "Query",
  "db.operation.name": "Database operation",
  "db.operation": "Database operation",
  "db.collection.name": "Table / collection",
  "db.sql.table": "Table",
  "db.response.returned_rows": "Rows returned",
  "rpc.system": "Call system",
  "rpc.service": "Called service",
  "rpc.method": "Called method",
  "messaging.system": "Messaging system",
  "messaging.destination.name": "Queue / topic",
  "error.type": "Error type",
  "exception.message": "Error message",
  "exception.type": "Error type",
  "host.name": "Machine",
  "service.instance.id": "Instance",
  "deployment.environment": "Environment",
  "deployment.environment.name": "Environment",
}

/** A plain label for a known key, or null to show the key as recorded. */
export function attributeLabel(key: string): string | null {
  return ATTRIBUTE_LABELS[key] ?? null
}
