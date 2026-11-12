import { useEffect, useState } from "react"
import { Check, Copy } from "lucide-react"
import clsx from "clsx"
import { copyText } from "@/lib/clipboard"

/** A small copy-to-clipboard button that confirms with a check mark. Stops click propagation so it
 * can sit inside clickable rows. */
export function CopyButton({
  value,
  label = "Copy",
  className,
  showLabel = false,
}: {
  value: string
  label?: string
  className?: string
  showLabel?: boolean
}) {
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (!copied) return
    const timer = setTimeout(() => setCopied(false), 1500)
    return () => clearTimeout(timer)
  }, [copied])

  return (
    <button
      type="button"
      title={copied ? "Copied" : label}
      aria-label={label}
      onClick={async (e) => {
        e.stopPropagation()
        if (await copyText(value)) setCopied(true)
      }}
      className={clsx(
        "inline-flex shrink-0 items-center gap-1 rounded-md p-1 text-[var(--text-dim)] transition-colors hover:bg-[var(--hover-strong)] hover:text-[var(--text)]",
        copied && "text-[var(--success)] hover:text-[var(--success)]",
        className,
      )}
    >
      {copied ? <Check size={12} /> : <Copy size={12} />}
      {showLabel && <span className="text-xs">{copied ? "Copied" : label}</span>}
    </button>
  )
}
