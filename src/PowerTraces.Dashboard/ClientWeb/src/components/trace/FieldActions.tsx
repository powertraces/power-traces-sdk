import { useEffect, useRef, useState, type CSSProperties } from "react"
import { createPortal } from "react-dom"
import { Link } from "react-router-dom"
import { Filter, FilterX, MoreHorizontal, Search } from "lucide-react"
import clsx from "clsx"
import { MAX_EXACT_VALUE_LENGTH, tracesHref } from "@/lib/traceSearch"

/** The "search by this" menu on any field of the trace page: traces where the field has this value,
 * traces without it, and traces mentioning the value anywhere. Each opens the Traces page with the
 * search already filled in.
 *
 * The menu is rendered in a portal with fixed positioning next to its button — inside a list, the
 * rows after this one (and any scrolling or clipping container: the span panel, a table, the
 * structure dialog) would otherwise cover it and take its clicks. It opens upward when there's
 * no room below, and closes on scroll, resize, Escape or a click elsewhere. */
export function FieldActions({ fieldKey, value, className }: { fieldKey: string; value: string; className?: string }) {
  const [open, setOpen] = useState(false)
  const [position, setPosition] = useState<CSSProperties | null>(null)
  const rootRef = useRef<HTMLDivElement>(null)
  const buttonRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onPointerDown = (e: PointerEvent) => {
      const target = e.target as Node
      if (!rootRef.current?.contains(target) && !menuRef.current?.contains(target)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false)
    // Scrolling inside the menu itself is fine; anything else moves the button away from it.
    const onScroll = (e: Event) => !menuRef.current?.contains(e.target as Node) && setOpen(false)
    const onResize = () => setOpen(false)
    document.addEventListener("pointerdown", onPointerDown)
    document.addEventListener("keydown", onKey)
    window.addEventListener("scroll", onScroll, true)
    window.addEventListener("resize", onResize)
    return () => {
      document.removeEventListener("pointerdown", onPointerDown)
      document.removeEventListener("keydown", onKey)
      window.removeEventListener("scroll", onScroll, true)
      window.removeEventListener("resize", onResize)
    }
  }, [open])


  const exact = value.length <= MAX_EXACT_VALUE_LENGTH
  // A long value is searched by its first words.
  const words = exact ? value : value.split(/\s+/).slice(0, 6).join(" ").slice(0, MAX_EXACT_VALUE_LENGTH)
  const items = [
    exact && { to: tracesHref({ filters: [{ key: fieldKey, value, match: "Equals" }] }), icon: Filter, label: "Traces where this field equals this", detail: `${fieldKey} = ${value}` },
    exact && { to: tracesHref({ filters: [{ key: fieldKey, value, match: "NotEquals" }] }), icon: FilterX, label: "Traces without this value", detail: `${fieldKey} ≠ ${value}` },
    !exact && { to: tracesHref({ filters: [{ key: fieldKey, value: words, match: "HasWord" }] }), icon: Filter, label: "Traces where this field has these words", detail: `${fieldKey} has ${words}` },
    { to: tracesHref({ q: exact && !/\s/.test(value) ? value : `"${words}"` }), icon: Search, label: "Traces mentioning this anywhere", detail: words },
  ].filter(Boolean) as { to: string; icon: typeof Filter; label: string; detail: string }[]

  // Under the button and right-aligned with it, or above it when it wouldn't fit below. Each item
  // is 54px tall, plus the menu's padding and border.
  const toggle = () => {
    if (open || !buttonRef.current) {
      setOpen(false)
      return
    }
    const button = buttonRef.current.getBoundingClientRect()
    const height = items.length * 54 + 10
    const gap = 4
    const below = button.bottom + gap + height <= window.innerHeight - 8 || button.top - gap - height < 8
    setPosition({
      right: Math.max(8, window.innerWidth - button.right),
      ...(below ? { top: button.bottom + gap } : { bottom: window.innerHeight - button.top + gap }),
    })
    setOpen(true)
  }

  return (
    <div ref={rootRef} className={clsx("relative", className)}>
      <button
        ref={buttonRef}
        type="button"
        aria-label={`Search traces by ${fieldKey}`}
        aria-expanded={open}
        title="Search traces by this value"
        onClick={toggle}
        className={clsx(
          "grid h-7 w-7 place-items-center rounded-md text-[var(--text-dim)] transition-colors hover:bg-[var(--hover-strong)] hover:text-[var(--text)]",
          open && "bg-[var(--hover-strong)] text-[var(--text)]",
        )}
      >
        <MoreHorizontal size={14} />
      </button>
      {open && position && createPortal(
        <div ref={menuRef} role="menu" style={position} className="fixed z-[60] flex w-80 max-w-[calc(100vw-1rem)] flex-col rounded-[10px] border border-[var(--border-strong)] bg-[var(--surface-raised)] p-1 shadow-[var(--shadow-md)]">
          {items.map((item) => (
            <Link
              key={item.label}
              role="menuitem"
              to={item.to}
              onClick={() => setOpen(false)}
              className="flex items-start gap-2.5 rounded-md px-2.5 py-2 hover:bg-[var(--hover-strong)]"
            >
              <item.icon size={14} className="mt-0.5 shrink-0 text-[var(--accent)]" aria-hidden="true" />
              <span className="flex min-w-0 flex-col gap-0.5">
                <span className="text-[13px] text-[var(--text)]">{item.label}</span>
                <span className="truncate font-mono text-[11px] text-[var(--text-muted)]">{item.detail}</span>
              </span>
            </Link>
          ))}
        </div>,
        document.body,
      )}
    </div>
  )
}
