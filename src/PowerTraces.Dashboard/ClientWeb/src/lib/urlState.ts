import { useCallback } from "react"
import { useSearchParams } from "react-router-dom"
import type { AttributeFilter } from "@/lib/api"

/** List-page state kept in the URL query string, so a filtered view can be shared or bookmarked
 * and survives a reload. `update` sets/clears several params at once; any change other than the
 * page itself resets to page 1. Typing-driven updates replace the history entry instead of
 * pushing one per keystroke. */
export function useUrlState() {
  const [params, setParams] = useSearchParams()

  const update = useCallback(
    (changes: Record<string, string | string[] | null | undefined>, options: { replace?: boolean } = {}) => {
      setParams(
        (current) => {
          const next = new URLSearchParams(current)
          for (const [key, value] of Object.entries(changes)) {
            next.delete(key)
            if (Array.isArray(value)) value.forEach((v) => next.append(key, v))
            else if (value !== null && value !== undefined && value !== "") next.set(key, value)
          }
          if (!("page" in changes)) next.delete("page")
          return next
        },
        { replace: options.replace },
      )
    },
    [setParams],
  )

  return { params, update }
}

export function pageParam(params: URLSearchParams): number {
  const page = Number(params.get("page"))
  return Number.isInteger(page) && page > 1 ? page : 1
}

// Attribute filters travel as repeated `attr` params: `attr=http.route:eq:/api/orders`, or
// `:contains:` for substring matches. Keys never contain ":eq:"/":contains:", values may contain
// anything (only the first operator token splits).
const OPS = [":eq:", ":contains:"] as const

export function encodeAttributeFilter(f: AttributeFilter): string {
  return `${f.key}${f.contains ? ":contains:" : ":eq:"}${f.value}`
}

export function decodeAttributeFilters(values: string[]): AttributeFilter[] {
  const filters: AttributeFilter[] = []
  for (const raw of values) {
    let best: { index: number; op: (typeof OPS)[number] } | null = null
    for (const op of OPS) {
      const index = raw.indexOf(op)
      if (index > 0 && (best === null || index < best.index)) best = { index, op }
    }
    if (best) {
      filters.push({ key: raw.slice(0, best.index), value: raw.slice(best.index + best.op.length), contains: best.op === ":contains:" })
    }
  }
  return filters
}
