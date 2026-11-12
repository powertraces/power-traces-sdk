import { useState } from "react"
import { ChevronRight, CircleAlert, CornerDownRight } from "lucide-react"
import clsx from "clsx"
import hljs from "highlight.js/lib/core"
import csharp from "highlight.js/lib/languages/csharp"
import { CopyButton } from "@/components/CopyButton"
import type { ExceptionFrame, ExceptionInfo, SpanException } from "./traceAnalysis"

hljs.registerLanguage("csharp", csharp)

/** `System.InvalidOperationException` → `InvalidOperationException`. */
const shortType = (type: string) => type.slice(type.lastIndexOf(".") + 1)
/** `Shop.Orders.OrderService.Submit` → `OrderService.Submit`. */
const shortMethod = (method: string) => method.split(".").slice(-2).join(".")
const fileName = (file: string) => file.slice(Math.max(file.lastIndexOf("/"), file.lastIndexOf("\\")) + 1)

/** One recorded exception: type, message and when, then — as stack frames — where. The app's own
 * frames are listed, framework frames fold into one row per run, and frames with a source
 * snapshot open to the code around the line that ran (the first one starts open). Causes (inner
 * exceptions) follow as "Caused by". Raw shows the stack trace text as recorded. */
export function ExceptionCard({ exception: ex, at }: { exception: SpanException; at: string }) {
  const [raw, setRaw] = useState(false)
  const hasFrames = ex.chain.some((e) => e.frames.length > 0)
  const [first, ...causes] = ex.chain
  const firstSnapshot = first?.frames.findIndex((f) => f.snapshot) ?? -1

  return (
    <div className="overflow-hidden rounded-lg ring-1 ring-[var(--error)]/25">
      <div className="flex gap-2.5 bg-[var(--error)]/8 px-3.5 py-3">
        <CircleAlert size={16} className="mt-px shrink-0 text-[var(--error)]" aria-hidden="true" />
        <div className="flex min-w-0 flex-1 flex-col gap-1">
          <div className="text-[13px] font-medium [overflow-wrap:anywhere] text-[var(--error)]" title={ex.type ?? undefined}>
            {ex.type ? shortType(ex.type) : "Exception"} <span className="font-normal text-[var(--text-muted)]">· at {at}</span>
          </div>
          {ex.message && <div className="font-mono text-xs leading-relaxed break-words text-[var(--text)]">{ex.message}</div>}
        </div>
        <div className="-my-1 flex shrink-0 items-start gap-1 self-start">
          {hasFrames && ex.stackTrace && (
            <button
              type="button"
              aria-pressed={raw}
              onClick={() => setRaw((v) => !v)}
              className={clsx("h-6 rounded-md px-1.5 text-[11px]", raw ? "bg-[var(--surface-raised)] text-[var(--text)] shadow-[var(--shadow-sm)]" : "text-[var(--text-muted)] hover:text-[var(--text)]")}
            >
              Raw
            </button>
          )}
          <CopyButton value={ex.stackTrace ?? ex.message ?? ""} label={ex.stackTrace ? "Copy stack trace" : "Copy exception message"} />
        </div>
      </div>

      {(raw || !hasFrames) && ex.stackTrace ? (
        <pre className="max-h-80 overflow-auto border-t border-[var(--error)]/15 bg-[var(--subtle)] px-3.5 py-2.5 font-mono text-[11px] leading-relaxed text-[var(--text-muted)]">{ex.stackTrace}</pre>
      ) : (
        hasFrames && (
          <div className="flex flex-col gap-2 border-t border-[var(--error)]/15 px-2 py-2">
            {first && <FrameList frames={first.frames} openIndex={firstSnapshot} />}
            {causes.map((cause, i) => (
              <Cause key={i} cause={cause} />
            ))}
          </div>
        )
      )}
    </div>
  )
}

function Cause({ cause }: { cause: ExceptionInfo }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="border-t border-[var(--border)] pt-2">
      <button type="button" aria-expanded={open} onClick={() => setOpen((v) => !v)} className="flex w-full min-w-0 items-start gap-1.5 rounded px-1 py-0.5 text-left hover:bg-[var(--hover)]">
        <CornerDownRight size={13} className="mt-0.5 shrink-0 text-[var(--text-dim)]" aria-hidden="true" />
        <span className="min-w-0 flex-1 text-xs">
          <span className="text-[var(--text-dim)]">Caused by </span>
          <span className="font-medium text-[var(--error)]" title={cause.type}>
            {shortType(cause.type)}
          </span>
          {cause.message && <span className="block truncate font-mono text-[11px] text-[var(--text-muted)]" title={cause.message}>{cause.message}</span>}
        </span>
        <ChevronRight size={13} className={clsx("mt-0.5 shrink-0 text-[var(--text-dim)] transition-transform", open && "rotate-90")} aria-hidden="true" />
      </button>
      {open && (
        <div className="mt-1">
          <FrameList frames={cause.frames} openIndex={-1} />
        </div>
      )}
    </div>
  )
}

/** The app's frames one by one; consecutive framework frames as one foldable row. */
function FrameList({ frames, openIndex }: { frames: ExceptionFrame[]; openIndex: number }) {
  const runs: ({ kind: "user"; frame: ExceptionFrame; index: number } | { kind: "framework"; frames: ExceptionFrame[] })[] = []
  frames.forEach((frame, index) => {
    const last = runs[runs.length - 1]
    if (frame.user) runs.push({ kind: "user", frame, index })
    else if (last?.kind === "framework") last.frames.push(frame)
    else runs.push({ kind: "framework", frames: [frame] })
  })

  return (
    <div className="flex flex-col">
      {runs.map((run, i) =>
        run.kind === "user" ? <UserFrame key={i} frame={run.frame} defaultOpen={run.index === openIndex} /> : <FrameworkFrames key={i} frames={run.frames} />,
      )}
    </div>
  )
}

function UserFrame({ frame, defaultOpen }: { frame: ExceptionFrame; defaultOpen: boolean }) {
  const [open, setOpen] = useState(defaultOpen)
  const snapshot = frame.snapshot
  const location = frame.file ? `${fileName(frame.file)}${frame.line ? `:${frame.line}` : ""}` : null

  return (
    <div>
      <button
        type="button"
        aria-expanded={snapshot ? open : undefined}
        disabled={!snapshot}
        onClick={() => setOpen((v) => !v)}
        className="flex min-h-7 w-full min-w-0 items-center gap-1.5 rounded px-1 text-left enabled:hover:bg-[var(--hover)]"
      >
        {snapshot ? (
          <ChevronRight size={12} className={clsx("shrink-0 text-[var(--text-dim)] transition-transform", open && "rotate-90")} aria-hidden="true" />
        ) : (
          <span className="w-3 shrink-0" />
        )}
        <span className="min-w-0 flex-1 truncate font-mono text-xs text-[var(--text)]" title={frame.method}>
          {shortMethod(frame.method)}
        </span>
        {location && (
          <span className="shrink-0 font-mono text-[11px] text-[var(--text-muted)]" title={frame.file ?? undefined}>
            {location}
          </span>
        )}
      </button>
      {snapshot && open && <Snippet snapshot={snapshot} line={frame.line} />}
    </div>
  )
}

function FrameworkFrames({ frames }: { frames: ExceptionFrame[] }) {
  const [open, setOpen] = useState(false)
  return (
    <div>
      <button type="button" aria-expanded={open} onClick={() => setOpen((v) => !v)} className="flex min-h-7 w-full items-center gap-1.5 rounded px-1 text-left text-[11px] text-[var(--text-dim)] hover:bg-[var(--hover)]">
        <ChevronRight size={12} className={clsx("shrink-0 transition-transform", open && "rotate-90")} aria-hidden="true" />
        {frames.length} framework frame{frames.length === 1 ? "" : "s"}
      </button>
      {open && (
        <div className="mb-1 ml-2.5 border-l border-[var(--border)] pl-2.5">
          {frames.map((f, i) => (
            <div key={i} className="truncate py-0.5 font-mono text-[11px] text-[var(--text-dim)]" title={f.method}>
              {f.method}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

/** The source around the line that ran, line-numbered, with that line marked. Highlighted a line
 * at a time, so a construct spanning lines may lose its colour but never breaks the layout. */
function Snippet({ snapshot, line }: { snapshot: { startLine: number; lines: string[] }; line: number | null }) {
  const width = String(snapshot.startLine + snapshot.lines.length - 1).length
  return (
    <div className="my-1 ml-4 overflow-x-auto rounded-md bg-[var(--subtle)] py-1.5 ring-1 ring-[var(--border)]">
      <pre className="min-w-fit font-mono text-xs leading-5">
        {snapshot.lines.map((text, i) => {
          const number = snapshot.startLine + i
          const current = number === line
          return (
            <div key={i} className={clsx("flex pr-3", current && "bg-[var(--error)]/12")}>
              <span className={clsx("w-4 shrink-0 text-center", current ? "text-[var(--error)]" : "text-transparent")} aria-hidden="true">
                ▶
              </span>
              <span className={clsx("shrink-0 pr-3 text-right select-none", current ? "text-[var(--error)]" : "text-[var(--text-dim)]")} style={{ width: `${width + 1}ch` }}>
                {number}
              </span>
              <code
                className="hljs language-csharp whitespace-pre !bg-transparent !p-0"
                // Safe: highlight.js escapes the source text and only adds its own spans.
                dangerouslySetInnerHTML={{ __html: hljs.highlight(text || " ", { language: "csharp" }).value }}
              />
            </div>
          )
        })}
      </pre>
    </div>
  )
}
