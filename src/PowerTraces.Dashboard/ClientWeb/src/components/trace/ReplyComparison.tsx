import { Check, Undo2, X } from "lucide-react"
import clsx from "clsx"
import type { ReplyComparisonDto } from "@/lib/api"
import { Button } from "@/components/ui"

type Verdict = "failure" | "fine" | null

/** "data.id:string" → ["data.id", "string"]; a whole-body path (":text") has no name. */
function splitPath(path: string): [string, string] {
  const i = path.lastIndexOf(":")
  return i < 0 ? [path, ""] : [path.slice(0, i) || "(whole reply)", path.slice(i + 1)]
}

function PathList({ paths, tone }: { paths: { path: string; mark?: "+" | "−" }[]; tone?: "warn" | "error" }) {
  if (paths.length === 0) return <p className="text-xs text-[var(--text-dim)]">Nothing</p>
  return (
    <ul className="flex flex-col gap-0.5 font-mono text-[11.5px] leading-relaxed">
      {paths.map(({ path, mark }) => {
        const [name, type] = splitPath(path)
        return (
          <li
            key={`${mark ?? ""}${path}`}
            className={clsx(
              "min-w-0 truncate",
              mark === "+" && "text-[var(--warn)]",
              mark === "−" && "text-[var(--error)] line-through decoration-[var(--error)]/60",
              !mark && tone === undefined && "text-[var(--text)]",
            )}
            title={path}
          >
            {mark && <span className="mr-1 no-underline">{mark}</span>}
            {name}
            <span className="text-[var(--text-dim)]"> · {type}</span>
          </li>
        )
      })}
    </ul>
  )
}

/** This reply next to the call's usual successful replies: fields that are usually there and
 * aren't, and fields that usually aren't there and are. Says nothing about what fields mean. */
export function ReplyComparisonView({ comparison: cmp }: { comparison: ReplyComparisonDto }) {
  const same = cmp.missing.length === 0 && cmp.added.length === 0
  const kept = cmp.usual.filter((p) => !cmp.missing.includes(p))
  return (
    <section aria-label="Compared with a normal reply" className="overflow-hidden rounded-lg ring-1 ring-[var(--border)]">
      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 border-b border-[var(--border)] px-3 py-2">
        <h3 className="text-[13px] font-semibold">Compared with a normal reply</h3>
        <span className="text-[11px] text-[var(--text-dim)]">
          {cmp.sampleSize} successful calls to this step, last 7 days
        </span>
      </div>
      <div className="grid sm:grid-cols-2">
        <div className="flex min-w-0 flex-col gap-1.5 border-b border-[var(--border)] p-3 sm:border-r sm:border-b-0">
          <span className="text-[11px] font-bold tracking-wider text-[var(--text-muted)] uppercase">Usually</span>
          <PathList paths={cmp.usual.map((path) => ({ path }))} />
          {cmp.usualBytes != null && <span className="text-[11px] text-[var(--text-dim)]">About {cmp.usualBytes.toLocaleString()} bytes</span>}
        </div>
        <div className={clsx("flex min-w-0 flex-col gap-1.5 p-3", !same && "bg-[var(--warn)]/5")}>
          <span className={clsx("text-[11px] font-bold tracking-wider uppercase", same ? "text-[var(--text-muted)]" : "text-[var(--warn)]")}>This time</span>
          {same ? (
            <p className="text-xs">Same fields as {Math.round(cmp.sameShapeShare * 100)}% of the usual replies.</p>
          ) : (
            <PathList paths={[...cmp.added.map((path) => ({ path, mark: "+" as const })), ...cmp.missing.map((path) => ({ path, mark: "−" as const })), ...kept.map((path) => ({ path }))]} />
          )}
          {cmp.bytes != null && <span className="text-[11px] text-[var(--text-dim)]">{cmp.bytes.toLocaleString()} bytes</span>}
        </div>
      </div>
      {!same && (
        <p className="border-t border-[var(--border)] px-3 py-2 text-xs leading-relaxed text-[var(--text-muted)]">
          The field names don’t need to mean anything to us: what counts is that{" "}
          {cmp.missing.length > 0 ? `${cmp.missing.length} usual ${cmp.missing.length === 1 ? "field is" : "fields are"} missing` : "the reply has fields the usual ones don't"}.
        </p>
      )}
    </section>
  )
}

/** "Was this a failure?" — remembered for replies of this shape from this call, so the next one
 * is judged the same way. */
export function ReplyFeedback({ comparison: cmp, pending, onVerdict }: { comparison: ReplyComparisonDto; pending: boolean; onVerdict: (shapeKey: string, verdict: Verdict) => void }) {
  if (cmp.verdict) {
    return (
      <div className="flex flex-wrap items-center gap-2 text-xs text-[var(--text-muted)]">
        <span>{cmp.verdict === "failure" ? "You confirmed that replies like this mean failure." : "Replies like this were marked as fine, so they aren’t flagged."}</span>
        <Button variant="ghost" size="sm" disabled={pending} onClick={() => onVerdict(cmp.shapeKey, null)}>
          <Undo2 size={12} /> Undo
        </Button>
      </div>
    )
  }
  return (
    <div className="flex flex-wrap items-center gap-2 border-t border-[var(--warn)]/25 pt-2.5 text-xs text-[var(--text-muted)]">
      <span>Is this right? Your answer applies to replies like this one from now on.</span>
      <div className="flex gap-2">
        <Button size="sm" disabled={pending} onClick={() => onVerdict(cmp.shapeKey, "failure")}>
          <Check size={12} /> Yes, it failed
        </Button>
        <Button size="sm" disabled={pending} onClick={() => onVerdict(cmp.shapeKey, "fine")}>
          <X size={12} /> This was fine
        </Button>
      </div>
    </div>
  )
}
