/** `<input type="datetime-local">` values are local-time strings with no timezone
 * (`"2026-01-02T15:04"`) — `new Date(...)` parses that as local time, so `.toISOString()` gives the
 * correct UTC instant to send to the API. */
export function localInputToIso(value: string): string | undefined {
  if (!value) return undefined
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? undefined : date.toISOString()
}

/** The inverse of `localInputToIso`: an ISO instant shown as a `datetime-local` value in the
 * viewer's own time zone (so a shared link shows each viewer the same instant, in their time). */
export function isoToLocalInput(iso: string | null | undefined): string {
  if (!iso) return ""
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ""
  const pad = (n: number) => String(n).padStart(2, "0")
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}
