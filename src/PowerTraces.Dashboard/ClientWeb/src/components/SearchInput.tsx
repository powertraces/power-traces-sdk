import { useEffect, useRef, useState } from "react"
import { Search, X } from "lucide-react"

/** A search box whose value lives in the URL: it updates its own text on every keystroke but only
 * reports the change after a short pause, so typing doesn't fire a query (or a history entry) per
 * key. Back/forward and shared links still drive it, through `value`. */
export function SearchInput({ value, onChange, placeholder, ariaLabel }: { value: string; onChange: (value: string) => void; placeholder: string; ariaLabel: string }) {
  const [text, setText] = useState(value)
  const [lastValue, setLastValue] = useState(value)
  const onChangeRef = useRef(onChange)

  useEffect(() => {
    onChangeRef.current = onChange
  })

  // The URL changed underneath us (navigation, a shared link) — show what it says.
  if (value !== lastValue) {
    setLastValue(value)
    setText(value)
  }

  useEffect(() => {
    if (text === value) return
    const timer = setTimeout(() => onChangeRef.current(text), 300)
    return () => clearTimeout(timer)
  }, [text, value])

  return (
    <div className="relative min-w-[220px] flex-1 sm:max-w-sm">
      <Search size={14} className="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-[var(--text-dim)]" />
      <input
        value={text}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={(e) => e.key === "Enter" && onChangeRef.current(text)}
        placeholder={placeholder}
        aria-label={ariaLabel}
        className="control w-full pr-8 pl-9"
      />
      {text && (
        <button
          type="button"
          onClick={() => {
            setText("")
            onChangeRef.current("")
          }}
          aria-label="Clear search"
          className="absolute top-1/2 right-2 -translate-y-1/2 rounded p-0.5 text-[var(--text-dim)] hover:text-[var(--text)]"
        >
          <X size={13} />
        </button>
      )}
    </div>
  )
}
