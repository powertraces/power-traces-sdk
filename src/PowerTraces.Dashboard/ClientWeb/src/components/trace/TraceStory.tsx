import { Fragment } from "react"
import { ArrowRight, CircleAlert, CircleCheck, Crosshair, TriangleAlert } from "lucide-react"
import clsx from "clsx"
import type { TraceDetail } from "@/lib/api"
import { Button } from "@/components/ui"
import { CopyButton } from "@/components/CopyButton"
import { verdictText, type JourneyStep, type Verdict } from "./story"

/** How many journey steps show before the middle ones fold into "+N more". */
const MAX_STEPS = 8

/** The trace in plain words: one sentence saying whether it worked and, if not, who failed and
 * why — then the services the request went through, with the one where it broke marked. */
export function TraceStory({
  trace,
  verdict,
  journey,
  onSelectSpan,
}: {
  trace: TraceDetail
  verdict: Verdict
  journey: JourneyStep[]
  onSelectSpan: (spanId: string) => void
}) {
  const failed = verdict.outcome === "failed"
  const soft = verdict.outcome === "soft"
  const name = trace.rootName ?? "The request"
  const why = (
    <>
      <span className={soft ? "text-[var(--warn)]" : "text-[var(--error)]"}>{verdict.culprit}</span> {verdict.happened}
      {verdict.reason ? (
        <>
          : <span className={soft ? "text-[var(--warn)]" : "text-[var(--error)]"}>{verdict.reason}</span>
        </>
      ) : null}
      .
    </>
  )

  return (
    <section aria-label="What happened" className="overflow-hidden rounded-xl border border-[var(--border)] bg-[var(--surface)] shadow-[var(--shadow-sm)]">
      <div className="flex flex-wrap items-start gap-x-4 gap-y-3 p-5">
        <span
          className={clsx(
            "flex h-10 w-10 shrink-0 items-center justify-center rounded-xl",
            failed ? "bg-[var(--error)]/12 text-[var(--error)]" : soft ? "bg-[var(--warn)]/12 text-[var(--warn)]" : "bg-[var(--success)]/12 text-[var(--success)]",
          )}
          aria-hidden="true"
        >
          {failed ? <CircleAlert size={20} /> : soft ? <TriangleAlert size={20} /> : <CircleCheck size={20} />}
        </span>
        <div className="flex min-w-0 flex-[999_1_480px] flex-col gap-1.5">
          <h2 className="text-lg leading-snug font-semibold">
            {failed ? (
              <>
                <span className="font-mono">{name}</span> failed because {why}
              </>
            ) : soft ? (
              <>
                <span className="font-mono">{name}</span> probably didn’t work: {why}
              </>
            ) : (
              <>
                Everything worked: <span className="font-mono">{name}</span> succeeded.
              </>
            )}
          </h2>
          {verdict.details.length > 0 && <p className="text-sm leading-relaxed text-[var(--text-muted)]">{verdict.details.join(" ")}</p>}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {verdict.focus && (
            <Button variant="primary" onClick={() => onSelectSpan(verdict.focus!.spanId)}>
              <Crosshair size={13} /> {soft ? "Show the reply" : "Show failing step"}
            </Button>
          )}
          <CopyButton
            value={verdictText(trace, verdict)}
            label="Copy summary"
            showLabel
            className="h-[34px] border border-[var(--border)] bg-[var(--surface)] px-3 text-[var(--text-muted)]"
          />
        </div>
      </div>

      {journey.length > 1 && (
        <div className="flex flex-col gap-2.5 border-t border-[var(--border)] px-5 py-4">
          <h3 className="text-[11px] font-semibold tracking-wider text-[var(--text-dim)] uppercase">The request’s journey</h3>
          <Journey steps={journey} onSelect={onSelectSpan} />
        </div>
      )}
    </section>
  )
}

function Journey({ steps, onSelect }: { steps: JourneyStep[]; onSelect: (spanId: string) => void }) {
  // Long journeys keep the first steps, the problem, and the last step.
  let shown: (JourneyStep | { more: number })[] = steps
  if (steps.length > MAX_STEPS) {
    const keep = new Set([0, 1, 2, steps.length - 1, ...steps.flatMap((s, i) => (s.state !== "ok" ? [i] : []))])
    shown = []
    let hidden = 0
    steps.forEach((s, i) => {
      if (keep.has(i)) {
        if (hidden) shown.push({ more: hidden })
        hidden = 0
        shown.push(s)
      } else hidden++
    })
  }

  return (
    <ol className="flex flex-wrap items-center gap-2">
      {shown.map((step, i) => (
        <Fragment key={"key" in step ? step.key : `more-${i}`}>
          {i > 0 && (
            <li aria-hidden="true" className="text-[var(--text-dim)]">
              <ArrowRight size={14} />
            </li>
          )}
          <li>
            {"more" in step ? (
              <span className="text-xs text-[var(--text-dim)]">+{step.more} more</span>
            ) : (
              <button
                type="button"
                onClick={() => onSelect(step.spanId)}
                title={step.external ? `${step.name} isn't traced; this is what its caller saw` : `Open ${step.name}’s first step`}
                className={clsx(
                  "flex min-w-32 flex-col items-start gap-0.5 rounded-lg px-3 py-2 text-left transition-colors",
                  step.state === "problem"
                    ? "border-2 border-[var(--error)] bg-[var(--error)]/8 hover:bg-[var(--error)]/12"
                    : step.state === "soft"
                      ? "border-2 border-[var(--warn)] bg-[var(--warn)]/8 hover:bg-[var(--warn)]/12"
                      : "border border-[var(--border)] bg-[var(--surface-raised)] hover:border-[var(--border-strong)]",
                  step.external && step.state !== "problem" && step.state !== "soft" && "border-dashed",
                )}
              >
                <span className="flex items-center gap-1.5 text-[13px] font-semibold">
                  <span
                    className={clsx(
                      "h-2 w-2 shrink-0",
                      step.state === "ok" ? "rounded-full bg-[var(--success)]" : step.state === "problem" ? "rounded-full bg-[var(--error)]" : "rotate-45 bg-[var(--warn)]",
                    )}
                    aria-hidden="true"
                  />
                  {step.name}
                </span>
                <span
                  className={clsx(
                    "text-xs",
                    step.state === "problem" ? "font-medium text-[var(--error)]" : step.state === "soft" ? "font-medium text-[var(--warn)]" : step.state === "affected" ? "text-[var(--warn)]" : "text-[var(--text-muted)]",
                  )}
                >
                  {step.note}
                </span>
              </button>
            )}
          </li>
        </Fragment>
      ))}
    </ol>
  )
}
