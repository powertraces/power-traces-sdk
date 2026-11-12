import type { ButtonHTMLAttributes, ReactNode } from "react"
import clsx from "clsx"
import { ChevronLeft, ChevronRight } from "lucide-react"

type ButtonVariant = "default" | "primary" | "ghost" | "danger"

const buttonVariants: Record<ButtonVariant, string> = {
  default: "border border-[var(--border)] bg-[var(--surface)] text-[var(--text)] hover:border-[var(--border-strong)] hover:bg-[var(--hover)]",
  primary: "border border-transparent bg-[var(--accent)] text-[var(--accent-contrast)] hover:bg-[var(--accent-strong)]",
  ghost: "border border-transparent text-[var(--text-muted)] hover:bg-[var(--hover-strong)] hover:text-[var(--text)]",
  danger: "border border-transparent text-[var(--text-muted)] hover:bg-[var(--error)]/10 hover:text-[var(--error)]",
}

export function Button({
  variant = "default",
  size = "md",
  className,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; size?: "sm" | "md" }) {
  return (
    <button
      {...props}
      className={clsx(
        "inline-flex shrink-0 items-center justify-center gap-1.5 rounded-lg font-medium whitespace-nowrap transition-colors disabled:pointer-events-none disabled:opacity-40",
        size === "sm" ? "h-7 px-2 text-xs" : "h-[34px] px-3 text-[13px]",
        buttonVariants[variant],
        className,
      )}
    />
  )
}

/** A row of mutually exclusive options — the time-range picker and similar. */
export function Segmented<T extends string>({
  value,
  options,
  onChange,
  label,
}: {
  value: T
  options: { value: T; label: string }[]
  onChange: (value: T) => void
  label: string
}) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex h-[34px] items-center gap-0.5 rounded-lg border border-[var(--border)] bg-[var(--input-bg)] p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          role="radio"
          aria-checked={o.value === value}
          onClick={() => onChange(o.value)}
          className={clsx(
            "h-full rounded-md px-2.5 text-xs font-medium transition-colors",
            o.value === value
              ? "bg-[var(--surface-raised)] text-[var(--text)] shadow-[var(--shadow-sm)] ring-1 ring-[var(--border)]"
              : "text-[var(--text-muted)] hover:text-[var(--text)]",
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

/** A filter bar card: controls wrap onto as many rows as the width needs. */
export function Toolbar({ children }: { children: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center gap-2 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-2.5 shadow-[var(--shadow-sm)]">
      {children}
    </div>
  )
}

/** "1–50 of 10,000+" plus prev/next. A capped total (the backend stops counting at a limit)
 * shows as a lower bound, and Next stays enabled while a full page came back. */
export function Pagination({
  page,
  pageSize,
  itemCount,
  totalCount,
  totalCountCapped,
  noun,
  onPage,
}: {
  page: number
  pageSize: number
  itemCount: number
  totalCount: number
  totalCountCapped?: boolean
  noun: string
  onPage: (page: number) => void
}) {
  const first = itemCount === 0 ? 0 : (page - 1) * pageSize + 1
  const last = (page - 1) * pageSize + itemCount
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize))
  const hasNext = totalCountCapped ? itemCount === pageSize : page < totalPages
  const total = `${totalCount.toLocaleString()}${totalCountCapped ? "+" : ""}`

  return (
    <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1.5 border-t border-[var(--border)] px-4 py-2.5 text-xs text-[var(--text-muted)]">
      <span className="tabular-nums">
        {first.toLocaleString()}–{last.toLocaleString()} of {total} {noun}
      </span>
      <div className="flex items-center gap-1">
        <Button variant="ghost" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)} aria-label="Previous page">
          <ChevronLeft size={14} /> Prev
        </Button>
        <span className="px-1 tabular-nums">
          Page {page.toLocaleString()}
          {totalCountCapped ? "" : ` of ${totalPages.toLocaleString()}`}
        </span>
        <Button variant="ghost" size="sm" disabled={!hasNext} onClick={() => onPage(page + 1)} aria-label="Next page">
          Next <ChevronRight size={14} />
        </Button>
      </div>
    </div>
  )
}

export function Kbd({ children }: { children: ReactNode }) {
  return (
    <kbd className="rounded border border-[var(--border)] bg-[var(--subtle)] px-1.5 py-0.5 font-mono text-[10px] text-[var(--text-dim)]">
      {children}
    </kbd>
  )
}
