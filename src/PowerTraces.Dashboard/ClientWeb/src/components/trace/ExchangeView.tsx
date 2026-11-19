import type { ReactNode } from "react"
import { ArrowRight, Info, MessageSquareWarning } from "lucide-react"
import clsx from "clsx"
import { formatDuration } from "@/lib/format"
import { CodeBlock } from "@/components/CodeBlock"
import { CopyButton } from "@/components/CopyButton"
import { httpStatusInfo } from "./glossary"
import { readableMessage, type Exchange, type RecordedValue } from "./story"
import type { SoftFailure } from "./softFailures"
import { isSensitiveAttr, kindLabel } from "./traceHelpers"
import { serviceOf } from "./traceAnalysis"

const PREVIEW_LINES = 7

function pretty(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    return text
  }
}

function preview(text: string): string {
  const lines = pretty(text).split("\n")
  return lines.length > PREVIEW_LINES ? [...lines.slice(0, PREVIEW_LINES), "…"].join("\n") : lines.join("\n")
}

/** Which span a value came from, as a button that opens it. */
function SourceChip({ value, onSelect }: { value: { span: RecordedValue["span"] }; onSelect: (spanId: string) => void }) {
  const span = value.span
  return (
    <button
      type="button"
      onClick={() => onSelect(span.spanId)}
      title={`Recorded on “${span.name}” (${span.spanId})`}
      className="max-w-full truncate rounded-md border border-[var(--border)] px-1.5 py-0.5 font-mono text-[10px] text-[var(--text-dim)] hover:border-[var(--border-strong)] hover:text-[var(--text)]"
    >
      from {serviceOf(span)} · {kindLabel(span.kind).toLowerCase()}
    </button>
  )
}

function Headers({ headers }: { headers: RecordedValue[] }) {
  if (headers.length === 0) return null
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[11px] text-[var(--text-dim)]">Headers · {headers.length}</span>
      <dl className="flex flex-col gap-0.5 font-mono text-[11px] leading-relaxed">
        {headers.map((h) => (
          <div key={h.key} className="flex min-w-0 gap-1.5">
            <dt className="shrink-0 text-[var(--text-muted)]">{h.key}:</dt>
            <dd className="min-w-0 truncate text-[var(--text)]" title={isSensitiveAttr(h.key) ? undefined : h.text}>
              {isSensitiveAttr(h.key) ? <span className="text-[var(--text-dim)]">••••• hidden</span> : h.text}
            </dd>
          </div>
        ))}
      </dl>
    </div>
  )
}

function Body({ label, body, full }: { label: string; body: RecordedValue | null; full: boolean }) {
  if (!body) return <p className="text-xs text-[var(--text-dim)] italic">{label} not recorded</p>
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <div className="flex items-center gap-1">
        <span className="text-[11px] text-[var(--text-dim)]">{label}</span>
        <CopyButton value={body.text} label={`Copy ${label.toLowerCase()}`} className="-my-1 ml-auto" />
      </div>
      {full ? (
        <div className="overflow-hidden rounded-lg ring-1 ring-[var(--border)]">
          <CodeBlock value={body.text} attributeKey={body.key} bare />
        </div>
      ) : (
        <pre className="max-h-40 overflow-hidden rounded-lg bg-[var(--subtle)] px-2.5 py-2 font-mono text-[11px] leading-relaxed break-all whitespace-pre-wrap text-[var(--text-muted)] ring-1 ring-[var(--border)]">
          {preview(body.text)}
        </pre>
      )}
    </div>
  )
}

type Tone = "neutral" | "error" | "warn"

function Side({ title, tone, chip, children }: { title: string; tone: Tone; chip: ReactNode; children: ReactNode }) {
  return (
    <div
      className={clsx(
        "flex min-w-0 flex-col gap-2.5 rounded-lg p-3 ring-1",
        tone === "error" ? "bg-[var(--error)]/6 ring-[var(--error)]/40" : tone === "warn" ? "bg-[var(--warn)]/6 ring-[var(--warn)]/50" : "bg-[var(--surface-raised)] ring-[var(--border)]",
      )}
    >
      <div className="flex min-w-0 flex-col items-start gap-1">
        <span
          className={clsx("text-[11px] font-bold tracking-wider uppercase", tone === "error" ? "text-[var(--error)]" : tone === "warn" ? "text-[var(--warn)]" : "text-[var(--text-muted)]")}
        >
          {title}
        </span>
        {chip}
      </div>
      {children}
    </div>
  )
}

/** A call as one conversation: what was sent next to what came back, whichever span recorded
 * each half. `full` adds headers and complete bodies; the compact form previews them. */
export function ExchangeView({
  exchange: ex,
  soft,
  full,
  onSelectSpan,
}: {
  exchange: Exchange
  soft?: SoftFailure | null
  full: boolean
  onSelectSpan: (spanId: string) => void
}) {
  const db = ex.kind === "db"
  const failed = ex.failed
  const info = ex.kind === "http" ? httpStatusInfo(ex.status) : null
  const message = failed ? readableMessage(ex.responseBody?.text) : (soft?.reason ?? null)
  const backTone: Tone = failed ? "error" : soft ? "warn" : "neutral"
  const sentFrom = ex.query ?? ex.requestBody ?? (ex.client ? { span: ex.client } : ex.server ? { span: ex.server } : null)
  const backFrom = ex.responseBody ?? (ex.server ? { span: ex.server } : ex.client ? { span: ex.client } : null)

  return (
    <div className="flex flex-col gap-3">
      {full && (
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[13px]">
          <span className="font-semibold">{ex.caller ?? "Caller"}</span>
          <ArrowRight size={13} className="text-[var(--text-dim)]" aria-hidden="true" />
          <span className="font-semibold">{ex.callee}</span>
          {(ex.method || ex.target) && <span className="min-w-0 truncate font-mono text-xs text-[var(--text-muted)]">{[ex.method, ex.target].filter(Boolean).join(" ")}</span>}
          <span className="ml-auto text-xs text-[var(--text-muted)]">{formatDuration(ex.durationMs)} round trip</span>
        </div>
      )}

      <div className={clsx("grid gap-2", full ? "md:grid-cols-2" : "sm:grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)]")}>
        <Side title={db ? "Question asked" : full ? "Request" : "What was sent"} tone="neutral" chip={sentFrom && <SourceChip value={sentFrom} onSelect={onSelectSpan} />}>
          {!db && (ex.method || ex.target) && (
            <div className="min-w-0 truncate font-mono text-xs">
              {ex.method && <span className="font-medium text-[var(--accent)]">{ex.method} </span>}
              <span title={ex.target ?? undefined}>{ex.target}</span>
            </div>
          )}
          {db ? (
            ex.query ? (
              full ? (
                <div className="overflow-hidden rounded-lg ring-1 ring-[var(--border)]">
                  <CodeBlock value={ex.query.text} attributeKey={ex.query.key} bare />
                </div>
              ) : (
                <pre className="max-h-32 overflow-hidden rounded-lg bg-[var(--subtle)] px-2.5 py-2 font-mono text-[11px] leading-relaxed break-all whitespace-pre-wrap ring-1 ring-[var(--border)]">{preview(ex.query.text)}</pre>
              )
            ) : (
              <p className="font-mono text-xs">{ex.target ?? "Query not recorded"}</p>
            )
          ) : (
            <>
              {full && <Headers headers={ex.requestHeaders} />}
              <Body label="Body" body={ex.requestBody} full={full} />
            </>
          )}
        </Side>

        {!full && (
          <div className="hidden flex-col items-center justify-center gap-0.5 text-[10px] text-[var(--text-dim)] sm:flex" aria-hidden="true">
            <ArrowRight size={14} />
            {formatDuration(ex.durationMs)}
          </div>
        )}

        <Side title={db ? "Answer" : full ? "Response" : "What came back"} tone={backTone} chip={backFrom && <SourceChip value={backFrom} onSelect={onSelectSpan} />}>
          {ex.status ? (
            <div className="font-mono text-xs">
              <span className={clsx("font-medium", failed ? "text-[var(--error)]" : soft ? "text-[var(--success)] line-through decoration-[var(--text-dim)]" : "text-[var(--success)]")}>
                {ex.status}
              </span>{" "}
              {ex.statusText}
              {soft ? (
                <span className="font-sans text-[var(--warn)]"> — the status says it worked; what happened next says it didn’t</span>
              ) : (
                info && full && <span className="font-sans text-[var(--text-muted)]"> — {info.meaning}</span>
              )}
            </div>
          ) : db ? (
            <p className="text-xs">
              {failed ? "The database returned an error." : ex.rows != null ? `${ex.rows} ${ex.rows === "1" ? "row" : "rows"} in ${formatDuration(ex.durationMs)}.` : `Answered in ${formatDuration(ex.durationMs)}.`}
            </p>
          ) : (
            <p className="text-xs text-[var(--text-muted)]">{failed ? "No answer — the call failed." : "No result code recorded."}</p>
          )}
          {!db && (
            <>
              {full && <Headers headers={ex.responseHeaders} />}
              <Body label="Body" body={ex.responseBody} full={full} />
            </>
          )}
          {message && (
            <div className={clsx("flex gap-2 rounded-md px-2.5 py-2 text-xs leading-relaxed", soft && !failed ? "bg-[var(--warn)]/10" : "bg-[var(--error)]/10")}>
              <MessageSquareWarning size={13} className={clsx("mt-0.5 shrink-0", soft && !failed ? "text-[var(--warn)]" : "text-[var(--error)]")} aria-hidden="true" />
              <span>
                {failed ? "The reply says" : "Why it failed"}: <strong>“{message}”</strong>
              </span>
            </div>
          )}
        </Side>
      </div>

      {!ex.server && ex.kind !== "db" && full && (
        <p className="flex gap-2 text-xs leading-relaxed text-[var(--text-muted)]">
          <Info size={13} className="mt-0.5 shrink-0" aria-hidden="true" />
          {ex.callee} isn’t sending traces, so this is what {ex.caller ?? "the caller"} saw.
        </p>
      )}
      {!ex.client && full && (
        <p className="flex gap-2 text-xs leading-relaxed text-[var(--text-muted)]">
          <Info size={13} className="mt-0.5 shrink-0" aria-hidden="true" />
          {ex.caller ? `${ex.caller} didn’t record its side of this call` : "The caller isn’t traced"}, so this is what {ex.callee} received.
        </p>
      )}
    </div>
  )
}
