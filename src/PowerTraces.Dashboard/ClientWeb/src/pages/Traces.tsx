import { useMemo } from "react"
import { useQuery } from "@tanstack/react-query"
import { Ban, CircleAlert, GitBranch } from "lucide-react"
import clsx from "clsx"
import { getIgnoreRules, getResources, getTraceFacets, searchTraces, type TraceMatch, type TraceSearch } from "@/lib/api"
import { Panel, EmptyState, Loading } from "@/components/Panel"
import { PageHeader } from "@/components/PageHeader"
import { ViewButton } from "@/components/ViewButton"
import { ColumnPicker } from "@/components/ColumnPicker"
import { DateRangeFilter } from "@/components/DateRangeFilter"
import { TimeRangePicker } from "@/components/TimeRangePicker"
import { Pagination } from "@/components/ui"
import { formatAttributeValue, formatDuration, formatTimestamp } from "@/lib/format"
import { useLocalStorageState } from "@/lib/useLocalStorageState"
import { pageParam, useUrlState } from "@/lib/urlState"
import { readTimeRange } from "@/lib/timeRange"
import { decodeFieldFilters, decodeLegacyAttributeFilters, encodeFieldFilter } from "@/lib/traceSearch"
import { CopyButton } from "@/components/CopyButton"
import { SearchBar, type SearchState } from "@/components/search/SearchBar"
import { TraceFacetPanel } from "@/components/search/TraceFacetPanel"
import { FiltersButton } from "@/components/search/FiltersButton"

const numberParam = (v: string | null) => (v !== null && v !== "" && !Number.isNaN(Number(v)) ? Number(v) : undefined)

export function Traces() {
  // Every filter lives in the URL so the view can be shared or bookmarked; column choice is a
  // per-viewer preference instead.
  const { params, update } = useUrlState()
  const time = readTimeRange(params)
  const { range, from, to } = time

  // `search` and `attr` are the parameters links used before the search redesign.
  const textKey = [...params.getAll("q"), ...params.getAll("search")].join("\u0000")
  const filterKey = params.getAll("f").join("\u0000")
  const legacyKey = params.getAll("attr").join("\u0000")
  const endpointKey = params.getAll("endpoint").join("\u0000")
  const serviceKey = params.getAll("service").join("\u0000")
  const errorsParam = params.get("errors")
  const errors = errorsParam === "1" ? true : errorsParam === "0" ? false : undefined
  const minMs = numberParam(params.get("minMs"))
  const maxMs = numberParam(params.get("maxMs"))
  const page = pageParam(params)
  const pageSize = 50

  const state = useMemo<SearchState>(() => {
    const split = (key: string) => (key ? key.split("\u0000") : [])
    return {
      text: split(textKey),
      endpoints: split(endpointKey),
      filters: [...decodeFieldFilters(split(filterKey)), ...decodeLegacyAttributeFilters(split(legacyKey))],
    }
  }, [textKey, filterKey, legacyKey, endpointKey])
  const services = useMemo(() => (serviceKey ? serviceKey.split("\u0000") : []), [serviceKey])

  const search: TraceSearch = {
    q: state.text.join(" ") || undefined,
    ...time.query,
    endpoints: state.endpoints,
    services,
    errors,
    minMs,
    maxMs,
    filters: state.filters,
    page,
    pageSize,
  }

  const [columns, setColumns] = useLocalStorageState<string[]>("powertraces.traces.columns", [])
  const { data: serviceNames } = useQuery({ queryKey: ["resources"], queryFn: getResources })
  const { data: ignoreRules } = useQuery({ queryKey: ["ignore-rules"], queryFn: getIgnoreRules })

  const { data, isLoading, isFetching, error } = useQuery({
    queryKey: ["trace-search", search],
    queryFn: ({ signal }) => searchTraces(search, { signal }),
    placeholderData: (previous) => previous,
  })

  const facetSearch = { ...search, page: undefined, pageSize: undefined }
  const { data: facets } = useQuery({
    queryKey: ["trace-facets", facetSearch],
    queryFn: ({ signal }) => getTraceFacets(facetSearch, { signal }),
    placeholderData: (previous) => previous,
  })

  const setSearchState = (next: SearchState) =>
    update({
      q: next.text,
      search: null,
      attr: null,
      f: next.filters.map(encodeFieldFilter),
      endpoint: next.endpoints,
    })

  const traceRules = new Set(ignoreRules?.filter((r) => r.kind === "trace").map((r) => r.name) ?? [])
  const showMatch = data?.items.some((t) => t.match) ?? false
  const total = data ? `${data.totalCount.toLocaleString()}${data.totalCountCapped ? "+" : ""}` : "…"
  const panelFilterCount = state.endpoints.length + services.length + (errors === undefined ? 0 : 1) + (minMs !== undefined || maxMs !== undefined ? 1 : 0)

  // One panel, shown beside the table when the content area is wide enough (whatever the sidebar
  // is doing) and behind the Filters button otherwise.
  const facetPanel = (
    <TraceFacetPanel
      facets={facets}
      endpoints={state.endpoints}
      onEndpoints={(endpoint) => update({ endpoint })}
      errors={errors}
      onErrors={(next) => update({ errors: next === undefined ? null : next ? "1" : "0" })}
      services={serviceNames ?? []}
      selectedServices={services}
      onServices={(service) => update({ service })}
      minMs={minMs}
      maxMs={maxMs}
      onDuration={(min, max) => update({ minMs: min?.toString() ?? null, maxMs: max?.toString() ?? null })}
    />
  )

  return (
    <div className="@container flex flex-col gap-4">
      <PageHeader
        title="Traces"
        subtitle="Search any value, word or field — including inside request and response bodies. A trace matches when any of its spans does."
        actions={<CopyButton value={window.location.href} label="Copy link" showLabel className="h-[34px] border border-[var(--border)] bg-[var(--surface)] px-3 text-[var(--text-muted)]" />}
      />

      <div className="flex flex-col gap-2.5 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3 shadow-[var(--shadow-sm)]">
        <div className="flex flex-wrap items-start gap-2">
          <SearchBar scope="traces" state={state} onChange={setSearchState} endpointOptions={facets?.endpoints ?? []} />
          <TimeRangePicker range={range} from={from} update={update} />
        </div>
        {range === "custom" && <DateRangeFilter from={from} to={to} onChange={(f, t) => update({ from: f, to: t, range: f || t ? "custom" : null })} />}
        <div className="flex flex-wrap items-center justify-between gap-2 px-0.5 text-xs text-[var(--text-muted)]">
          <span>
            <strong className="font-semibold text-[var(--text)]">{total} traces</strong> {time.description}
            {isFetching && !isLoading && <span className="ml-2 text-[var(--text-dim)]">updating…</span>}
          </span>
          <div className="ml-auto flex items-center gap-2">
            <FiltersButton className="@5xl:hidden" active={panelFilterCount}>
              {facetPanel}
            </FiltersButton>
            <ColumnPicker scope="traces" selected={columns} onChange={setColumns} />
          </div>
        </div>
      </div>

      <div className="flex items-start gap-4">
        <div className="hidden w-[260px] shrink-0 @5xl:block">{facetPanel}</div>

        <Panel className={clsx("min-w-0 flex-1 transition-opacity", isFetching && !isLoading && "opacity-70")}>
          {isLoading ? (
            <Loading />
          ) : error ? (
            <EmptyState label="The search failed." hint="Try again, or narrow the time range." icon={CircleAlert} />
          ) : !data?.items.length ? (
            <EmptyState
              label="No traces match this search."
              hint={range === "all" ? "Try removing a filter." : "Try a wider time range or removing a filter."}
              icon={GitBranch}
            />
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-[var(--border)] bg-[var(--subtle)] text-left text-[11px] tracking-wide text-[var(--text-dim)] uppercase">
                    <th className="px-4 py-2.5 font-medium">Started</th>
                    <th className="px-4 py-2.5 font-medium">Operation</th>
                    {showMatch && <th className="px-4 py-2.5 font-medium">Why it matched</th>}
                    <th className="px-4 py-2.5 text-right font-medium">Spans</th>
                    <th className="px-4 py-2.5 text-right font-medium">Duration</th>
                    {columns.map((key) => (
                      <th key={key} className="px-4 py-2.5 font-medium normal-case">
                        {key}
                      </th>
                    ))}
                    <th className="w-11 px-2 py-2.5" />
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((trace) => (
                    <tr key={trace.traceId} className="border-b border-[var(--border)] transition-colors last:border-0 hover:bg-[var(--hover)]">
                      <td className="px-4 py-2.5 font-mono text-xs whitespace-nowrap text-[var(--text-muted)]">{formatTimestamp(trace.startTime)}</td>
                      <td className="max-w-[380px] px-4 py-2.5">
                        <span className="flex items-center gap-2">
                          {trace.hasError ? (
                            <CircleAlert size={14} className="shrink-0 text-[var(--error)]" aria-label="Failed" />
                          ) : (
                            <span className="h-2 w-2 shrink-0 rounded-full bg-[var(--success)]" aria-label="Succeeded" />
                          )}
                          <span className="truncate font-mono text-[13px]" title={trace.rootName ?? undefined}>
                            {trace.rootName ?? "(unnamed)"}
                          </span>
                          {trace.rootName && traceRules.has(trace.rootName) && (
                            <span
                              title="New traces with this root span are no longer exported"
                              className="inline-flex shrink-0 items-center gap-1 rounded-md bg-[var(--warn)]/12 px-1.5 py-0.5 text-[10px] font-medium text-[var(--warn)]"
                            >
                              <Ban size={10} /> Ignored
                            </span>
                          )}
                          {trace.serviceName && <span className="shrink-0 truncate text-xs text-[var(--text-muted)]">{trace.serviceName}</span>}
                        </span>
                      </td>
                      {showMatch && (
                        <td className="max-w-[420px] px-4 py-2.5">
                          <MatchCell match={trace.match} />
                        </td>
                      )}
                      <td className="px-4 py-2.5 text-right text-xs text-[var(--text-muted)] tabular-nums">{trace.spanCount}</td>
                      <td className="px-4 py-2.5 text-right font-mono text-xs tabular-nums">{formatDuration(trace.durationMs)}</td>
                      {columns.map((key) => (
                        <td key={key} className="max-w-[200px] truncate px-4 py-2.5 font-mono text-xs text-[var(--text-muted)]" title={formatAttributeValue(trace.attributes[key])}>
                          {formatAttributeValue(trace.attributes[key])}
                        </td>
                      ))}
                      <td className="px-2 py-2.5">
                        <ViewButton to={`/traces/${trace.traceId}`} label="View trace" />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {data && data.totalCount > 0 && (
            <Pagination
              page={page}
              pageSize={pageSize}
              itemCount={data.items.length}
              totalCount={data.totalCount}
              totalCountCapped={data.totalCountCapped}
              noun="traces"
              onPage={(p) => update({ page: p > 1 ? String(p) : null })}
            />
          )}
        </Panel>
      </div>
    </div>
  )
}

/** `key = …value…` with the matched part marked. */
function MatchCell({ match }: { match?: TraceMatch | null }) {
  if (!match) return <span className="text-xs text-[var(--text-dim)]">—</span>
  const { value, highlightStart: start, highlightLength: length } = match
  return (
    <span className="flex min-w-0 items-baseline gap-1.5 font-mono text-xs" title={`${match.key} = ${value}`}>
      <span className="shrink-0 text-[var(--text-muted)]">{match.key}</span>
      <span className="shrink-0 text-[var(--text-dim)]">=</span>
      <span className="min-w-0 truncate text-[var(--text-muted)]">
        {value.slice(0, start)}
        <mark className="rounded-sm bg-[var(--warn)]/20 px-0.5 text-[var(--text)]">{value.slice(start, start + length)}</mark>
        {value.slice(start + length)}
      </span>
    </span>
  )
}
