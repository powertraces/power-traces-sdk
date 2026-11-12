import type { FieldFilter, FieldMatch } from "@/lib/api"

// Field filters travel in the URL as repeated `f` params: `f=http.route:eq:/api/orders`, with
// `:ne:` for "not equal" and `:has:` for "has word". Keys never contain these tokens; values may
// contain anything (only the first operator token splits).
const OPS: [string, FieldMatch][] = [
  [":eq:", "Equals"],
  [":ne:", "NotEquals"],
  [":has:", "HasWord"],
]

export function encodeFieldFilter(f: FieldFilter): string {
  const op = OPS.find(([, m]) => m === f.match)?.[0] ?? ":eq:"
  return `${f.key}${op}${f.value}`
}

export function decodeFieldFilters(values: string[]): FieldFilter[] {
  const filters: FieldFilter[] = []
  for (const raw of values) {
    let best: { index: number; op: string; match: FieldMatch } | null = null
    for (const [op, match] of OPS) {
      const index = raw.indexOf(op)
      if (index > 0 && (best === null || index < best.index)) best = { index, op, match }
    }
    if (best) filters.push({ key: raw.slice(0, best.index), value: raw.slice(best.index + best.op.length), match: best.match })
  }
  return filters
}

/** Links written before the search redesign used `attr=key:eq:value` / `key:contains:value`. */
export function decodeLegacyAttributeFilters(values: string[]): FieldFilter[] {
  return values.flatMap((raw) => {
    for (const [op, match] of [[":eq:", "Equals"], [":contains:", "HasWord"]] as const) {
      const index = raw.indexOf(op)
      if (index > 0) return [{ key: raw.slice(0, index), value: raw.slice(index + op.length), match }]
    }
    return []
  })
}

export const MATCH_LABEL: Record<FieldMatch, string> = { Equals: "=", NotEquals: "≠", HasWord: "has" }

/** The Traces page URL for a search, from anywhere in the app (e.g. a field on the trace page). */
export function tracesHref({ filters = [], q, range = "24h" }: { filters?: FieldFilter[]; q?: string; range?: string }): string {
  const p = new URLSearchParams()
  if (range) p.set("range", range)
  if (q) p.append("q", q)
  filters.forEach((f) => p.append("f", encodeFieldFilter(f)))
  return `/traces?${p.toString()}`
}

/** A value's search text: strings as they are, anything else as JSON. */
export function fieldValueText(v: unknown): string {
  return typeof v === "string" ? v : JSON.stringify(v)
}

/** Longest value the search index keeps whole (`SearchTerms.MaxValueLength`) — longer ones can only
 * be found by their words. */
export const MAX_EXACT_VALUE_LENGTH = 100

/** Unpacks a JSON-looking string the way the search index does (`SearchTerms.Fields`): one entry
 * per value, keyed by path — `body.customer.email`, `body.lines[].sku`. Returns null when the
 * value isn't JSON. */
export function jsonFields(key: string, value: string): { key: string; value: string }[] | null {
  const trimmed = value.trim()
  if (trimmed.length < 2 || !((trimmed[0] === "{" && trimmed.endsWith("}")) || (trimmed[0] === "[" && trimmed.endsWith("]")))) return null
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    return null
  }
  if (parsed === null || typeof parsed !== "object") return null
  const out: { key: string; value: string }[] = []
  collect(key, parsed, 1, out)
  return out
}

const FIELD_NAME = /^[A-Za-z_$@][\w$@-]{0,39}$/
export const looksLikeFieldName = (name: string) => FIELD_NAME.test(name) && !(name.length >= 16 && /^[0-9a-fA-F-]+$/.test(name))

function collect(key: string, value: unknown, depth: number, out: { key: string; value: string }[]) {
  if (out.length >= 200 || value === null || value === undefined) return
  if (typeof value === "string") {
    const nested = depth < 6 ? jsonFields(key, value) : null
    if (nested) out.push(...nested)
    else if (value !== "") out.push({ key, value })
    return
  }
  if (typeof value !== "object") {
    out.push({ key, value: String(value) })
    return
  }
  if (depth >= 6) return
  if (Array.isArray(value)) {
    for (const item of value) collect(item !== null && typeof item === "object" ? `${key}[]` : key, item, depth + 1, out)
    return
  }
  for (const [name, child] of Object.entries(value)) collect(looksLikeFieldName(name) ? `${key}.${name}` : key, child, depth + 1, out)
}

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json }

/** Parses a value that holds a JSON object or array; null for anything else. */
export function parseJsonContainer(text: string): Json[] | { [key: string]: Json } | null {
  const trimmed = text.trim()
  if (trimmed.length < 2 || !((trimmed[0] === "{" && trimmed.endsWith("}")) || (trimmed[0] === "[" && trimmed.endsWith("]")))) return null
  try {
    const parsed = JSON.parse(trimmed) as Json
    return parsed !== null && typeof parsed === "object" ? parsed : null
  } catch {
    return null
  }
}
