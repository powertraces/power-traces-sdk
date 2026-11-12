import { useMemo, useState, type ReactNode } from "react"
import { ChevronRight, Eye, EyeOff, Lock, Network } from "lucide-react"
import clsx from "clsx"
import { CodeBlock } from "@/components/CodeBlock"
import { CopyButton } from "@/components/CopyButton"
import { parseJsonContainer } from "@/lib/traceSearch"
import { ATTR_NAMESPACE_LABELS, groupAttrs, isSensitiveAttr } from "./traceHelpers"
import { FieldActions } from "./FieldActions"
import { RowActions } from "./JsonFieldsView"
import { JsonStructureDialog } from "./JsonStructureDialog"

/** Values longer than this (or spanning lines) start folded to one line. */
const INLINE_LIMIT = 90

/** Attribute values arrive as JSON values — strings as-is, everything else (numbers, booleans,
 * arrays, objects) as its JSON text, which is also what gets copied. */
function valueText(v: unknown): string {
  return typeof v === "string" ? v : JSON.stringify(v)
}

/** A span's attributes as one quiet key/value list, grouped by namespace (`http.*`, `url.*`, …).
 * Copy and search appear on hover; JSON bodies and long values fold to one line and open in
 * place as raw text (everything starts folded); a JSON value's structure opens in a dialog. */
export function AttributeList({ attributes }: { attributes: [string, unknown][] }) {
  // A namespace with a single attribute isn't worth a heading: it joins "Other", full key shown.
  const groups = useMemo(() => {
    const list: [string, [string, unknown][]][] = []
    const other: [string, unknown][] = []
    for (const [ns, entries] of groupAttrs(attributes)) {
      if (ns === "other" || entries.length === 1) other.push(...entries)
      else list.push([ns, entries])
    }
    if (other.length) list.push(["other", other])
    return list
  }, [attributes])
  const showHeaders = groups.length > 1

  return (
    <div className="flex flex-col gap-4">
      {groups.map(([ns, entries]) => (
        <section key={ns} aria-label={ns === "other" ? "Other attributes" : `${ns} attributes`}>
          {showHeaders && (
            <h4 className="mb-1 flex items-center gap-2 px-1 text-[10px] font-semibold tracking-wider text-[var(--text-dim)] uppercase">
              {ns === "other" ? "Other" : namespaceLabel(ns)}
              <span className="font-normal tabular-nums">{entries.length}</span>
            </h4>
          )}
          <div className="divide-y divide-[var(--border)] border-y border-[var(--border)]">
            {entries.map(([key, value]) => (
              <AttributeRow key={key} attributeKey={key} label={showHeaders && ns !== "other" ? key.slice(ns.length + 1) : key} value={valueText(value)} />
            ))}
          </div>
        </section>
      ))}
    </div>
  )
}

function namespaceLabel(ns: string): string {
  const known = ATTR_NAMESPACE_LABELS[ns]
  if (known) return known
  const words = ns.replace(/_/g, " ")
  return words.charAt(0).toUpperCase() + words.slice(1)
}

function AttributeRow({ attributeKey: k, label, value }: { attributeKey: string; label: string; value: string }) {
  const json = useMemo(() => parseJsonContainer(value), [value])
  const sensitive = isSensitiveAttr(k)
  const long = json === null && (value.length > INLINE_LIMIT || value.includes("\n"))
  const foldable = !sensitive && (json !== null || long)
  const [open, setOpen] = useState(false)
  const [structure, setStructure] = useState(false)

  const summary = json === null ? value : Array.isArray(json) ? `${json.length} item${json.length === 1 ? "" : "s"}` : `${Object.keys(json).length} field${Object.keys(json).length === 1 ? "" : "s"}`

  return (
    <div>
      <div className={clsx("group/row relative flex min-h-8 min-w-0 items-center gap-3 px-1", foldable && "cursor-pointer hover:bg-[var(--hover)]")} onClick={foldable ? () => setOpen((v) => !v) : undefined}>
        <span className="flex w-[38%] shrink-0 items-center gap-1 font-mono text-[11px] text-[var(--text-muted)]" title={k}>
          {foldable ? (
            <ChevronRight size={12} className={clsx("shrink-0 text-[var(--text-dim)] transition-transform", open && "rotate-90")} aria-hidden="true" />
          ) : (
            <span className="w-3 shrink-0" />
          )}
          <span className="truncate">{label}</span>
          {sensitive && <Lock size={10} className="shrink-0 text-[var(--warn)]" aria-label="Sensitive" />}
        </span>
        <span className="min-w-0 flex-1">
          {sensitive ? (
            <MaskedValue value={value} />
          ) : foldable ? (
            <button
              type="button"
              aria-expanded={open}
              aria-label={`${open ? "Collapse" : "Expand"} ${k}`}
              className={clsx("block w-full truncate text-left font-mono text-xs", json !== null ? "text-[var(--text-dim)]" : "text-[var(--text)]")}
              onClick={(e) => {
                e.stopPropagation()
                setOpen((v) => !v)
              }}
            >
              {open && long ? <span className="text-[var(--text-dim)]">{value.length.toLocaleString()} characters</span> : summary}
            </button>
          ) : (
            <span className="block truncate font-mono text-xs text-[var(--text)]" title={value}>
              {value}
            </span>
          )}
        </span>
        {json !== null && !sensitive ? (
          // A JSON row keeps its buttons in the row: Structure is always there, copy on hover.
          <span className="flex shrink-0 items-center gap-0.5" onClick={(e) => e.stopPropagation()}>
            <span className="opacity-0 transition-opacity group-hover/row:opacity-100 focus-within:opacity-100 [@media(hover:none)]:opacity-100">
              <CopyButton value={value} label={`Copy ${k}`} />
            </span>
            <button
              type="button"
              onClick={() => setStructure(true)}
              className="inline-flex h-6 items-center gap-1 rounded-md border border-[var(--border)] bg-[var(--surface)] px-2 text-[11px] font-medium text-[var(--text-muted)] transition-colors hover:border-[var(--border-strong)] hover:text-[var(--text)]"
            >
              <Network size={11} aria-hidden="true" />
              Structure
            </button>
          </span>
        ) : (
          <RowActions>
            <CopyButton value={value} label={`Copy ${k}`} />
            {!sensitive && <FieldActions fieldKey={k} value={value} />}
          </RowActions>
        )}
      </div>
      {foldable && open && <CodeBlock value={value} attributeKey={k} bare className="text-[var(--text)]" />}
      {structure && json !== null && <JsonStructureDialog attributeKey={k} text={value} value={json} onClose={() => setStructure(false)} />}
    </div>
  )
}

function MaskedValue({ value }: { value: string }): ReactNode {
  const [revealed, setRevealed] = useState(false)
  return (
    <span className="flex min-w-0 items-center gap-1.5 font-mono text-xs">
      {revealed ? (
        <span className="truncate text-[var(--text)]" title={value}>
          {value}
        </span>
      ) : (
        <span className="tracking-widest text-[var(--text-dim)] select-none">••••••••••••</span>
      )}
      <button
        type="button"
        className="shrink-0 text-[var(--text-dim)] transition-colors hover:text-[var(--text)]"
        title={revealed ? "Hide value" : "Reveal value"}
        aria-label={revealed ? "Hide value" : "Reveal value"}
        onClick={() => setRevealed((v) => !v)}
      >
        {revealed ? <EyeOff size={12} /> : <Eye size={12} />}
      </button>
    </span>
  )
}
