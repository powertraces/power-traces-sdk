import type { LucideIcon } from "lucide-react"
import type { ReactNode } from "react"
import { Inbox, Loader2 } from "lucide-react"

export function Panel({
  title,
  subtitle,
  icon: Icon,
  action,
  children,
  className = "",
}: {
  title?: string
  subtitle?: string
  icon?: LucideIcon
  action?: ReactNode
  children: ReactNode
  className?: string
}) {
  return (
    <section className={`overflow-hidden rounded-xl border border-[var(--border)] bg-[var(--surface)] shadow-[var(--shadow-sm)] ${className}`}>
      {title && (
        <header className="flex items-center justify-between gap-3 px-4 pt-3.5 pb-3">
          <div className="min-w-0">
            <h2 className="flex items-center gap-2 text-[13px] font-semibold text-[var(--text)]">
              {Icon && <Icon size={14} className="text-[var(--text-dim)]" />}
              {title}
            </h2>
            {subtitle && <p className="mt-0.5 text-xs text-[var(--text-dim)]">{subtitle}</p>}
          </div>
          {action}
        </header>
      )}
      {children}
    </section>
  )
}

export function EmptyState({ label, hint, icon: Icon = Inbox }: { label: string; hint?: string; icon?: LucideIcon }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-4 py-14 text-center">
      <span className="flex h-10 w-10 items-center justify-center rounded-full bg-[var(--subtle)] ring-1 ring-[var(--border)]">
        <Icon size={18} className="text-[var(--text-dim)]" />
      </span>
      <p className="text-sm font-medium text-[var(--text-muted)]">{label}</p>
      {hint && <p className="max-w-sm text-xs text-[var(--text-dim)]">{hint}</p>}
    </div>
  )
}

export function Loading({ label = "Loading…" }: { label?: string }) {
  return (
    <div className="flex items-center justify-center gap-2 py-14 text-sm text-[var(--text-dim)]">
      <Loader2 size={16} className="animate-spin" />
      {label}
    </div>
  )
}
