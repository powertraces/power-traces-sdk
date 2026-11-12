import type { LucideIcon } from "lucide-react"
import { Sparkline } from "@/components/charts/TimeSeriesChart"

/** A KPI: label, headline number, a one-line context note, and an optional trend sparkline. A
 * status tone tints only the icon chip (never the number), and always sits beside a text label. */
export function StatTile({
  label,
  value,
  note,
  icon: Icon,
  tone = "default",
  trend,
}: {
  label: string
  value: string
  note?: string
  icon: LucideIcon
  tone?: "default" | "error" | "warn" | "good"
  trend?: number[]
}) {
  const accent = { default: "var(--accent)", error: "var(--error)", warn: "var(--warn)", good: "var(--success)" }[tone]
  return (
    <div className="flex flex-col rounded-xl border border-[var(--border)] bg-[var(--surface)] p-4 shadow-[var(--shadow-sm)]">
      <div className="flex items-center justify-between">
        <span className="text-[13px] font-medium text-[var(--text-muted)]">{label}</span>
        <span
          className="flex h-7 w-7 items-center justify-center rounded-lg"
          style={{ background: `color-mix(in srgb, ${accent} 14%, transparent)`, color: accent }}
        >
          <Icon size={14} />
        </span>
      </div>
      <div className="mt-2 text-[28px] leading-none font-semibold tracking-tight text-[var(--text)]">{value}</div>
      {note && <div className="mt-1.5 text-xs text-[var(--text-dim)]">{note}</div>}
      {trend && (
        <div className="mt-3">
          <Sparkline values={trend} color="var(--series-1)" />
        </div>
      )}
    </div>
  )
}
