import { useState, type ReactNode } from "react"
import clsx from "clsx"
import type { TraceFacets } from "@/lib/api"

const ENDPOINTS_SHOWN = 8

/** The side panel of the Traces page: endpoints and status with how many of the current matches
 * each has (counted with that facet's own selection left out, so the alternatives stay visible),
 * services, and a duration range. */
export function TraceFacetPanel({
  className,
  facets,
  endpoints,
  onEndpoints,
  errors,
  onErrors,
  services,
  selectedServices,
  onServices,
  minMs,
  maxMs,
  onDuration,
}: {
  className?: string
  facets: TraceFacets | undefined
  endpoints: string[]
  onEndpoints: (next: string[]) => void
  errors: boolean | undefined
  onErrors: (next: boolean | undefined) => void
  services: string[]
  selectedServices: string[]
  onServices: (next: string[]) => void
  minMs: number | undefined
  maxMs: number | undefined
  onDuration: (min: number | undefined, max: number | undefined) => void
}) {
  const [showAll, setShowAll] = useState(false)
  const list = facets?.endpoints ?? []
  // Selected endpoints stay listed even when the current matches have none.
  const merged = [...list, ...endpoints.filter((e) => !list.some((f) => f.value === e)).map((value) => ({ value, count: 0 }))]
  const shown = showAll ? merged : merged.slice(0, ENDPOINTS_SHOWN)
  const approx = facets?.sampled ? "+" : ""

  const toggle = <T,>(items: T[], item: T) => (items.includes(item) ? items.filter((i) => i !== item) : [...items, item])

  return (
    <FacetPanelFrame className={className}>
      <FacetGroup title="Endpoint">
        {shown.length === 0 && <p className="px-1 text-xs text-[var(--text-dim)]">No endpoints in these results.</p>}
        {shown.map((e) => {
          const on = endpoints.includes(e.value)
          return (
            <button
              key={e.value || "(unnamed)"}
              type="button"
              aria-pressed={on}
              onClick={() => onEndpoints(toggle(endpoints, e.value))}
              title={e.value || "(unnamed)"}
              className={clsx(
                "flex min-h-8 items-center gap-2 rounded-md border px-2 text-left text-xs transition-colors",
                on ? "border-[var(--svc-2)]/40 bg-[var(--svc-2)]/10 text-[var(--text)]" : "border-transparent text-[var(--text)] hover:bg-[var(--hover-strong)]",
              )}
            >
              <span className="min-w-0 flex-1 truncate font-mono">{e.value || "(unnamed)"}</span>
              <span className="shrink-0 text-[var(--text-muted)] tabular-nums">
                {e.count.toLocaleString()}
                {e.count > 0 ? approx : ""}
              </span>
            </button>
          )
        })}
        {merged.length > ENDPOINTS_SHOWN && (
          <button type="button" onClick={() => setShowAll((v) => !v)} className="w-fit px-1 text-xs text-[var(--accent)] hover:underline">
            {showAll ? "Show fewer" : `Show all ${merged.length} endpoints`}
          </button>
        )}
      </FacetGroup>

      <FacetGroup title="Status">
        <Check label="Error" count={facets?.errorCount} approx={approx} checked={errors === true} onChange={() => onErrors(errors === true ? undefined : true)} dot="var(--error)" />
        <Check label="OK" count={facets?.okCount} approx={approx} checked={errors === false} onChange={() => onErrors(errors === false ? undefined : false)} dot="var(--success)" />
      </FacetGroup>

      {services.length > 0 && (
        <FacetGroup title="Service">
          {services.map((s) => (
            <Check key={s} label={s} checked={selectedServices.includes(s)} onChange={() => onServices(toggle(selectedServices, s))} />
          ))}
        </FacetGroup>
      )}

      <FacetGroup title="Duration (ms)">
        <DurationInputs minMs={minMs} maxMs={maxMs} onChange={onDuration} />
      </FacetGroup>
    </FacetPanelFrame>
  )
}

/** The card a list page's filter panel sits in; the caller sets its width. */
export function FacetPanelFrame({ className, children }: { className?: string; children: ReactNode }) {
  return (
    <aside aria-label="Refine results" className={clsx("flex min-w-0 flex-col gap-4 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3.5 shadow-[var(--shadow-sm)]", className)}>
      {children}
    </aside>
  )
}

export function FacetGroup({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1 border-b border-[var(--border)] pb-4 last:border-0 last:pb-0">
      <h2 className="mb-1 px-1 text-[11px] font-semibold tracking-wider text-[var(--text-dim)] uppercase">{title}</h2>
      {children}
    </div>
  )
}

export function Check({ label, count, approx, checked, onChange, dot }: { label: string; count?: number; approx?: string; checked: boolean; onChange: () => void; dot?: string }) {
  return (
    <label className="flex min-h-8 cursor-pointer items-center gap-2 rounded-md px-1 text-[13px] hover:bg-[var(--hover)]">
      <input type="checkbox" checked={checked} onChange={onChange} className="accent-[var(--accent)]" />
      {dot && <span className="h-2 w-2 shrink-0 rounded-full" style={{ background: dot }} aria-hidden="true" />}
      <span className="min-w-0 flex-1 truncate">{label}</span>
      {count !== undefined && (
        <span className="text-xs text-[var(--text-muted)] tabular-nums">
          {count.toLocaleString()}
          {count > 0 ? approx : ""}
        </span>
      )}
    </label>
  )
}

/** Applied on Enter or leaving the field, so typing doesn't run a search per digit. */
function DurationInputs({ minMs, maxMs, onChange }: { minMs?: number; maxMs?: number; onChange: (min: number | undefined, max: number | undefined) => void }) {
  const [min, setMin] = useState(minMs?.toString() ?? "")
  const [max, setMax] = useState(maxMs?.toString() ?? "")
  const [last, setLast] = useState({ minMs, maxMs })

  // The URL changed underneath (navigation, a shared link) — show what it says.
  if (last.minMs !== minMs || last.maxMs !== maxMs) {
    setLast({ minMs, maxMs })
    setMin(minMs?.toString() ?? "")
    setMax(maxMs?.toString() ?? "")
  }

  const parse = (v: string) => (v.trim() === "" || Number.isNaN(Number(v)) ? undefined : Math.max(0, Math.round(Number(v))))
  const commit = () => {
    const nextMin = parse(min)
    const nextMax = parse(max)
    if (nextMin !== minMs || nextMax !== maxMs) onChange(nextMin, nextMax)
  }

  return (
    <div className="flex gap-2 px-1">
      {(
        [
          ["Min", min, setMin, "0"],
          ["Max", max, setMax, "any"],
        ] as const
      ).map(([label, value, set, placeholder]) => (
        <label key={label} className="flex min-w-0 flex-1 flex-col gap-1 text-[11px] text-[var(--text-muted)]">
          {label}
          <input
            inputMode="numeric"
            value={value}
            placeholder={placeholder}
            onChange={(e) => set(e.target.value)}
            onBlur={commit}
            onKeyDown={(e) => e.key === "Enter" && commit()}
            className="control h-8 w-full font-mono text-xs"
          />
        </label>
      ))}
    </div>
  )
}
