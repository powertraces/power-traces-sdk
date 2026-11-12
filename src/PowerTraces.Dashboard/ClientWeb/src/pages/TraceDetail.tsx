import { useMemo, useRef, useState } from "react"
import { useParams, Link } from "react-router-dom"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Ban, ChevronRight, Pin, PinOff } from "lucide-react"
import { createIgnoreRule, getIgnoreRules, getRetention, getTrace, pinTrace, unpinTrace, type TraceDetail as TraceDetailDto } from "@/lib/api"
import { EmptyState, Loading } from "@/components/Panel"
import { Button, Segmented } from "@/components/ui"
import { CopyButton } from "@/components/CopyButton"
import { useUrlState } from "@/lib/urlState"
import { TraceHeader } from "@/components/trace/TraceHeader"
import { TraceWaterfall } from "@/components/trace/TraceWaterfall"
import { TraceHotspots } from "@/components/trace/TraceHotspots"
import { SpanFocus } from "@/components/trace/SpanFocus"
import { analyzeTrace, pathToSpan } from "@/components/trace/traceAnalysis"

type View = "timeline" | "hotspots"

export function TraceDetail() {
  const { traceId } = useParams<{ traceId: string }>()
  const { data, isLoading } = useQuery({
    queryKey: ["trace", traceId],
    queryFn: ({ signal }) => getTrace(traceId!, { signal }),
    enabled: !!traceId,
  })

  if (isLoading) return <Loading label="Loading trace…" />
  if (!data) return <EmptyState label="Trace not found." hint="It may have been removed by retention, or its spans haven't been exported yet." />
  return <TraceView key={data.traceId} trace={data} />
}

function TraceView({ trace }: { trace: TraceDetailDto }) {
  const queryClient = useQueryClient()
  // The left pane's view (?view=hotspots; the timeline is the default) and the selected span
  // (?span=) live in the URL so a link reopens exactly this view.
  const { params, update } = useUrlState()
  const view: View = params.get("view") === "hotspots" ? "hotspots" : "timeline"
  const analysis = useMemo(() => analyzeTrace(trace), [trace])
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set())
  const detailsRef = useRef<HTMLDivElement>(null)

  const requested = params.get("span")
  const selected =
    (requested && analysis.spanById.get(requested)) ||
    // Default to the first failing span — usually the reason anyone opened the trace — else the root.
    trace.spans
      .filter((s) => s.hasError && !(analysis.childrenByParent.get(s.spanId) ?? []).some((c) => c.hasError))
      .sort((a, b) => a.startTime.localeCompare(b.startTime))[0] ||
    analysis.roots[0] ||
    trace.spans[0]

  const { data: ignoreRules } = useQuery({ queryKey: ["ignore-rules"], queryFn: getIgnoreRules })
  const invalidateRules = () => queryClient.invalidateQueries({ queryKey: ["ignore-rules"] })
  const ignoreTrace = useMutation({ mutationFn: () => createIgnoreRule("trace", trace.rootName ?? ""), onSuccess: invalidateRules })
  const ignoreSpan = useMutation({ mutationFn: (name: string) => createIgnoreRule("span", name), onSuccess: invalidateRules })
  const traceIgnored = ignoreRules?.some((r) => r.kind === "trace" && r.name === trace.rootName) ?? false
  const ignoredSpanNames = useMemo(() => new Set(ignoreRules?.filter((r) => r.kind === "span").map((r) => r.name) ?? []), [ignoreRules])

  // A provider without retention answers 501 — then there's nothing to keep the trace from.
  const { data: retention, isError: retentionUnsupported } = useQuery({ queryKey: ["retention"], queryFn: getRetention, retry: false })
  const togglePin = useMutation({
    mutationFn: () => (trace.isPinned ? unpinTrace(trace.traceId) : pinTrace(trace.traceId)),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["trace", trace.traceId] })
      queryClient.invalidateQueries({ queryKey: ["pinned-traces"] })
    },
  })

  const selectSpan = (spanId: string) => {
    const span = analysis.spanById.get(spanId)
    // Unfold whatever hides the span in the tree.
    if (span && collapsed.size > 0) {
      const ancestors = pathToSpan(span, analysis.spanById).slice(0, -1)
      if (ancestors.some((a) => collapsed.has(a.spanId))) {
        const next = new Set(collapsed)
        ancestors.forEach((a) => next.delete(a.spanId))
        setCollapsed(next)
      }
    }
    update({ span: spanId }, { replace: true })
    // Stacked layout: the details sit below the tree, so bring them into view.
    if (!window.matchMedia("(min-width: 1280px)").matches) detailsRef.current?.scrollIntoView({ behavior: "smooth", block: "start" })
  }
  const setView = (next: View) => update({ view: next === "hotspots" ? "hotspots" : null }, { replace: true })

  const actions = (
    <>
      <CopyButton value={window.location.href} label="Copy link" showLabel className="h-[34px] border border-[var(--border)] bg-[var(--surface)] px-3 text-[var(--text-muted)]" />
      <Button
        onClick={() => ignoreTrace.mutate()}
        disabled={!trace.rootName || traceIgnored || ignoreTrace.isPending || ignoreTrace.isSuccess}
        title="Stop exporting future traces with this root span. Existing telemetry is kept."
      >
        <Ban size={13} /> {traceIgnored || ignoreTrace.isSuccess ? "Root span ignored" : "Ignore root span"}
      </Button>
      {!retentionUnsupported && (
        <Button
          variant={trace.isPinned ? "default" : "primary"}
          onClick={() => togglePin.mutate()}
          disabled={togglePin.isPending}
          aria-pressed={trace.isPinned}
          title={
            trace.isPinned
              ? "Let retention delete this trace again once it expires"
              : "Exclude this trace, and its logs, from retention — it's never deleted automatically"
          }
        >
          {trace.isPinned ? <PinOff size={13} /> : <Pin size={13} />}
          {trace.isPinned ? "Stop keeping" : "Keep forever"}
        </Button>
      )}
    </>
  )

  const viewSwitch = (
    <Segmented
      label="Span view"
      value={view}
      onChange={setView}
      options={[
        { value: "timeline", label: `Timeline · ${trace.spans.length}` },
        { value: "hotspots", label: "Hotspots" },
      ]}
    />
  )

  return (
    <div className="flex flex-col gap-4">
      <nav aria-label="Breadcrumb" className="flex min-w-0 items-center gap-1.5 text-xs text-[var(--text-muted)]">
        <Link to="/traces" className="hover:text-[var(--text)]">
          Traces
        </Link>
        <ChevronRight size={12} aria-hidden="true" />
        <span className="truncate font-mono text-[var(--text)]">{trace.traceId.slice(0, 16)}</span>
      </nav>

      <TraceHeader trace={trace} analysis={analysis} retention={retention?.settings ?? null} actions={actions} />

      {/* Master–detail: the span tree (or the hotspots) on the left, the selected span on the
          right, pinned in view while the tree scrolls. Stacked below xl. */}
      <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,1.35fr)_minmax(400px,1fr)]">
        <section aria-label="Spans" className="min-w-0 overflow-hidden rounded-xl border border-[var(--border)] bg-[var(--surface)] shadow-[var(--shadow-sm)]">
          {view === "timeline" ? (
            <TraceWaterfall
              trace={trace}
              analysis={analysis}
              ignoredSpanNames={ignoredSpanNames}
              selectedSpanId={selected?.spanId ?? null}
              onSelectSpan={selectSpan}
              collapsed={collapsed}
              onCollapsedChange={setCollapsed}
              toolbarStart={viewSwitch}
            />
          ) : (
            <>
              <div className="flex items-center border-b border-[var(--border)] px-3 py-2.5">{viewSwitch}</div>
              <TraceHotspots analysis={analysis} spans={trace.spans} selectedSpanId={selected?.spanId ?? null} onSelect={selectSpan} />
            </>
          )}
        </section>

        <div ref={detailsRef} className="min-w-0 scroll-mt-4 xl:sticky xl:top-0 xl:max-h-[calc(100vh-3rem)] xl:overflow-y-auto xl:rounded-xl">
          {selected ? (
            <SpanFocus
              key={selected.spanId}
              span={selected}
              analysis={analysis}
              traceDurationMs={trace.durationMs}
              ignored={ignoredSpanNames.has(selected.name) || (ignoreSpan.isSuccess && ignoreSpan.variables === selected.name)}
              onIgnore={(name) => ignoreSpan.mutate(name)}
              onSelect={selectSpan}
            />
          ) : (
            <EmptyState label="This trace has no spans." />
          )}
        </div>
      </div>
    </div>
  )
}
