import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from "react"
import { useQuery } from "@tanstack/react-query"
import { Braces, CornerDownLeft, Route, Search, X } from "lucide-react"
import clsx from "clsx"
import { getAttributeKeys, getFieldValues, type AttributeScope, type FacetValue, type FieldFilter, type FieldMatch } from "@/lib/api"
import { MATCH_LABEL } from "@/lib/traceSearch"
import { Highlight } from "@/components/Combobox"

export interface SearchState {
  /** Free-text pieces, each a chip; the backend gets them joined with spaces. */
  text: string[]
  endpoints: string[]
  filters: FieldFilter[]
}

type Suggestion =
  | { kind: "text"; value: string }
  | { kind: "filter"; filter: FieldFilter }
  | { kind: "field"; key: string }
  | { kind: "endpoint"; name: string; count: number }
  | { kind: "value"; value: string; count?: number }

const MATCH_OPTIONS: { value: FieldMatch; label: string; title: string }[] = [
  { value: "Equals", label: "=", title: "Equals (whole value)" },
  { value: "NotEquals", label: "≠", title: "Not equal" },
  { value: "HasWord", label: "has word", title: "Has these words" },
]

// "key=value", "key!=value", "key~words" typed straight in.
const TYPED_FILTER = /^([^\s=!~]+)\s*(!=|=|~)\s*(.+)$/

const SCOPE_TEXT: Record<AttributeScope, { aria: string; where: string; empty: string; more: string }> = {
  traces: {
    aria: "Search traces",
    where: "names, attributes, bodies",
    empty: "Search any value, word, field or endpoint…  e.g. 12345, card declined, http.route=/api/orders",
    more: "Add a word, field or endpoint…",
  },
  logs: {
    aria: "Search logs",
    where: "messages",
    empty: "Search messages, a trace id or a field…  e.g. timeout, card declined, http.route=/api/orders",
    more: "Add a word or field…",
  },
}

/** One search box for a whole list search: the current conditions as removable chips
 * (endpoints, field filters, text), and an input whose suggestions offer — for what's typed — a
 * text search, matching fields (attribute keys, and for traces fields inside JSON bodies), and
 * matching endpoints. Picking a field turns the input into that field's value, with its real
 * values suggested where the search index knows them (traces). Backspace on an empty input
 * removes the last chip. */
export function SearchBar({
  scope,
  state,
  onChange,
  endpointOptions = [],
}: {
  scope: AttributeScope
  state: SearchState
  onChange: (next: SearchState) => void
  endpointOptions?: FacetValue[]
}) {
  const copy = SCOPE_TEXT[scope]
  const [input, setInput] = useState("")
  const [field, setField] = useState<string | null>(null)
  const [match, setMatch] = useState<FieldMatch>("Equals")
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const [debounced, setDebounced] = useState("")
  const rootRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const listId = useId()

  const { data: keys } = useQuery({ queryKey: ["attribute-keys", scope], queryFn: ({ signal }) => getAttributeKeys(scope, { signal }) })

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(input.trim()), 150)
    return () => clearTimeout(timer)
  }, [input])

  const { data: values } = useQuery({
    queryKey: ["field-values", field, debounced],
    queryFn: ({ signal }) => getFieldValues(field!, debounced.toLowerCase(), { signal }),
    enabled: scope === "traces" && field !== null && match !== "HasWord",
    placeholderData: (previous) => previous,
  })

  useEffect(() => {
    if (!open) return
    const onPointerDown = (e: PointerEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener("pointerdown", onPointerDown)
    return () => document.removeEventListener("pointerdown", onPointerDown)
  }, [open])

  const suggestions = useMemo<Suggestion[]>(() => {
    const typed = input.trim()
    const q = typed.toLowerCase()
    if (field !== null) {
      const list: Suggestion[] = (values ?? []).filter((v) => match === "HasWord" || v.value.startsWith(q)).map((v) => ({ kind: "value", value: v.value, count: v.count }))
      // Real values first, so Enter takes the best one; the typed text itself is the fallback.
      const shown = list.slice(0, 11)
      if (typed && !list.some((s) => s.kind === "value" && s.value === q)) shown.push({ kind: "value", value: typed })
      return shown
    }

    const list: Suggestion[] = []
    if (typed) list.push({ kind: "text", value: typed })
    const typedFilter = TYPED_FILTER.exec(typed)
    if (typedFilter) {
      const op: FieldMatch = typedFilter[2] === "!=" ? "NotEquals" : typedFilter[2] === "~" ? "HasWord" : "Equals"
      list.unshift({ kind: "filter", filter: { key: typedFilter[1], value: typedFilter[3].trim(), match: op } })
    }

    const keyMatches = (keys ?? [])
      .filter((k) => !q || k.toLowerCase().includes(q))
      .sort((a, b) => Number(!a.toLowerCase().startsWith(q)) - Number(!b.toLowerCase().startsWith(q)) || a.length - b.length)
      .slice(0, q ? 6 : 5)
    list.push(...keyMatches.map((key) => ({ kind: "field" as const, key })))

    const endpointMatches = endpointOptions
      .filter((e) => e.value && !state.endpoints.includes(e.value) && (!q || e.value.toLowerCase().includes(q)))
      .slice(0, 4)
    list.push(...endpointMatches.map((e) => ({ kind: "endpoint" as const, name: e.value, count: e.count })))
    return list
  }, [input, field, match, values, keys, endpointOptions, state.endpoints])

  const reset = () => {
    setInput("")
    setField(null)
    setMatch("Equals")
    setActive(0)
    setOpen(false)
  }

  const apply = (s: Suggestion) => {
    switch (s.kind) {
      case "text":
        onChange({ ...state, text: [...state.text, s.value] })
        reset()
        break
      case "filter":
        onChange({ ...state, filters: [...state.filters, s.filter] })
        reset()
        break
      case "endpoint":
        onChange({ ...state, endpoints: [...state.endpoints, s.name] })
        reset()
        break
      case "field":
        setField(s.key)
        setInput("")
        setActive(0)
        setOpen(true)
        break
      case "value":
        onChange({ ...state, filters: [...state.filters, { key: field!, value: s.value, match }] })
        reset()
        break
    }
    inputRef.current?.focus()
  }

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "ArrowDown") {
      e.preventDefault()
      setOpen(true)
      setActive((a) => Math.min(a + 1, suggestions.length - 1))
    } else if (e.key === "ArrowUp") {
      e.preventDefault()
      setActive((a) => Math.max(a - 1, 0))
    } else if (e.key === "Enter") {
      e.preventDefault()
      const pick = open ? suggestions[active] : undefined
      if (pick) apply(pick)
      else if (input.trim()) apply(field !== null ? { kind: "value", value: input.trim() } : { kind: "text", value: input.trim() })
    } else if (e.key === "Escape") {
      if (field !== null) reset()
      else setOpen(false)
    } else if (e.key === "Backspace" && input === "") {
      if (field !== null) reset()
      else removeLast()
    }
  }

  const removeLast = () => {
    if (state.text.length) onChange({ ...state, text: state.text.slice(0, -1) })
    else if (state.filters.length) onChange({ ...state, filters: state.filters.slice(0, -1) })
    else if (state.endpoints.length) onChange({ ...state, endpoints: state.endpoints.slice(0, -1) })
  }

  const hasChips = state.text.length + state.filters.length + state.endpoints.length > 0
  const showList = open && suggestions.length > 0

  return (
    <div ref={rootRef} className="relative min-w-0 flex-[999_1_560px]">
      <div
        className={clsx(
          "flex min-h-[42px] flex-wrap items-center gap-1.5 rounded-[10px] border bg-[var(--input-bg)] px-2.5 py-1.5 transition-colors",
          open ? "border-[var(--accent)] ring-3 ring-[var(--accent)]/15" : "border-[var(--border)]",
        )}
        onClick={() => inputRef.current?.focus()}
      >
        <Search size={15} className="shrink-0 text-[var(--text-muted)]" aria-hidden="true" />
        {state.endpoints.map((name, i) => (
          <Chip key={`e-${name}`} tone="endpoint" label="endpoint" onRemove={() => onChange({ ...state, endpoints: state.endpoints.filter((_, j) => j !== i) })}>
            <span className="font-mono">{name}</span>
          </Chip>
        ))}
        {state.filters.map((f, i) => (
          <Chip key={`f-${i}-${f.key}`} tone="field" onRemove={() => onChange({ ...state, filters: state.filters.filter((_, j) => j !== i) })} title={`${f.key} ${MATCH_LABEL[f.match]} ${f.value}`}>
            <span className="truncate font-mono text-[var(--text-muted)]">{f.key}</span>
            <span className="shrink-0 text-[var(--accent)]">{MATCH_LABEL[f.match]}</span>
            <span className="truncate font-mono">{f.value}</span>
          </Chip>
        ))}
        {state.text.map((t, i) => (
          <Chip key={`t-${i}-${t}`} tone="text" label="text" onRemove={() => onChange({ ...state, text: state.text.filter((_, j) => j !== i) })}>
            <span className="font-mono">{t}</span>
          </Chip>
        ))}
        {field !== null && (
          <span className="flex h-7 items-center gap-1.5 rounded-md border border-dashed border-[var(--accent)]/50 pr-1 pl-2 text-xs">
            <span className="font-mono text-[var(--text-muted)]">{field}</span>
            <span role="radiogroup" aria-label="Match" className="flex gap-0.5 rounded bg-[var(--surface-raised)] p-0.5">
              {MATCH_OPTIONS.map((o) => (
                <button
                  key={o.value}
                  type="button"
                  role="radio"
                  aria-checked={match === o.value}
                  title={o.title}
                  onClick={(e) => {
                    e.stopPropagation()
                    setMatch(o.value)
                    inputRef.current?.focus()
                  }}
                  className={clsx("h-5 rounded px-1.5 text-[11px]", match === o.value ? "bg-[var(--accent)] font-semibold text-[var(--accent-contrast)]" : "text-[var(--text-muted)] hover:text-[var(--text)]")}
                >
                  {o.label}
                </button>
              ))}
            </span>
          </span>
        )}
        <input
          ref={inputRef}
          value={input}
          onChange={(e) => {
            setInput(e.target.value)
            setActive(0)
            setOpen(true)
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={onKeyDown}
          placeholder={field !== null ? "Value…" : hasChips ? copy.more : copy.empty}
          aria-label={copy.aria}
          role="combobox"
          aria-expanded={showList}
          aria-controls={listId}
          aria-autocomplete="list"
          className="h-7 min-w-[140px] flex-1 bg-transparent text-[13px] text-[var(--text)] outline-none placeholder:text-[var(--text-dim)]"
        />
        {hasChips && (
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation()
              onChange({ text: [], filters: [], endpoints: [] })
              reset()
            }}
            className="h-6 shrink-0 rounded px-1.5 text-xs text-[var(--text-dim)] hover:bg-[var(--hover-strong)] hover:text-[var(--text)]"
          >
            Clear
          </button>
        )}
      </div>

      {showList && (
        <ul
          id={listId}
          role="listbox"
          aria-label="Suggestions"
          className="absolute top-full right-0 left-0 z-30 mt-1.5 max-h-[420px] overflow-y-auto rounded-[10px] border border-[var(--border-strong)] bg-[var(--surface-raised)] py-1 shadow-[var(--shadow-md)]"
        >
          {suggestions.map((s, i) => (
            <SuggestionRow key={i} suggestion={s} where={copy.where} query={input.trim()} active={i === active} previous={suggestions[i - 1]} field={field} onPick={() => apply(s)} onHover={() => setActive(i)} />
          ))}
          <li className="mt-1 flex flex-wrap gap-x-4 gap-y-1 border-t border-[var(--border)] px-3 pt-2 pb-1 text-[11px] text-[var(--text-dim)]">
            <span>↑↓ move</span>
            <span>Enter add</span>
            <span>⌫ remove last</span>
            {field === null && <span className="font-mono">key=value · key!=value · key~words · -word · "phrase"</span>}
          </li>
        </ul>
      )}
    </div>
  )
}

function SuggestionRow({
  suggestion: s,
  where,
  query,
  active,
  previous,
  field,
  onPick,
  onHover,
}: {
  suggestion: Suggestion
  where: string
  query: string
  active: boolean
  previous?: Suggestion
  field: string | null
  onPick: () => void
  onHover: () => void
}) {
  const group = s.kind === "field" ? "Fields" : s.kind === "endpoint" ? "Endpoints" : s.kind === "value" && previous?.kind !== "value" ? "Values in the last 24 hours" : null
  const showGroup = group && (s.kind !== previous?.kind || !previous)
  return (
    <>
      {showGroup && <li className="px-3 pt-2 pb-1 text-[10px] font-semibold tracking-wider text-[var(--text-dim)] uppercase">{group}</li>}
      <li
        role="option"
        aria-selected={active}
        onPointerDown={(e) => e.preventDefault()}
        onClick={onPick}
        onMouseEnter={onHover}
        className={clsx("flex cursor-pointer items-center gap-2.5 px-3 py-1.5 text-[13px]", active && "bg-[var(--accent)]/12")}
      >
        {s.kind === "text" && (
          <>
            <Search size={13} className="shrink-0 text-[var(--accent)]" aria-hidden="true" />
            <span className="min-w-0 flex-1 truncate">
              Search for <strong className="font-mono font-medium">{s.value}</strong>
            </span>
            <span className="shrink-0 text-[11px] text-[var(--text-dim)]">{where}</span>
          </>
        )}
        {s.kind === "filter" && (
          <>
            <CornerDownLeft size={13} className="shrink-0 text-[var(--accent)]" aria-hidden="true" />
            <span className="min-w-0 flex-1 truncate font-mono text-xs">
              <span className="text-[var(--text-muted)]">{s.filter.key}</span> <span className="text-[var(--accent)]">{MATCH_LABEL[s.filter.match]}</span> {s.filter.value}
            </span>
            <span className="shrink-0 text-[11px] text-[var(--text-dim)]">field filter</span>
          </>
        )}
        {s.kind === "field" && (
          <>
            <Braces size={13} className="shrink-0 text-[var(--violet)]" aria-hidden="true" />
            <span className="min-w-0 flex-1 truncate font-mono text-xs">
              <Highlight text={s.key} query={query} />
            </span>
          </>
        )}
        {s.kind === "endpoint" && (
          <>
            <Route size={13} className="shrink-0 text-[var(--svc-2)]" aria-hidden="true" />
            <span className="min-w-0 flex-1 truncate font-mono text-xs">
              <Highlight text={s.name} query={query} />
            </span>
            <span className="shrink-0 text-[11px] text-[var(--text-dim)] tabular-nums">{s.count.toLocaleString()}</span>
          </>
        )}
        {s.kind === "value" && (
          <>
            <span className="min-w-0 flex-1 truncate font-mono text-xs">
              {s.count === undefined ? (
                <>
                  <span className="text-[var(--text-dim)]">{field} = </span>
                  {s.value}
                </>
              ) : (
                <Highlight text={s.value} query={query} />
              )}
            </span>
            {s.count !== undefined && <span className="shrink-0 text-[11px] text-[var(--text-dim)] tabular-nums">{s.count.toLocaleString()}</span>}
          </>
        )}
      </li>
    </>
  )
}

function Chip({
  tone,
  label,
  title,
  children,
  onRemove,
}: {
  tone: "endpoint" | "field" | "text"
  label?: string
  title?: string
  children: ReactNode
  onRemove: () => void
}) {
  return (
    <span
      title={title}
      className={clsx(
        "flex h-7 max-w-full min-w-0 items-center gap-1.5 rounded-md border pr-0.5 pl-2 text-xs",
        tone === "endpoint" && "border-[var(--svc-2)]/35 bg-[var(--svc-2)]/10",
        tone === "field" && "border-[var(--accent)]/35 bg-[var(--accent)]/10",
        tone === "text" && "border-[var(--border-strong)] bg-[var(--hover)]",
      )}
    >
      {label && <span className={clsx("shrink-0 font-medium", tone === "endpoint" ? "text-[var(--svc-2)]" : "text-[var(--text-muted)]")}>{label}</span>}
      {children}
      <button
        type="button"
        onClick={(e) => {
          e.stopPropagation()
          onRemove()
        }}
        aria-label="Remove"
        className="grid h-6 w-6 shrink-0 place-items-center rounded text-[var(--text-muted)] hover:bg-[var(--hover-strong)] hover:text-[var(--text)]"
      >
        <X size={12} />
      </button>
    </span>
  )
}
