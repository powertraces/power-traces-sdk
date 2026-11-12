import { useState } from "react"

/** Per-viewer preference storage — nothing here is server-persisted or shared across browsers. */
export function useLocalStorageState<T>(key: string, initial: T) {
  const [value, setValue] = useState<T>(() => {
    try {
      const raw = localStorage.getItem(key)
      return raw ? (JSON.parse(raw) as T) : initial
    } catch {
      return initial
    }
  })

  const update = (next: T) => {
    setValue(next)
    try {
      localStorage.setItem(key, JSON.stringify(next))
    } catch {
      // Private browsing, storage disabled/full, etc. — the preference just won't persist.
    }
  }

  return [value, update] as const
}
