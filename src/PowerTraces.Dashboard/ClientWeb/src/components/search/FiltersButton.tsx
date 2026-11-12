import { useEffect, useRef, useState, type ReactNode } from "react"
import { SlidersHorizontal } from "lucide-react"
import clsx from "clsx"

/** Where a list page's filter panel goes when the content area is too narrow to keep it beside
 * the table: a button (next to Columns) that opens the panel as a popover. Closes on Escape or a
 * click outside. */
export function FiltersButton({ active, className, children }: { active: number; className?: string; children: ReactNode }) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onPointerDown = (e: PointerEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKeyDown = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false)
    document.addEventListener("pointerdown", onPointerDown)
    document.addEventListener("keydown", onKeyDown)
    return () => {
      document.removeEventListener("pointerdown", onPointerDown)
      document.removeEventListener("keydown", onKeyDown)
    }
  }, [open])

  return (
    <div ref={rootRef} className={clsx("relative", className)}>
      <button
        type="button"
        aria-expanded={open}
        aria-haspopup="dialog"
        onClick={() => setOpen((v) => !v)}
        className={clsx(
          "flex h-[34px] items-center gap-1.5 rounded-lg border px-3 text-[13px] font-medium transition-colors",
          open || active > 0
            ? "border-[var(--accent)]/40 bg-[var(--accent)]/10 text-[var(--text)]"
            : "border-[var(--border)] bg-[var(--surface)] text-[var(--text-muted)] hover:border-[var(--border-strong)] hover:text-[var(--text)]",
        )}
      >
        <SlidersHorizontal size={13} />
        Filters{active > 0 ? ` (${active})` : ""}
      </button>
      {open && (
        <div role="dialog" aria-label="Filters" className="absolute right-0 z-30 mt-1.5 max-h-[min(70vh,560px)] w-[min(20rem,calc(100vw-2rem))] overflow-y-auto rounded-xl shadow-[var(--shadow-md)]">
          {children}
        </div>
      )}
    </div>
  )
}
