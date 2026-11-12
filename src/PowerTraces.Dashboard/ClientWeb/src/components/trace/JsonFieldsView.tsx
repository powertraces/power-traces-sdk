import { createContext, useContext, useState, type ReactNode } from "react"
import { ChevronRight } from "lucide-react"
import clsx from "clsx"
import { CopyButton } from "@/components/CopyButton"
import { looksLikeFieldName, parseJsonContainer, type Json } from "@/lib/traceSearch"
import { FieldActions } from "./FieldActions"

const MAX_DEPTH = 6
const MAX_TABLE_COLUMNS = 12

const isObject = (v: Json): v is { [key: string]: Json } => v !== null && typeof v === "object" && !Array.isArray(v)
const isContainer = (v: Json): v is Json[] | { [key: string]: Json } => v !== null && typeof v === "object"
const scalarText = (v: Json) => (typeof v === "string" ? v : JSON.stringify(v))

/** A string field that itself holds JSON (a body inside a body) is shown — and indexed — unpacked. */
function unwrap(v: Json, depth: number): Json {
  if (typeof v === "string" && depth < MAX_DEPTH) return parseJsonContainer(v) ?? v
  return v
}

// Search keys follow the search index (`SearchTerms.Fields`, mirrored by `jsonFields`): a member
// adds `.name` (unless the name looks like data, e.g. an id used as a key), and an array of
// objects adds `[]` — so `body.lines[].sku`, whichever item it came from.
const memberKey = (parent: string, name: string) => (looksLikeFieldName(name) ? `${parent}.${name}` : parent)
const itemKey = (parent: string, item: Json) => (isContainer(item) ? `${parent}[]` : parent)

/** An array of objects reads best as a table: one row per item, one column per key. */
function asTable(items: Json[]): { columns: string[]; rows: { [key: string]: Json }[] } | null {
  if (items.length === 0 || !items.every(isObject)) return null
  const columns: string[] = []
  for (const item of items) for (const key of Object.keys(item)) if (!columns.includes(key)) columns.push(key)
  return columns.length > 0 && columns.length <= MAX_TABLE_COLUMNS ? { columns, rows: items } : null
}

/** A request/response body (or any JSON attribute) as its structure: objects as nested groups,
 * arrays of objects as tables, other arrays as numbered items. Every value can be copied and
 * searched by its field, exactly as the search index keys it. */
/** How many levels of nested groups start open (0: all collapsed). */
const OpenDepth = createContext(0)

export function JsonFieldsView({ attributeKey, value, openDepth = 0 }: { attributeKey: string; value: Json[] | { [key: string]: Json }; openDepth?: number }) {
  return (
    <OpenDepth.Provider value={openDepth}>
      <Container value={value} searchKey={attributeKey} depth={1} />
    </OpenDepth.Provider>
  )
}

/** A row's copy/search buttons: laid over the row's right edge while it is hovered or has focus,
 * so a long list reads as data rather than as a wall of icons, and values keep the full width
 * when the buttons are hidden. On touch screens they stay in the row. The row carries
 * `group/row relative`. */
export function RowActions({ children }: { children: ReactNode }) {
  return (
    <span
      className={clsx(
        "flex shrink-0 items-center gap-0.5 rounded-md bg-[var(--surface-raised)] pl-1 shadow-[-10px_0_10px_-2px_var(--surface)] ring-1 ring-[var(--border)]",
        "pointer-events-none absolute top-1/2 right-0.5 -translate-y-1/2 opacity-0 transition-opacity",
        "group-hover/row:pointer-events-auto group-hover/row:opacity-100 focus-within:pointer-events-auto focus-within:opacity-100",
        "[@media(hover:none)]:pointer-events-auto [@media(hover:none)]:static [@media(hover:none)]:translate-y-0 [@media(hover:none)]:opacity-100 [@media(hover:none)]:shadow-none [@media(hover:none)]:ring-0",
      )}
    >
      {children}
    </span>
  )
}

function Container({ value, searchKey, depth }: { value: Json[] | { [key: string]: Json }; searchKey: string; depth: number }) {
  if (Array.isArray(value)) {
    const table = asTable(value.map((v) => unwrap(v, depth)))
    if (table) return <ArrayTable table={table} searchKey={`${searchKey}[]`} depth={depth} />
    if (value.length === 0) return <Empty label="Empty list" />
    return (
      <div>
        {value.map((raw, i) => {
          const item = unwrap(raw, depth)
          return <Entry key={i} label={<span className="text-[var(--text-dim)]">[{i}]</span>} value={item} searchKey={itemKey(searchKey, item)} depth={depth} />
        })}
      </div>
    )
  }

  const entries = Object.entries(value)
  if (entries.length === 0) return <Empty label="Empty object" />
  return (
    <div>
      {entries.map(([name, raw]) => (
        <Entry key={name} label={name} value={unwrap(raw, depth)} searchKey={memberKey(searchKey, name)} depth={depth} />
      ))}
    </div>
  )
}

function Empty({ label }: { label: string }) {
  return <div className="px-2.5 py-1.5 text-xs text-[var(--text-dim)] italic">{label}</div>
}

/** One member or item: a value row, or a collapsible group for a nested object/array. */
function Entry({ label, value, searchKey, depth }: { label: ReactNode; value: Json; searchKey: string; depth: number }) {
  const openDepth = useContext(OpenDepth)
  const [open, setOpen] = useState(depth <= openDepth)

  if (!isContainer(value) || depth >= MAX_DEPTH) {
    const text = scalarText(value)
    return (
      <div className="group/row relative flex min-h-7 min-w-0 items-center gap-3 rounded pl-1 hover:bg-[var(--hover)]">
        <span className="w-[34%] shrink-0 truncate font-mono text-[11px] text-[var(--text-muted)]" title={typeof label === "string" ? label : undefined}>
          {label}
        </span>
        <span className={clsx("min-w-0 flex-1 truncate font-mono text-xs", value === null || typeof value !== "string" ? "text-[var(--info)]" : "text-[var(--text)]", value === null && "text-[var(--text-dim)]")} title={text}>
          {text}
        </span>
        <RowActions>
          <CopyButton value={text} label={`Copy ${searchKey}`} />
          <FieldActions fieldKey={searchKey} value={text} />
        </RowActions>
      </div>
    )
  }

  const count = Array.isArray(value) ? value.length : Object.keys(value).length
  return (
    <div>
      <div className="group/row relative flex min-h-7 min-w-0 items-center rounded hover:bg-[var(--hover)]">
        <button
          type="button"
          aria-expanded={open}
          onClick={() => setOpen((v) => !v)}
          className="flex min-w-0 flex-1 items-center gap-1.5 py-1 text-left"
        >
          <ChevronRight size={13} className={clsx("shrink-0 text-[var(--text-dim)] transition-transform", open && "rotate-90")} aria-hidden="true" />
          <span className="min-w-0 truncate font-mono text-[11px] text-[var(--text-muted)]">{label}</span>
          <span className="shrink-0 font-mono text-[11px] text-[var(--text-dim)] tabular-nums">
            {Array.isArray(value) ? `[${count}]` : `{${count}}`}
          </span>
        </button>
        <RowActions>
          <CopyButton value={JSON.stringify(value, null, 2)} label={`Copy ${searchKey} as JSON`} />
        </RowActions>
      </div>
      {open && (
        <div className="mb-1 pl-4">
          <Container value={value} searchKey={searchKey} depth={depth + 1} />
        </div>
      )}
    </div>
  )
}

/** Items as rows, keys as columns. Selecting a cell shows it in full below the table, with copy
 * and search (kept outside the scrolling table so the menu isn't clipped); a nested value in a
 * cell opens as its own structure there. */
function ArrayTable({ table, searchKey, depth }: { table: { columns: string[]; rows: { [key: string]: Json }[] }; searchKey: string; depth: number }) {
  const [selected, setSelected] = useState<{ row: number; column: string } | null>(null)
  const cell = selected ? unwrap(table.rows[selected.row]?.[selected.column] ?? null, depth) : undefined
  const cellKey = selected ? memberKey(searchKey, selected.column) : ""

  return (
    <div className="my-1 overflow-hidden rounded-md ring-1 ring-[var(--border)]">
      <div className="max-h-[360px] overflow-auto">
        <table className="w-full text-xs">
          {/* Opaque, so rows scrolling under the sticky header don't show through it. */}
          <thead className="sticky top-0 z-10 bg-[var(--surface-raised)] shadow-[0_1px_0_var(--border)]">
            <tr className="border-b border-[var(--border)] text-left">
              <th className="w-8 px-2 py-1.5 font-mono text-[10px] font-medium text-[var(--text-dim)]">#</th>
              {table.columns.map((c) => (
                <th key={c} className="px-2 py-1.5 font-mono text-[11px] font-medium whitespace-nowrap text-[var(--text-muted)]">
                  {c}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {table.rows.map((row, i) => (
              <tr key={i} className="border-b border-[var(--border)] last:border-0">
                <td className="px-2 py-1 font-mono text-[10px] text-[var(--text-dim)] tabular-nums">{i}</td>
                {table.columns.map((c) => {
                  const has = Object.prototype.hasOwnProperty.call(row, c)
                  const v = has ? unwrap(row[c], depth) : null
                  const text = !has ? "" : isContainer(v) ? (Array.isArray(v) ? `[${v.length}]` : `{${Object.keys(v).length}}`) : scalarText(v)
                  const active = selected?.row === i && selected.column === c
                  return (
                    <td key={c} className="p-0">
                      <button
                        type="button"
                        disabled={!has}
                        onClick={() => setSelected(active ? null : { row: i, column: c })}
                        title={has ? scalarText(v) : undefined}
                        className={clsx(
                          "block w-full max-w-[220px] truncate px-2 py-1 text-left font-mono",
                          !has ? "cursor-default" : active ? "bg-[var(--accent)]/12 text-[var(--text)]" : "text-[var(--text)] hover:bg-[var(--hover)]",
                          (v === null || isContainer(v)) && "text-[var(--text-dim)]",
                        )}
                      >
                        {text}
                      </button>
                    </td>
                  )
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {selected && cell !== undefined && (
        <div className="border-t border-[var(--border)] bg-[var(--subtle)]">
          {isContainer(cell) && depth < MAX_DEPTH ? (
            <div className="p-1.5">
              <div className="mb-1 px-1 font-mono text-[11px] text-[var(--text-muted)]">
                [{selected.row}].{selected.column}
              </div>
              <div className="pl-4">
                <Container value={cell} searchKey={cellKey} depth={depth + 1} />
              </div>
            </div>
          ) : (
            <div className="px-1.5 py-0.5">
              <Entry label={`[${selected.row}].${selected.column}`} value={cell} searchKey={cellKey} depth={depth} />
            </div>
          )}
        </div>
      )}
    </div>
  )
}
