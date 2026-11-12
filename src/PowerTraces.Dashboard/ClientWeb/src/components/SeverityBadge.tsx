import { severityLevel } from "@/lib/format"

const styles: Record<string, string> = {
  trace: "text-[var(--text-dim)] bg-[var(--hover-strong)]",
  debug: "text-[var(--text-muted)] bg-[var(--hover-strong)]",
  info: "text-[var(--info)] bg-[var(--info)]/10",
  warn: "text-[var(--warn)] bg-[var(--warn)]/10",
  error: "text-[var(--error)] bg-[var(--error)]/10",
  fatal: "text-white bg-[var(--error)]",
}

export function SeverityBadge({ severityNumber, text }: { severityNumber: number; text?: string | null }) {
  const level = severityLevel(severityNumber)
  return (
    <span
      className={`inline-flex items-center rounded px-1.5 py-0.5 text-[11px] font-mono font-medium uppercase tracking-wide ${styles[level]}`}
    >
      {text || level}
    </span>
  )
}
