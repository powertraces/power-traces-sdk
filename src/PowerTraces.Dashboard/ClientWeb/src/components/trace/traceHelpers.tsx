import type { ReactNode } from "react"
import { AlertCircle, ArrowLeftRight, Cpu, Database, ExternalLink, Globe, MessageSquare } from "lucide-react"
import type { SpanItem } from "@/lib/api"

// ── Category detection ──────────────────────────────────────────────────────
// Shared by the waterfall row and the detail panel so the same span looks the same in both —
// keyed off semconv attributes rather than just `kind`, since `kind` alone can't tell a DB call
// or an outbound RPC apart from a plain CLIENT span.

export type SpanCat = "http-server" | "http-client" | "db" | "rpc" | "messaging" | "internal" | "error"

export function detectCategory(span: SpanItem): SpanCat {
  if (span.hasError) return "error"
  const a = span.attributes
  if (a["db.system"] || a["db.system.name"]) return "db"
  if (a["rpc.system"]) return "rpc"
  if (a["messaging.system"]) return "messaging"
  const kind = span.kind?.toUpperCase()
  if (kind === "SERVER") return "http-server"
  if (kind === "CLIENT") return "http-client"
  return "internal"
}

interface CatStyle {
  /** CSS var name (no var()) used for the timeline bar / left accent border. */
  color: string
  icon: (props: { size?: number; className?: string }) => ReactNode
}

export const CAT_STYLES: Record<SpanCat, CatStyle> = {
  "http-server": { color: "--info", icon: (p) => <Globe {...p} /> },
  "http-client": { color: "--violet", icon: (p) => <ExternalLink {...p} /> },
  db: { color: "--accent", icon: (p) => <Database {...p} /> },
  rpc: { color: "--warn", icon: (p) => <ArrowLeftRight {...p} /> },
  messaging: { color: "--rose", icon: (p) => <MessageSquare {...p} /> },
  internal: { color: "--text-dim", icon: (p) => <Cpu {...p} /> },
  error: { color: "--error", icon: (p) => <AlertCircle {...p} /> },
}

export function CatIcon({ cat, size = 12 }: { cat: SpanCat; size?: number }) {
  const style = CAT_STYLES[cat]
  return <>{style.icon({ size, className: "shrink-0" })}</>
}

// ── Badges ───────────────────────────────────────────────────────────────────

export function kindLabel(kind?: string | null) {
  return (kind ?? "INTERNAL").replace("SPAN_KIND_", "").toUpperCase()
}

export function KindBadge({ kind, hasError }: { kind?: string | null; hasError: boolean }) {
  const label = kindLabel(kind)
  const cls = hasError
    ? "text-[var(--error)] bg-[var(--error)]/10"
    : label === "SERVER" || label === "CLIENT"
      ? "text-[var(--info)] bg-[var(--info)]/10"
      : label === "PRODUCER" || label === "CONSUMER"
        ? "text-[var(--warn)] bg-[var(--warn)]/10"
        : "text-[var(--text-dim)] bg-[var(--hover-strong)]"
  return (
    <span className={`shrink-0 rounded-sm px-1 py-px text-[9px] font-bold font-mono ${cls}`}>{label}</span>
  )
}

export function getHttpStatusFromSpan(span: SpanItem): string | null {
  const val = span.attributes["http.response.status_code"] ?? span.attributes["http.status_code"]
  return val != null ? String(val) : null
}

/** A CSS var name (no `var()`, matching `CatStyle.color`'s format) driven by the span's HTTP or
 * gRPC status — `null` when neither is present, so the caller can fall back to category color.
 * Distinct from `detectCategory`: category says *what kind* of call this was, this says *whether
 * it succeeded* — a span can be `db` AND have failed. */
export function getStatusColor(span: SpanItem): string | null {
  const httpStatus = getHttpStatusFromSpan(span)
  if (httpStatus != null) {
    const code = Number(httpStatus)
    if (code >= 500) return "--error"
    if (code >= 400) return "--warn"
    if (code >= 300) return "--info"
    if (code >= 200) return "--success"
  }

  const grpcStatus = span.attributes["rpc.grpc.status_code"]
  if (grpcStatus != null) {
    return Number(grpcStatus) === 0 ? "--success" : "--error"
  }

  return null
}

export function HttpStatusBadge({ status }: { status?: string | null }) {
  if (!status) return null
  const code = Number(status)
  const cls =
    code >= 500
      ? "text-[var(--error)] bg-[var(--error)]/10"
      : code >= 400
        ? "text-[var(--warn)] bg-[var(--warn)]/10"
        : code >= 300
          ? "text-[var(--info)] bg-[var(--info)]/10"
          : "text-[var(--success)] bg-[var(--success)]/10"
  return <span className={`shrink-0 rounded-sm px-1 py-px text-[9px] font-bold font-mono ${cls}`}>{status}</span>
}

export function StatusBadge({ hasError, status }: { hasError: boolean; status?: string | null }) {
  if (hasError || status === "ERROR")
    return (
      <span className="rounded-sm border border-[var(--error)]/30 bg-[var(--error)]/10 px-2 py-0.5 font-mono text-[10px] font-bold text-[var(--error)]">
        ERROR
      </span>
    )
  if (status === "OK")
    return (
      <span className="rounded-sm border border-[var(--success)]/30 bg-[var(--success)]/10 px-2 py-0.5 font-mono text-[10px] font-bold text-[var(--success)]">
        OK
      </span>
    )
  return (
    <span className="rounded-sm border border-[var(--border)] bg-[var(--hover-strong)] px-2 py-0.5 font-mono text-[10px] font-bold text-[var(--text-dim)]">
      UNSET
    </span>
  )
}

// ── Attribute grouping / humanizing ─────────────────────────────────────────

export const ATTR_NAMESPACE_LABELS: Record<string, string> = {
  http: "HTTP",
  rpc: "RPC",
  db: "DB",
  net: "Net",
  server: "Server",
  client: "Client",
  peer: "Peer",
  url: "URL",
  messaging: "Messaging",
  faas: "FaaS",
  code: "Code",
  thread: "Thread",
  process: "Process",
  otel: "OTel",
  telemetry: "Telemetry",
}

export function humanizeAttr(key: string): string {
  const parts = key.split(".")
  return parts
    .map((p, i) => {
      const upper = ATTR_NAMESPACE_LABELS[p]
      if (upper && i === 0) return upper
      return p.replace(/_/g, " ").replace(/\b\w/g, (c) => c.toUpperCase())
    })
    .join(" ")
}

export function groupAttrs(attrs: [string, unknown][]): Map<string, [string, unknown][]> {
  const map = new Map<string, [string, unknown][]>()
  for (const entry of attrs) {
    const ns = entry[0].includes(".") ? entry[0].split(".")[0] : "other"
    if (!map.has(ns)) map.set(ns, [])
    map.get(ns)!.push(entry)
  }
  return map
}

// ── Sensitive attribute masking ─────────────────────────────────────────────

const SENSITIVE_PATTERNS = [
  /authorization/i,
  /^x-api-key$/i,
  /\btoken\b/i,
  /\bpassword\b/i,
  /\bsecret\b/i,
  /\bcookie\b/i,
  /\bcredential/i,
  /\bauth\b/i,
  /\bbearer\b/i,
  /\bprivate.?key/i,
  /\baccess.?key/i,
]

export function isSensitiveAttr(key: string): boolean {
  const lower = key.toLowerCase()
  return SENSITIVE_PATTERNS.some((p) => p.test(lower))
}
