import { isoToLocalInput, localInputToIso } from "@/lib/datetime"

interface DateRangeFilterProps {
  /** ISO instants (as kept in the URL), or undefined for an open end. */
  from?: string
  to?: string
  onChange: (fromIso: string | undefined, toIso: string | undefined) => void
}

/** Two `datetime-local` inputs over ISO instants: the URL holds UTC, the inputs show local time. */
export function DateRangeFilter({ from, to, onChange }: DateRangeFilterProps) {
  return (
    <div className="flex items-center gap-1.5">
      <input
        type="datetime-local"
        value={isoToLocalInput(from)}
        onChange={(e) => onChange(localInputToIso(e.target.value), to)}
        title="From"
        aria-label="From date/time"
        className="control text-[var(--text-muted)]"
      />
      <span className="text-xs text-[var(--text-dim)]">to</span>
      <input
        type="datetime-local"
        value={isoToLocalInput(to)}
        onChange={(e) => onChange(from, localInputToIso(e.target.value))}
        title="To"
        aria-label="To date/time"
        className="control text-[var(--text-muted)]"
      />
      {(from || to) && (
        <button
          onClick={() => onChange(undefined, undefined)}
          title="Clear date range"
          aria-label="Clear date range"
          className="h-[34px] rounded-lg px-2.5 text-xs text-[var(--text-dim)] transition-colors hover:bg-[var(--hover-strong)] hover:text-[var(--text)]"
        >
          Clear
        </button>
      )}
    </div>
  )
}
