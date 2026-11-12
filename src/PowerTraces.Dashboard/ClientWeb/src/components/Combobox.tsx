import { useEffect, useId, useMemo, useRef, useState } from "react"
import { Check, ChevronsUpDown, Search } from "lucide-react"
import clsx from "clsx"

interface ComboboxProps {
  options: string[]
  value: string
  onChange: (value: string) => void
  placeholder: string
  /** Offer the typed text itself when no option matches it exactly — attribute keys come from a
   * sample of recent rows, so a real key can be missing from the list. */
  allowCustom?: boolean
  ariaLabel: string
  className?: string
}

/** A searchable single-select: type to narrow the list, arrow keys + Enter to pick, Escape or a
 * click outside to close. Matches anywhere in the option (case-insensitive) and highlights the
 * matched part. */
export function Combobox({ options, value, onChange, placeholder, allowCustom, ariaLabel, className }: ComboboxProps) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState("")
  const [active, setActive] = useState(0)
  const rootRef = useRef<HTMLDivElement>(null)
  const listRef = useRef<HTMLUListElement>(null)
  const listId = useId()

  const items = useMemo(() => {
    const q = query.trim().toLowerCase()
    const matches = q ? options.filter((o) => o.toLowerCase().includes(q)) : options
    // Prefix matches first, then the rest, each alphabetical.
    const sorted = q ? [...matches].sort((a, b) => Number(!a.toLowerCase().startsWith(q)) - Number(!b.toLowerCase().startsWith(q))) : matches
    const custom = allowCustom && query.trim() && !options.includes(query.trim()) ? [query.trim()] : []
    // The free-text entry goes last, so Enter picks the best real match when there is one.
    return [...sorted, ...custom]
  }, [options, query, allowCustom])

  useEffect(() => {
    if (!open) return
    const onPointerDown = (e: PointerEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener("pointerdown", onPointerDown)
    return () => document.removeEventListener("pointerdown", onPointerDown)
  }, [open])

  useEffect(() => {
    listRef.current?.children[active]?.scrollIntoView({ block: "nearest" })
  }, [active])

  const openWith = (q: string) => {
    setQuery(q)
    setActive(0)
    setOpen(true)
  }

  const pick = (option: string) => {
    onChange(option)
    setOpen(false)
  }

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "ArrowDown") {
      e.preventDefault()
      setActive((i) => Math.min(items.length - 1, i + 1))
    } else if (e.key === "ArrowUp") {
      e.preventDefault()
      setActive((i) => Math.max(0, i - 1))
    } else if (e.key === "Enter") {
      e.preventDefault()
      if (items[active]) pick(items[active])
    } else if (e.key === "Escape") {
      setOpen(false)
    }
  }

  return (
    <div ref={rootRef} className={clsx("relative", className)}>
      <button
        type="button"
        aria-label={ariaLabel}
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => (open ? setOpen(false) : openWith(""))}
        className="control flex w-full items-center gap-2 text-left"
      >
        <span className={clsx("min-w-0 flex-1 truncate", value ? "font-mono text-xs" : "text-[13px] text-[var(--text-dim)]")}>
          {value || placeholder}
        </span>
        <ChevronsUpDown size={13} className="shrink-0 text-[var(--text-dim)]" />
      </button>

      {open && (
        <div className="absolute left-0 z-30 mt-1.5 w-[min(360px,90vw)] overflow-hidden rounded-lg border border-[var(--border)] bg-[var(--surface-raised)] shadow-[var(--shadow-md)]">
          <div className="flex items-center gap-2 border-b border-[var(--border)] px-3">
            <Search size={13} className="shrink-0 text-[var(--text-dim)]" />
            <input
              autoFocus
              value={query}
              onChange={(e) => openWith(e.target.value)}
              onKeyDown={onKeyDown}
              placeholder="Search attributes…"
              role="combobox"
              aria-label={`Search ${ariaLabel.toLowerCase()}s`}
              aria-controls={listId}
              aria-expanded
              aria-activedescendant={items[active] ? `${listId}-${active}` : undefined}
              className="h-10 min-w-0 flex-1 bg-transparent text-[13px] outline-none placeholder:text-[var(--text-dim)]"
            />
            <span className="shrink-0 text-[11px] text-[var(--text-dim)] tabular-nums">{items.length}</span>
          </div>
          <ul ref={listRef} id={listId} role="listbox" className="max-h-72 overflow-y-auto p-1">
            {items.length === 0 ? (
              <li className="px-2.5 py-2 text-xs text-[var(--text-dim)]">No matching attributes.</li>
            ) : (
              items.map((option, i) => {
                const isCustom = allowCustom && i === items.length - 1 && !options.includes(option)
                return (
                  <li
                    key={`${isCustom ? "custom:" : ""}${option}`}
                    id={`${listId}-${i}`}
                    role="option"
                    aria-selected={option === value}
                    onPointerEnter={() => setActive(i)}
                    onClick={() => pick(option)}
                    className={clsx(
                      "flex cursor-pointer items-center gap-2 rounded-md px-2.5 py-1.5 text-xs",
                      i === active ? "bg-[var(--hover-strong)] text-[var(--text)]" : "text-[var(--text-muted)]",
                    )}
                  >
                    <span className="min-w-0 flex-1 truncate font-mono">
                      {isCustom ? (
                        <>
                          <span className="font-sans text-[var(--text-dim)]">Use </span>
                          {option}
                        </>
                      ) : (
                        <Highlight text={option} query={query.trim()} />
                      )}
                    </span>
                    {option === value && <Check size={12} className="shrink-0 text-[var(--accent)]" />}
                  </li>
                )
              })
            )}
          </ul>
        </div>
      )}
    </div>
  )
}

export function Highlight({ text, query }: { text: string; query: string }) {
  const index = query ? text.toLowerCase().indexOf(query.toLowerCase()) : -1
  if (index < 0) return <>{text}</>
  return (
    <>
      {text.slice(0, index)}
      <mark className="rounded-sm bg-[var(--accent)]/20 text-[var(--text)]">{text.slice(index, index + query.length)}</mark>
      {text.slice(index + query.length)}
    </>
  )
}
