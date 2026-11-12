export type SeverityLevel = "trace" | "debug" | "info" | "warn" | "error" | "fatal"

export function severityLevel(severityNumber: number): SeverityLevel {
  if (severityNumber >= 21) return "fatal"
  if (severityNumber >= 17) return "error"
  if (severityNumber >= 13) return "warn"
  if (severityNumber >= 9) return "info"
  if (severityNumber >= 5) return "debug"
  return "trace"
}

export function formatDuration(ms: number): string {
  if (ms < 1) return "<1ms"
  if (ms < 1000) return `${Math.round(ms)}ms`
  return `${(ms / 1000).toFixed(2)}s`
}

export function formatTimestamp(iso: string): string {
  const d = new Date(iso.endsWith("Z") ? iso : iso + "Z")
  return d.toLocaleString(undefined, {
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  })
}

export function formatTimeOnly(iso: string): string {
  const d = new Date(iso.endsWith("Z") ? iso : iso + "Z")
  return d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" })
}

export function formatAttributeValue(value: unknown): string {
  if (value === undefined || value === null) return "—"
  return typeof value === "object" ? JSON.stringify(value) : String(value)
}

export function formatCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n >= 10_000_000 ? 0 : 1)}M`
  if (n >= 10_000) return `${(n / 1_000).toFixed(n >= 100_000 ? 0 : 1)}k`
  return n.toLocaleString()
}

export function formatRelative(iso: string): string {
  const d = new Date(iso.endsWith("Z") ? iso : iso + "Z")
  const s = Math.round((Date.now() - d.getTime()) / 1000)
  if (s < 5) return "just now"
  if (s < 60) return `${s}s ago`
  if (s < 3600) return `${Math.floor(s / 60)}m ago`
  if (s < 86_400) return `${Math.floor(s / 3600)}h ago`
  return `${Math.floor(s / 86_400)}d ago`
}

/** A bucket width for chart captions: 60 -> "1 min", 900 -> "15 min", 7200 -> "2 h". */
export function formatInterval(seconds: number): string {
  if (seconds < 60) return `${seconds} s`
  if (seconds < 3600) return `${Math.round(seconds / 60)} min`
  if (seconds < 86_400) return `${+(seconds / 3600).toFixed(1)} h`
  return `${+(seconds / 86_400).toFixed(1)} d`
}
