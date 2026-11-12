import { useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { Columns3, Search } from "lucide-react"
import { Highlight } from "@/components/Combobox"
import { getAttributeKeys, type AttributeScope } from "@/lib/api"

interface ColumnPickerProps {
  scope: AttributeScope
  selected: string[]
  onChange: (keys: string[]) => void
}

/** A zero-dependency disclosure (native `<details>`) for picking which attribute keys to show as
 * extra table columns — there's no dropdown/multi-select library in this project, and one isn't
 * warranted for a single checklist. */
export function ColumnPicker({ scope, selected, onChange }: ColumnPickerProps) {
  const { data: keys } = useQuery({ queryKey: ["attribute-keys", scope], queryFn: ({ signal }) => getAttributeKeys(scope, { signal }) })

  const [query, setQuery] = useState("")
  const visible = (keys ?? []).filter((k) => k.toLowerCase().includes(query.trim().toLowerCase()))

  const toggle = (key: string) => {
    onChange(selected.includes(key) ? selected.filter((k) => k !== key) : [...selected, key])
  }

  return (
    <details className="relative ml-auto">
      <summary className="flex h-[34px] cursor-pointer list-none items-center gap-1.5 rounded-lg border border-[var(--border)] bg-[var(--surface)] px-3 text-[13px] font-medium text-[var(--text-muted)] transition-colors hover:border-[var(--border-strong)] hover:text-[var(--text)]">
        <Columns3 size={13} />
        Columns{selected.length > 0 ? ` (${selected.length})` : ""}
      </summary>
      <div className="absolute right-0 z-20 mt-1.5 w-72 rounded-lg border border-[var(--border)] bg-[var(--surface-raised)] p-1.5 shadow-[var(--shadow-md)]">
        {!keys?.length ? (
          <div className="px-2 py-1.5 text-xs text-[var(--text-dim)]">No attribute keys found yet.</div>
        ) : (
          <>
          <div className="mb-1 flex items-center gap-2 border-b border-[var(--border)] px-2 pb-1.5">
            <Search size={13} className="shrink-0 text-[var(--text-dim)]" />
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search attributes…"
              aria-label="Search attribute columns"
              className="h-7 min-w-0 flex-1 bg-transparent text-xs outline-none placeholder:text-[var(--text-dim)]"
            />
            {selected.length > 0 && (
              <button type="button" onClick={() => onChange([])} className="shrink-0 text-[11px] text-[var(--text-dim)] hover:text-[var(--text)]">
                Clear
              </button>
            )}
          </div>
          <div className="flex max-h-72 flex-col gap-0.5 overflow-y-auto">
            {visible.length === 0 && <div className="px-2 py-1.5 text-xs text-[var(--text-dim)]">No matching attributes.</div>}
            {visible.map((key) => (
              <label key={key} className="flex cursor-pointer items-center gap-2 rounded px-2 py-1 text-xs hover:bg-[var(--hover-strong)]">
                <input
                  type="checkbox"
                  checked={selected.includes(key)}
                  onChange={() => toggle(key)}
                  className="accent-[var(--accent)]"
                />
                <span className="truncate font-mono">
                  <Highlight text={key} query={query.trim()} />
                </span>
              </label>
            ))}
          </div>
          </>
        )}
      </div>
    </details>
  )
}
