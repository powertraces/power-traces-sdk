import { useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { Plus, SlidersHorizontal, X } from "lucide-react"
import { getAttributeKeys, type AttributeFilter, type AttributeScope } from "@/lib/api"
import { Combobox } from "@/components/Combobox"
import { Button, Segmented } from "@/components/ui"

interface AttributeFilterBuilderProps {
  scope: AttributeScope
  filters: AttributeFilter[]
  onChange: (filters: AttributeFilter[]) => void
}

/** Stacks up attribute constraints (ANDed together), each equals or contains: chips for the active
 * filters, and a row to add another — a searchable key picker (keys can also be typed freely),
 * the operator, and the value. */
export function AttributeFilterBuilder({ scope, filters, onChange }: AttributeFilterBuilderProps) {
  const { data: keys } = useQuery({ queryKey: ["attribute-keys", scope], queryFn: ({ signal }) => getAttributeKeys(scope, { signal }) })
  const [draftKey, setDraftKey] = useState("")
  const [draftValue, setDraftValue] = useState("")
  const [draftOp, setDraftOp] = useState<"equals" | "contains">("equals")

  const canAdd = draftKey.trim() !== "" && draftValue !== ""
  const addFilter = () => {
    if (!canAdd) return
    onChange([...filters, { key: draftKey.trim(), value: draftValue, contains: draftOp === "contains" }])
    setDraftValue("")
  }

  return (
    <div className="flex flex-col gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-2.5 shadow-[var(--shadow-sm)]">
      <form
        className="flex flex-wrap items-center gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          addFilter()
        }}
      >
        <span className="flex items-center gap-1.5 pr-1 text-[13px] font-medium text-[var(--text-muted)]">
          <SlidersHorizontal size={14} className="text-[var(--text-dim)]" />
          Attribute
        </span>
        <Combobox
          ariaLabel="Attribute key"
          options={keys ?? []}
          value={draftKey}
          onChange={setDraftKey}
          placeholder="Choose or type a key…"
          allowCustom
          className="w-full min-w-[200px] sm:w-72"
        />
        <Segmented
          label="Match"
          value={draftOp}
          onChange={setDraftOp}
          options={[
            { value: "equals", label: "equals" },
            { value: "contains", label: "contains" },
          ]}
        />
        <input
          value={draftValue}
          onChange={(e) => setDraftValue(e.target.value)}
          placeholder="Value"
          aria-label="Attribute value"
          className="control min-w-[160px] flex-1 font-mono text-xs sm:max-w-xs"
        />
        <Button type="submit" variant="primary" disabled={!canAdd}>
          <Plus size={13} /> Add filter
        </Button>
        {filters.length > 0 && (
          <Button type="button" variant="ghost" onClick={() => onChange([])}>
            Clear all
          </Button>
        )}
      </form>

      {filters.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5 border-t border-[var(--border)] pt-2">
          {filters.map((f, i) => (
            <span
              key={`${f.key}-${i}`}
              className="flex h-7 max-w-full items-center gap-1.5 rounded-md border border-[var(--accent)]/30 bg-[var(--accent)]/10 pr-1 pl-2.5 font-mono text-xs text-[var(--text)]"
              title={`${f.key} ${f.contains ? "contains" : "="} ${f.value}`}
            >
              <span className="truncate text-[var(--text-muted)]">{f.key}</span>
              <span className="shrink-0 font-sans text-[var(--accent)]">{f.contains ? "contains" : "="}</span>
              <span className="truncate">{f.value}</span>
              <button
                type="button"
                onClick={() => onChange(filters.filter((_, j) => j !== i))}
                aria-label={`Remove filter ${f.key}`}
                className="shrink-0 rounded p-0.5 hover:bg-[var(--accent)]/20"
              >
                <X size={12} />
              </button>
            </span>
          ))}
        </div>
      )}
    </div>
  )
}
