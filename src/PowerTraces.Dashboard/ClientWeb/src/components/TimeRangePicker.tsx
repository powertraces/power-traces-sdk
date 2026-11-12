import { Segmented } from "@/components/ui"
import { DEFAULT_RANGE, RANGES, type RangeKey } from "@/lib/timeRange"

type Update = (changes: Record<string, string | string[] | null | undefined>) => void

/** The preset ranges as one segmented control; "Custom" switches to `from`/`to` (starting from the
 * last 24 hours) and the page shows a {@link DateRangeFilter} for it. */
export function TimeRangePicker({ range, from, update }: { range: RangeKey; from?: string; update: Update }) {
  return (
    <div className="max-w-full overflow-x-auto">
      <Segmented
        label="Time range"
        value={range}
        options={RANGES.map((r) => ({ value: r.value, label: r.label }))}
        onChange={(next) =>
          next === "custom"
            ? update({ range: "custom", from: from ?? new Date(Date.now() - 24 * 3600_000).toISOString(), to: null })
            : update({ range: next === DEFAULT_RANGE ? null : next, from: null, to: null })
        }
      />
    </div>
  )
}
