import { useState, type CSSProperties, type ReactNode } from "react"
import { Link } from "react-router-dom"
import { ArrowRight, Ban, ChevronRight, CircleAlert, Layers, Search, TriangleAlert } from "lucide-react"
import clsx from "clsx"
import type { SpanItem } from "@/lib/api"
import { formatDuration } from "@/lib/format"
import { CodeBlock } from "@/components/CodeBlock"
import { CopyButton } from "@/components/CopyButton"
import { tracesHref } from "@/lib/traceSearch"
import { getHttpStatusFromSpan, kindLabel } from "./traceHelpers"
import { AttributeList } from "./AttributeList"
import { ExceptionCard } from "./ExceptionCard"
import { ExchangeView } from "./ExchangeView"
import { describeSpan, partnerOf, type Exchange } from "./story"
import type { SoftFailure } from "./softFailures"
import { pathToSpan, serviceOf, spanExceptions, type TraceAnalysis } from "./traceAnalysis"

export type SpanTab = "overview" | "exchange" | "technical"

/** Attribute values arrive as JSON values — strings as-is, everything else (numbers, booleans,
 * arrays, objects) as its JSON text, which is also what gets copied. */
function valueText(v: unknown): string {
  return typeof v === "string" ? v : JSON.stringify(v)
}

function SectionTitle({ children }: { children: ReactNode }) {
  return <h3 className="text-xs font-semibold text-[var(--text-muted)]">{children}</h3>
}

/** Root › … › parent: how the span is reached, each step selectable. Long paths keep the root
 * and the two nearest ancestors. */
function SpanAncestors({ span, analysis, onSelect }: { span: SpanItem; analysis: TraceAnalysis; onSelect: (id: string) => void }) {
  const ancestors = pathToSpan(span, analysis.spanById).slice(0, -1)
  if (ancestors.length === 0) return null
  const shown = ancestors.length > 3 ? [ancestors[0], null, ...ancestors.slice(-2)] : ancestors

  return (
    <nav aria-label="Path to this span" className="flex min-w-0 flex-wrap items-center gap-1 text-[11px] text-[var(--text-dim)]">
      {shown.map((a, i) => (
        <span key={a?.spanId ?? "gap"} className="flex min-w-0 items-center gap-1">
          {i > 0 && <ChevronRight size={11} aria-hidden="true" className="shrink-0" />}
          {a ? (
            <button
              type="button"
              onClick={() => onSelect(a.spanId)}
              title={`${a.name} · ${serviceOf(a)}`}
              className={clsx("max-w-48 truncate rounded px-1 py-0.5 font-mono hover:bg-[var(--hover-strong)] hover:text-[var(--text)]", a.hasError && "text-[var(--error)]")}
            >
              {a.name}
            </button>
          ) : (
            <span title={`${ancestors.length - 3} more`}>…</span>
          )}
        </span>
      ))}
      <ChevronRight size={11} aria-hidden="true" className="shrink-0" />
    </nav>
  )
}

const visibleAttrs = (span: SpanItem) => Object.entries(span.attributes).filter(([, v]) => v != null && v !== "")

/** The few facts anyone needs first, under everyday names. */
function KeyFacts({ span, exchange, soft, traceDurationMs, selfMs }: { span: SpanItem; exchange: Exchange | undefined; soft: SoftFailure | null; traceDurationMs: number; selfMs: number }) {
  const share = Math.round((span.durationMs / (traceDurationMs || 1)) * 100)
  const hostOf = (s: SpanItem | null | undefined) => {
    const v = s && (s.attributes["host.name"] ?? s.attributes["k8s.pod.name"] ?? s.attributes["service.instance.id"])
    return v != null && v !== "" ? valueText(v) : null
  }
  const ownHost = hostOf(span)
  const answerHost = exchange?.server && exchange.server.spanId !== span.spanId ? hostOf(exchange.server) : null
  const facts: { label: string; value: ReactNode; tone?: "error" | "warn" }[] = exchange
    ? [
        { label: "Who asked", value: exchange.caller ?? "Not traced" },
        { label: exchange.kind === "db" ? "Database" : "Who answered", value: exchange.callee },
        {
          label: "Result",
          value: `${exchange.status ? `${exchange.status}${exchange.statusText ? ` · ${exchange.statusText}` : ""}` : exchange.failed ? "Failed" : "Worked"}${soft ? " · but failed" : ""}`,
          tone: exchange.failed ? "error" : soft ? "warn" : undefined,
        },
      ]
    : [
        { label: "Service", value: serviceOf(span) },
        { label: "Result", value: span.hasError ? "Failed" : "Worked", tone: span.hasError ? "error" : undefined },
      ]
  facts.push({ label: "How long", value: `${formatDuration(span.durationMs)} · ${share}% of the trace` })
  if (selfMs !== span.durationMs) facts.push({ label: "Its own work", value: `${formatDuration(selfMs)} (the rest is in steps it called)` })
  if (ownHost) facts.push({ label: "Where it ran", value: ownHost })
  if (answerHost && answerHost !== ownHost) facts.push({ label: "Answered on", value: answerHost })

  return (
    <dl className="grid gap-2 sm:grid-cols-2">
      {facts.map((f) => (
        <div key={f.label} className="min-w-0 rounded-lg px-3 py-2 ring-1 ring-[var(--border)]">
          <dt className="text-[11px] text-[var(--text-dim)]">{f.label}</dt>
          <dd className={clsx("mt-0.5 truncate text-[13px] font-semibold", f.tone === "error" && "text-[var(--error)]", f.tone === "warn" && "text-[var(--warn)]")}>{f.value}</dd>
        </div>
      ))}
    </dl>
  )
}

/** Why a call that answered with a success status is counted as a failure: every finding with
 * its strength, and the checks that found nothing. */
export function Evidence({ soft, children }: { soft: SoftFailure; children?: ReactNode }) {
  return (
    <section aria-label="Why we think it failed" className="flex flex-col gap-2.5 rounded-lg bg-[var(--warn)]/5 p-3.5 ring-1 ring-[var(--warn)]/40">
      <h3 className="flex items-center gap-1.5 text-[13px] font-semibold">
        <TriangleAlert size={14} className="text-[var(--warn)]" aria-hidden="true" />
        Said {soft.exchange.status ?? "OK"}, but we think it failed
      </h3>
      <ul className="flex flex-col gap-1.5 text-[12.5px] leading-relaxed">
        {soft.signals.map((sig) => (
          <li key={sig.id} className="flex items-start gap-2">
            <span
              className={clsx(
                "mt-0.5 w-14 shrink-0 rounded px-1 py-px text-center text-[9px] font-bold tracking-wider",
                sig.strength === "strong" ? "bg-[var(--warn)]/18 text-[var(--warn)]" : "bg-[var(--hover-strong)] text-[var(--text-muted)]",
              )}
            >
              {sig.strength === "strong" ? "STRONG" : "WEAK"}
            </span>
            <span>{sig.text}</span>
          </li>
        ))}
        {soft.notSeen.map((text) => (
          <li key={text} className="flex items-start gap-2 text-[var(--text-dim)]">
            <span className="mt-0.5 w-14 shrink-0 rounded px-1 py-px text-center text-[9px] font-bold tracking-wider ring-1 ring-[var(--border)]">NOT SEEN</span>
            <span>{text}</span>
          </li>
        ))}
      </ul>
      {children}
    </section>
  )
}

/** The selected span: what it did in a sentence, then three tabs — the overview anyone can read,
 * the request and response side by side (from both halves of the call), and every recorded
 * attribute for developers. */
export function SpanFocus({
  span,
  analysis,
  exchange,
  soft,
  softFeedback,
  traceDurationMs,
  ignored,
  onIgnore,
  onSelect,
  tab: requestedTab,
  onTabChange,
}: {
  span: SpanItem
  analysis: TraceAnalysis
  exchange: Exchange | undefined
  /** Set when this span's call answered with a success status but didn't work. */
  soft: SoftFailure | null
  /** Rendered under the evidence — the "was it a failure?" buttons. */
  softFeedback?: ReactNode
  traceDurationMs: number
  ignored: boolean
  onIgnore: (name: string) => void
  onSelect: (spanId: string) => void
  tab: SpanTab
  onTabChange: (tab: SpanTab) => void
}) {
  const httpStatus = getHttpStatusFromSpan(span)
  const exceptions = spanExceptions(span)
  const offsetMs = Math.max(0, new Date(span.startTime).getTime() - analysis.traceStart)
  const total = traceDurationMs || 1
  const leftPct = Math.min((offsetMs / total) * 100, 99.5)
  const widthPct = Math.max(Math.min((span.durationMs / total) * 100, 100 - leftPct), 0.5)
  const selfMs = analysis.selfTimeMs.get(span.spanId) ?? span.durationMs
  const swatch = `var(${analysis.serviceColor(span)})`
  const partner = partnerOf(span, exchange)
  const attrCount = visibleAttrs(span).length + (partner ? visibleAttrs(partner).length : 0)
  const tab: SpanTab = requestedTab === "exchange" && !exchange ? "overview" : requestedTab

  const tabs: { value: SpanTab; label: string }[] = [
    { value: "overview", label: "Overview" },
    ...(exchange ? [{ value: "exchange" as const, label: exchange.kind === "db" ? "Query & answer" : "Request & response" }] : []),
    { value: "technical", label: `Technical · ${attrCount}` },
  ]

  return (
    <section aria-label="Selected span" className="min-w-0 overflow-hidden rounded-xl border border-[var(--border)] bg-[var(--surface)] shadow-[var(--shadow-sm)]">
      <div className="flex flex-col gap-4 border-b border-[var(--border)] p-5 pb-0">
        <div className="flex flex-wrap items-center gap-2 text-xs text-[var(--text-muted)]">
          <span className="h-2 w-2 shrink-0 rounded-sm" style={{ background: swatch }} />
          {exchange?.caller && exchange.callee !== exchange.caller ? (
            <span className="flex items-center gap-1">
              {exchange.caller}
              <ArrowRight size={11} aria-hidden="true" />
              <span className={clsx(exchange.failed && "font-semibold text-[var(--error)]")}>{exchange.callee}</span>
            </span>
          ) : (
            serviceOf(span)
          )}
          <span className="rounded bg-[var(--hover-strong)] px-1.5 py-px text-[10px] font-semibold tracking-wide uppercase">{kindLabel(span.kind)}</span>
          <span className="ml-auto flex items-center gap-1 font-mono text-[11px] text-[var(--text-dim)]">
            {span.spanId}
            <CopyButton value={span.spanId} label="Copy span id" />
          </span>
        </div>

        <div className="flex flex-col gap-1.5">
          <SpanAncestors span={span} analysis={analysis} onSelect={onSelect} />
          <div className="flex flex-wrap items-start justify-between gap-3">
            <h2 className="min-w-0 font-mono text-base leading-snug font-semibold break-all">{span.name}</h2>
            <span
              className={clsx(
                "shrink-0 rounded-full px-2.5 py-0.5 text-xs font-medium",
                span.hasError
                  ? "bg-[var(--error)]/12 text-[var(--error)]"
                  : soft
                    ? "bg-[var(--warn)]/12 text-[var(--warn)]"
                    : span.status === "OK"
                      ? "bg-[var(--success)]/12 text-[var(--success)]"
                      : "bg-[var(--hover-strong)] text-[var(--text-muted)]",
              )}
            >
              {span.hasError
                ? httpStatus
                  ? `Error · ${httpStatus}`
                  : "Error"
                : soft
                  ? `${httpStatus ?? "OK"} · but failed`
                  : span.status === "OK"
                    ? httpStatus
                      ? `OK · ${httpStatus}`
                      : "OK"
                    : (httpStatus ?? "Unset")}
            </span>
          </div>
          <p className="text-sm leading-relaxed text-[var(--text)]">{describeSpan(span, exchange, soft)}</p>
        </div>

        <div className="flex flex-col gap-1.5">
          <div className="relative h-2.5 overflow-hidden rounded-full bg-[var(--hover-strong)]" aria-hidden="true">
            <div
              className="absolute inset-y-0 left-(--focus-left) w-(--focus-width) rounded-full"
              style={{ "--focus-left": `${leftPct}%`, "--focus-width": `${widthPct}%`, background: span.hasError ? "var(--error)" : swatch } as CSSProperties}
            />
          </div>
          <dl className="flex flex-wrap justify-between gap-x-4 gap-y-1 text-xs text-[var(--text-muted)]">
            <div className="flex gap-1">
              <dt>Starts</dt>
              <dd className="font-semibold text-[var(--text)] tabular-nums">+{formatDuration(offsetMs)}</dd>
            </div>
            <div className="flex gap-1">
              <dt>Duration</dt>
              <dd className="tabular-nums">
                <span className="font-semibold text-[var(--text)]">{formatDuration(span.durationMs)}</span> · {Math.round((span.durationMs / total) * 100)}% of trace
              </dd>
            </div>
            <div className="flex gap-1">
              <dt>Self time</dt>
              <dd className="font-semibold text-[var(--text)] tabular-nums">{formatDuration(selfMs)}</dd>
            </div>
          </dl>
        </div>

        <div className="flex flex-wrap gap-x-4 gap-y-1">
          <button
            type="button"
            onClick={() => onIgnore(span.name)}
            disabled={ignored}
            className="inline-flex w-fit items-center gap-1 text-xs text-[var(--text-muted)] hover:text-[var(--warn)] disabled:opacity-60"
            title="Stop exporting future spans with this name. Existing telemetry is kept."
          >
            <Ban size={12} /> {ignored ? "Spans like this are ignored" : "Ignore future spans like this"}
          </button>
          <Link
            to={tracesHref({ filters: [{ key: "span.name", value: span.name, match: "Equals" }] })}
            className="inline-flex w-fit items-center gap-1 text-xs text-[var(--text-muted)] hover:text-[var(--accent)]"
          >
            <Search size={12} /> Find traces with this span
          </Link>
        </div>

        <div role="tablist" aria-label="Span details" className="-mx-5 flex gap-1 overflow-x-auto px-4">
          {tabs.map((t) => (
            <button
              key={t.value}
              type="button"
              role="tab"
              aria-selected={tab === t.value}
              onClick={() => onTabChange(t.value)}
              className={clsx(
                "h-9 shrink-0 border-b-2 px-2.5 text-[13px] transition-colors",
                tab === t.value ? "border-[var(--accent)] font-semibold text-[var(--text)]" : "border-transparent text-[var(--text-muted)] hover:text-[var(--text)]",
              )}
            >
              {t.label}
            </button>
          ))}
        </div>
      </div>

      <div role="tabpanel" className="flex flex-col gap-5 p-5">
        {tab === "overview" && (
          <>
            {exceptions.map((ex, i) => (
              <ExceptionCard key={i} exception={ex} at={`+${formatDuration(offsetMs + ex.offsetMs)}`} />
            ))}
            {exceptions.length === 0 && span.statusMessage && (
              <div className="flex gap-2.5 rounded-lg bg-[var(--error)]/8 px-3.5 py-3">
                <CircleAlert size={16} className="mt-px shrink-0 text-[var(--error)]" aria-hidden="true" />
                <div className="min-w-0 flex-1 font-mono text-xs leading-relaxed break-words text-[var(--text)]">{span.statusMessage}</div>
                <CopyButton value={span.statusMessage} label="Copy status message" className="-my-1 self-start" />
              </div>
            )}
            {soft && <Evidence soft={soft}>{softFeedback}</Evidence>}
            {exchange && <ExchangeView exchange={exchange} soft={soft} full={false} onSelectSpan={onSelect} />}
            <KeyFacts span={span} exchange={exchange} soft={soft} traceDurationMs={traceDurationMs} selfMs={selfMs} />
            <button
              type="button"
              onClick={() => onTabChange("technical")}
              className="flex h-11 w-full items-center justify-between rounded-lg border border-dashed border-[var(--border-strong)] px-3.5 text-[13px] text-[var(--text-muted)] hover:bg-[var(--hover)] hover:text-[var(--text)]"
            >
              <span>
                Technical details for developers · {attrCount} {attrCount === 1 ? "attribute" : "attributes"}
                {partner ? " from 2 spans" : ""}
              </span>
              <ChevronRight size={14} aria-hidden="true" />
            </button>
          </>
        )}

        {tab === "exchange" && exchange && <ExchangeView exchange={exchange} soft={soft} full onSelectSpan={onSelect} />}

        {tab === "technical" && <Technical span={span} partner={partner} offsetMs={offsetMs} onSelect={onSelect} />}
      </div>
    </section>
  )
}

/** Every recorded attribute (filterable, each copyable) — this span's and, for a call traced on
 * both sides, the other side's — then events and links. */
function Technical({ span, partner, offsetMs, onSelect }: { span: SpanItem; partner: SpanItem | null; offsetMs: number; onSelect: (spanId: string) => void }) {
  const [filter, setFilter] = useState("")
  const needle = filter.trim().toLowerCase()
  const match = (entries: [string, unknown][]) =>
    needle ? entries.filter(([k, v]) => k.toLowerCase().includes(needle) || valueText(v).toLowerCase().includes(needle)) : entries
  const own = visibleAttrs(span)
  const other = partner ? visibleAttrs(partner) : []
  const shownOwn = match(own)
  const shownOther = match(other)
  const otherEvents = span.events.filter((e) => e.name !== "exception")
  const total = own.length + other.length

  return (
    <>
      {total > 0 && (
        <div className="flex flex-col gap-3">
          <div className="flex flex-wrap items-center gap-2">
            <label className="flex h-8 min-w-48 flex-1 items-center gap-2 rounded-lg border border-[var(--border)] bg-[var(--input-bg)] px-2.5 text-[var(--text-dim)] focus-within:border-[var(--accent)]">
              <Search size={13} aria-hidden="true" />
              <input
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
                placeholder={`Filter ${total} attribute${total === 1 ? "" : "s"}…`}
                aria-label="Filter attributes"
                className="min-w-0 flex-1 bg-transparent text-[13px] text-[var(--text)] outline-none placeholder:text-[var(--text-dim)]"
              />
            </label>
            <CopyButton
              value={JSON.stringify(span.attributes, null, 2)}
              label="Copy as JSON"
              showLabel
              className="h-8 border border-[var(--border)] px-2.5 text-[var(--text-muted)]"
            />
          </div>
          {partner && <SectionTitle>{`This span · ${serviceOf(span)} · ${kindLabel(span.kind).toLowerCase()}`}</SectionTitle>}
          {shownOwn.length === 0 ? (
            <p className="py-2 text-center text-xs text-[var(--text-dim)]">{own.length === 0 ? "No attributes on this span." : `No attribute matches “${filter}”.`}</p>
          ) : (
            <AttributeList attributes={shownOwn} />
          )}
          {partner && (
            <>
              <div className="mt-2 flex items-center gap-2">
                <SectionTitle>{`Other side of the call · ${serviceOf(partner)} · ${kindLabel(partner.kind).toLowerCase()}`}</SectionTitle>
                <button type="button" onClick={() => onSelect(partner.spanId)} className="ml-auto text-xs text-[var(--accent)] hover:underline">
                  Open it
                </button>
              </div>
              {shownOther.length === 0 ? (
                <p className="py-2 text-center text-xs text-[var(--text-dim)]">{other.length === 0 ? "No attributes on that span." : `No attribute matches “${filter}”.`}</p>
              ) : (
                <AttributeList attributes={shownOther} />
              )}
            </>
          )}
        </div>
      )}

      {otherEvents.length > 0 && (
        <div className="flex flex-col gap-2">
          <SectionTitle>{`Events · ${otherEvents.length}`}</SectionTitle>
          <div className="divide-y divide-[var(--border)] overflow-hidden rounded-lg ring-1 ring-[var(--border)]">
            {otherEvents.map((e, i) => (
              <div key={i} className="px-3 py-2.5">
                <div className="flex items-center gap-3">
                  <span className="shrink-0 font-mono text-[11px] text-[var(--text-dim)] tabular-nums">
                    +{formatDuration(offsetMs + new Date(e.time).getTime() - new Date(span.startTime).getTime())}
                  </span>
                  <span className="text-xs font-medium">{e.name}</span>
                </div>
                {e.attributes && Object.keys(e.attributes).length > 0 && (
                  <div className="mt-1.5">
                    <CodeBlock value={JSON.stringify(e.attributes)} />
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {span.links.length > 0 && (
        <div className="flex flex-col gap-2">
          <SectionTitle>{`Links · ${span.links.length}`}</SectionTitle>
          <div className="divide-y divide-[var(--border)] overflow-hidden rounded-lg ring-1 ring-[var(--border)]">
            {span.links.map((l, i) => (
              <div key={i} className="px-3 py-2 font-mono text-[11px] break-all text-[var(--text-muted)]">
                {l.traceId} / {l.spanId}
              </div>
            ))}
          </div>
        </div>
      )}

      {total === 0 && otherEvents.length === 0 && span.links.length === 0 && (
        <div className="flex flex-col items-center justify-center gap-2 py-10 text-[var(--text-dim)]">
          <Layers size={24} />
          <p className="text-xs">No attributes recorded for this span</p>
        </div>
      )}
    </>
  )
}
