import type { LogFacets } from "@/lib/api"
import { Check, FacetGroup, FacetPanelFrame } from "@/components/search/TraceFacetPanel"

/** Severity levels by their lowest OpenTelemetry severity number (see the backend's `LogSeverity`). */
const SEVERITY_LEVELS = [
  { value: 1, label: "Trace", color: "var(--text-dim)" },
  { value: 5, label: "Debug", color: "var(--text-muted)" },
  { value: 9, label: "Info", color: "var(--info)" },
  { value: 13, label: "Warn", color: "var(--warn)" },
  { value: 17, label: "Error", color: "var(--error)" },
  { value: 21, label: "Fatal", color: "var(--error)" },
]

const UNSPECIFIED = { value: 0, label: "Unspecified", color: "var(--border-strong)" }

/** The side panel of the Logs page: severity levels and services, each with how many of the
 * current matches it has (counted with that facet's own selection left out, so the alternatives
 * stay visible). */
export function LogFacetPanel({
  className,
  facets,
  severities,
  onSeverities,
  services,
  selectedServices,
  onServices,
}: {
  className?: string
  facets: LogFacets | undefined
  severities: number[]
  onSeverities: (next: number[]) => void
  /** Every service that has reported, so services without matches can still be picked. */
  services: string[]
  selectedServices: string[]
  onServices: (next: string[]) => void
}) {
  const approx = facets?.sampled ? "+" : ""
  const severityCount = (level: number) => (facets ? (facets.severities.find((s) => s.severity === level)?.count ?? 0) : undefined)
  const levels = severityCount(0) || severities.includes(0) ? [...SEVERITY_LEVELS, UNSPECIFIED] : SEVERITY_LEVELS

  // Services with matches first (by count), then the rest of the known ones, then any selected
  // service no longer known.
  const counted = (facets?.services ?? []).filter((s) => s.value)
  const names = [...counted.map((s) => s.value), ...services.filter((s) => !counted.some((c) => c.value === s))]
  names.push(...selectedServices.filter((s) => !names.includes(s)))

  const toggle = <T,>(items: T[], item: T) => (items.includes(item) ? items.filter((i) => i !== item) : [...items, item])

  return (
    <FacetPanelFrame className={className}>
      <FacetGroup title="Severity">
        {levels.map((l) => (
          <Check
            key={l.value}
            label={l.label}
            dot={l.color}
            count={severityCount(l.value)}
            approx={approx}
            checked={severities.includes(l.value)}
            onChange={() => onSeverities(toggle(severities, l.value))}
          />
        ))}
      </FacetGroup>

      <FacetGroup title="Service">
        {names.length === 0 && <p className="px-1 text-xs text-[var(--text-dim)]">No services have reported yet.</p>}
        {names.map((s) => (
          <Check
            key={s}
            label={s}
            count={facets ? (counted.find((c) => c.value === s)?.count ?? 0) : undefined}
            approx={approx}
            checked={selectedServices.includes(s)}
            onChange={() => onServices(toggle(selectedServices, s))}
          />
        ))}
      </FacetGroup>
    </FacetPanelFrame>
  )
}
