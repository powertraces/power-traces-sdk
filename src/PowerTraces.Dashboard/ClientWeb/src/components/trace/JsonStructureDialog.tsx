import { useEffect, useId, useRef } from "react"
import { createPortal } from "react-dom"
import { X } from "lucide-react"
import { Button } from "@/components/ui"
import { CopyButton } from "@/components/CopyButton"
import type { Json } from "@/lib/traceSearch"
import { JsonFieldsView } from "./JsonFieldsView"

const formatBytes = (n: number) => (n < 1024 ? `${n} bytes` : n < 1024 * 1024 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`)

/** A JSON attribute's structure — tables for arrays of objects, groups for nested values, the
 * first two levels open — in a dialog with room for it. Closes on Escape, a click outside, ✕ or
 * Close; Copy JSON copies the value as recorded. */
export function JsonStructureDialog({
  attributeKey,
  text,
  value,
  onClose,
}: {
  attributeKey: string
  text: string
  value: Json[] | { [key: string]: Json }
  onClose: () => void
}) {
  const titleId = useId()
  const panelRef = useRef<HTMLDivElement>(null)
  const count = Array.isArray(value) ? value.length : Object.keys(value).length
  const summary = Array.isArray(value) ? `${count} item${count === 1 ? "" : "s"}` : `${count} field${count === 1 ? "" : "s"}`

  // The latest onClose, so the effect below runs once per opening (focus moves in, then back).
  const onCloseRef = useRef(onClose)
  useEffect(() => {
    onCloseRef.current = onClose
  })

  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    panelRef.current?.focus()
    const onKeyDown = (e: KeyboardEvent) => e.key === "Escape" && onCloseRef.current()
    document.addEventListener("keydown", onKeyDown)
    return () => {
      document.removeEventListener("keydown", onKeyDown)
      previous?.focus()
    }
  }, [])

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/45 p-0 sm:p-6" onPointerDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        className="flex h-full w-full flex-col overflow-hidden bg-[var(--surface)] shadow-[var(--shadow-md)] outline-none sm:h-auto sm:max-h-[85vh] sm:w-[min(900px,100%)] sm:rounded-xl sm:ring-1 sm:ring-[var(--border)]"
      >
        <header className="flex shrink-0 items-center gap-2.5 border-b border-[var(--border)] px-4 py-3">
          <h2 id={titleId} className="min-w-0 truncate font-mono text-[13px] font-semibold text-[var(--text)]" title={attributeKey}>
            {attributeKey}
          </h2>
          <span className="shrink-0 text-xs text-[var(--text-dim)]">{summary}</span>
          <Button variant="ghost" size="sm" onClick={onClose} aria-label="Close" className="ml-auto">
            <X size={15} />
          </Button>
        </header>

        <div className="min-h-0 flex-1 overflow-auto px-4 py-2">
          <JsonFieldsView attributeKey={attributeKey} value={value} openDepth={2} />
        </div>

        <footer className="flex shrink-0 items-center gap-2 border-t border-[var(--border)] bg-[var(--surface-raised)] px-4 py-2.5">
          <span className="text-[11px] text-[var(--text-dim)]">
            {summary} · {formatBytes(new TextEncoder().encode(text).length)}
          </span>
          <CopyButton value={text} label="Copy JSON" showLabel className="ml-auto h-7 border border-[var(--border)] bg-[var(--surface)] px-2.5 text-[var(--text-muted)]" />
          <Button variant="primary" size="sm" onClick={onClose}>
            Close
          </Button>
        </footer>
      </div>
    </div>,
    document.body,
  )
}
