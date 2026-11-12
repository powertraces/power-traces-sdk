export type RangeKey = "15m" | "1h" | "6h" | "24h" | "7d" | "30d" | "all" | "custom"

export const RANGES: { value: RangeKey; label: string; description: string }[] = [
  { value: "15m", label: "15m", description: "in the last 15 minutes" },
  { value: "1h", label: "1h", description: "in the last hour" },
  { value: "6h", label: "6h", description: "in the last 6 hours" },
  { value: "24h", label: "24h", description: "in the last 24 hours" },
  { value: "7d", label: "7d", description: "in the last 7 days" },
  { value: "30d", label: "30d", description: "in the last 30 days" },
  { value: "all", label: "All", description: "of all time" },
  { value: "custom", label: "Custom", description: "in the chosen range" },
]

export const DEFAULT_RANGE: RangeKey = "24h"

/** The time range a list page's URL describes (`range`, or `from`/`to` for a custom one), and
 * what to send the backend for it. It defaults to the last 24 hours: searching all of history is a
 * choice, not an accident. */
export function readTimeRange(params: URLSearchParams) {
  const from = params.get("from") ?? undefined
  const to = params.get("to") ?? undefined
  const rangeParam = params.get("range") as RangeKey | null
  const range: RangeKey = from || to ? "custom" : rangeParam && RANGES.some((r) => r.value === rangeParam) ? rangeParam : DEFAULT_RANGE
  return {
    range,
    from,
    to,
    description: RANGES.find((r) => r.value === range)!.description,
    query: {
      range: range === "custom" || range === "all" ? undefined : range,
      from: range === "custom" ? from : undefined,
      to: range === "custom" ? to : undefined,
    },
  }
}
