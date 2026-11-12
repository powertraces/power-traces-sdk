import { Eye } from "lucide-react"
import { Link } from "react-router-dom"

/** A row's explicit "open detail" affordance — deliberately not the whole row, so a click
 * anywhere else in the row (e.g. to select/copy text) doesn't accidentally navigate away. */
export function ViewButton({ to, label = "View" }: { to: string; label?: string }) {
  return (
    <Link
      to={to}
      title={label}
      aria-label={label}
      onClick={(e) => e.stopPropagation()}
      className="inline-flex h-7 w-7 items-center justify-center rounded-md text-[var(--text-dim)] transition-colors hover:bg-[var(--accent)]/10 hover:text-[var(--accent)]"
    >
      <Eye size={14} />
    </Link>
  )
}
