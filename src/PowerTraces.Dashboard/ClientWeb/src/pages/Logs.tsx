import { useEffect, useMemo } from "react"
import { useQuery } from "@tanstack/react-query"
import { Link } from "react-router-dom"
import { ArrowUpRight, CircleAlert, ScrollText, X } from "lucide-react"
import clsx from "clsx"
import { getLog, getLogFacets, getResources, searchLogs, type LogSearch } from "@/lib/api"
import { SeverityBadge } from "@/components/SeverityBadge"
import { Panel, EmptyState, Loading } from "@/components/Panel"
import { PageHeader } from "@/components/PageHeader"
import { ColumnPicker } from "@/components/ColumnPicker"
import { DateRangeFilter } from "@/components/DateRangeFilter"
import { TimeRangePicker } from "@/components/TimeRangePicker"
import { CodeBlock } from "@/components/CodeBlock"
import { Button, Pagination } from "@/components/ui"
import { formatAttributeValue, formatTimestamp } from "@/lib/format"
import { useLocalStorageState } from "@/lib/useLocalStorageState"
import { pageParam, useUrlState } from "@/lib/urlState"
import { readTimeRange } from "@/lib/timeRange"
import { decodeFieldFilters, decodeLegacyAttributeFilters, encodeFieldFilter } from "@/lib/traceSearch"
import { CopyButton } from "@/components/CopyButton"
import { SearchBar, type SearchState } from "@/components/search/SearchBar"
import { LogFacetPanel } from "@/components/search/LogFacetPanel"
import { FiltersButton } from "@/components/search/FiltersButton"

export function Logs() {
  // Every filter — and the open log record — lives in the URL so the view can be shared or
  // bookmarked, the same way as on the Traces page: q (one message condition each), f (field
  // filters), service and severity (repeatable), range or from/to, page, log.
  const { params, update } = useUrlState()
  const time = readTimeRange(params)
  const { range, from, to } = time

  // `search` and `attr` are the parameters links used before the search redesign.
  const textKey = [...params.getAll("q"), ...params.getAll("search")].join("\u0000")
  const filterKey = params.getAll("f").join("\u0000")
  const legacyKey = params.getAll("attr").join("\u0000")
  const serviceKey = params.getAll("service").join("\u0000")
  const severityKey = params.getAll("severity").join("\u0000")
  const page = pageParam(params)
  const pageSize = 50

  const state = useMemo<SearchState>(() => {
    const split = (key: string) => (key ? key.split("\u0000") : [])
    return {
      text: split(textKey),
      endpoints: [],
      filters: [...decodeFieldFilters(split(filterKey)), ...decodeLegacyAttributeFilters(split(legacyKey))],
    }
  }, [textKey, filterKey, legacyKey])
  const services = useMemo(() => (serviceKey ? serviceKey.split("\u0000").filter(Boolean) : []), [serviceKey])
  const severities = useMemo(
    () => (severityKey ? severityKey.split("\u0000").filter((v) => v !== "" && Number.isInteger(Number(v)) && Number(v) >= 0).map(Number) : []),
    [severityKey],
  )

  const logParam = Number(params.get("log"))
  const selectedId = Number.isInteger(logParam) && logParam > 0 ? logParam : null
  const setSelectedId = (id: number | null) => update({ log: id === null ? null : String(id), page: params.get("page") }, { replace: true })
  const [columns, setColumns] = useLocalStorageState<string[]>("powertraces.logs.columns", [])

  const search: LogSearch = {
    text: state.text,
    ...time.query,
    services,
    severities,
    filters: state.filters,
    page,
    pageSize,
  }

  const { data: serviceNames } = useQuery({ queryKey: ["resources"], queryFn: getResources })

  const { data, isLoading, isFetching, error } = useQuery({
    queryKey: ["log-search", search],
    queryFn: ({ signal }) => searchLogs(search, { signal }),
    placeholderData: (previous) => previous,
  })

  const facetSearch = { ...search, page: undefined, pageSize: undefined }
  const { data: facets } = useQuery({
    queryKey: ["log-facets", facetSearch],
    queryFn: ({ signal }) => getLogFacets(facetSearch, { signal }),
    placeholderData: (previous) => previous,
  })

  const { data: detail } = useQuery({
    queryKey: ["log", selectedId],
    queryFn: ({ signal }) => getLog(selectedId!, { signal }),
    enabled: selectedId !== null,
  })

  const setSearchState = (next: SearchState) =>
    update({
      q: next.text,
      search: null,
      attr: null,
      f: next.filters.map(encodeFieldFilter),
    })

  const total = data ? `${data.totalCount.toLocaleString()}${data.totalCountCapped ? "+" : ""}` : "…"
  const panelFilterCount = severities.length + services.length

  // One panel, shown beside the table when the content area is wide enough (whatever the sidebar
  // is doing) and behind the Filters button otherwise.
  const facetPanel = (
    <LogFacetPanel
      facets={facets}
      severities={severities}
      onSeverities={(next) => update({ severity: next.map(String) })}
      services={serviceNames ?? []}
      selectedServices={services}
      onServices={(service) => update({ service })}
    />
  )

  return (
    <div className="@container flex flex-col gap-4">
      <PageHeader
        title="Logs"
        subtitle="Structured log records from every instrumented service. Search messages or fields, and select a row for its details."
        actions={<CopyButton value={window.location.href} label="Copy link" showLabel className="h-[34px] border border-[var(--border)] bg-[var(--surface)] px-3 text-[var(--text-muted)]" />}
      />

      <div className="flex flex-col gap-2.5 rounded-xl border border-[var(--border)] bg-[var(--surface)] p-3 shadow-[var(--shadow-sm)]">
        <div className="flex flex-wrap items-start gap-2">
          <SearchBar scope="logs" state={state} onChange={setSearchState} />
          <TimeRangePicker range={range} from={from} update={update} />
        </div>
        {range === "custom" && <DateRangeFilter from={from} to={to} onChange={(f, t) => update({ from: f, to: t, range: f || t ? "custom" : null })} />}
        <div className="flex flex-wrap items-center justify-between gap-2 px-0.5 text-xs text-[var(--text-muted)]">
          <span>
            <strong className="font-semibold text-[var(--text)]">{total} logs</strong> {time.description}
            {isFetching && !isLoading && <span className="ml-2 text-[var(--text-dim)]">updating…</span>}
          </span>
          <div className="ml-auto flex items-center gap-2">
            <FiltersButton className="@5xl:hidden" active={panelFilterCount}>
              {facetPanel}
            </FiltersButton>
            <ColumnPicker scope="logs" selected={columns} onChange={setColumns} />
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
              label="No logs match this search."
              hint={range === "all" ? "Try removing a filter." : "Try a wider time range or removing a filter."}
              icon={ScrollText}
            />
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-[var(--border)] bg-[var(--subtle)] text-left text-[11px] tracking-wide text-[var(--text-dim)] uppercase">
                    <th className="px-4 py-2.5 font-medium">Time</th>
                    <th className="px-4 py-2.5 font-medium">Level</th>
                    <th className="px-4 py-2.5 font-medium">Service</th>
                    <th className="px-4 py-2.5 font-medium">Message</th>
                    {columns.map((key) => (
                      <th key={key} className="px-4 py-2.5 font-medium normal-case">
                        {key}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((log) => (
                    <tr
                      key={log.id}
                      onClick={() => setSelectedId(log.id === selectedId ? null : log.id)}
                      aria-selected={selectedId === log.id}
                      className={clsx(
                        "cursor-pointer border-b border-[var(--border)] transition-colors last:border-0",
                        selectedId === log.id ? "bg-[var(--accent)]/8" : "hover:bg-[var(--hover)]",
                      )}
                    >
                      <td className="px-4 py-2.5 font-mono text-xs whitespace-nowrap text-[var(--text-muted)]">{formatTimestamp(log.timestamp)}</td>
                      <td className="px-4 py-2.5">
                        <SeverityBadge severityNumber={log.severityNumber} text={log.severityText} />
                      </td>
                      <td className="max-w-[160px] truncate px-4 py-2.5 text-xs whitespace-nowrap text-[var(--text-muted)]" title={log.serviceName ?? undefined}>
                        {log.serviceName ?? "—"}
                      </td>
                      <td className="max-w-[560px] min-w-[220px] truncate px-4 py-2.5 font-mono text-xs" title={log.body ?? undefined}>
                        {log.body ?? "—"}
                      </td>
                      {columns.map((key) => (
                        <td key={key} className="max-w-[200px] truncate px-4 py-2.5 font-mono text-xs text-[var(--text-muted)]" title={formatAttributeValue(log.attributes[key])}>
                          {formatAttributeValue(log.attributes[key])}
                        </td>
                      ))}
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
              noun="logs"
              onPage={(p) => update({ page: p > 1 ? String(p) : null, log: null })}
            />
          )}
        </Panel>
      </div>

      {selectedId !== null && <LogDetailSheet detail={detail} onClose={() => setSelectedId(null)} />}
    </div>
  )
}

/** The selected log record, as a sheet over the right edge — it fits any screen width without
 * squeezing the table, and the table stays clickable behind it. */
function LogDetailSheet({ detail, onClose }: { detail: Awaited<ReturnType<typeof getLog>> | undefined; onClose: () => void }) {
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => e.key === "Escape" && onClose()
    document.addEventListener("keydown", onKeyDown)
    return () => document.removeEventListener("keydown", onKeyDown)
  }, [onClose])

  return (
    <aside
      aria-label="Log record"
      className="fixed inset-y-0 right-0 z-40 flex w-full flex-col border-l border-[var(--border)] bg-[var(--surface)] shadow-[var(--shadow-md)] sm:w-[440px]"
    >
      <header className="flex items-center justify-between gap-3 border-b border-[var(--border)] px-4 py-3">
        <h2 className="text-[13px] font-semibold text-[var(--text)]">Log record</h2>
        <Button variant="ghost" size="sm" onClick={onClose} aria-label="Close details">
          <X size={14} />
        </Button>
      </header>
      <div className="min-h-0 flex-1 overflow-y-auto">
        {!detail ? (
          <Loading />
        ) : (
          <div className="flex flex-col gap-4 p-4 text-sm">
            <div className="flex items-center gap-2">
              <SeverityBadge severityNumber={detail.severityNumber} text={detail.severityText} />
              <span className="font-mono text-xs text-[var(--text-muted)]">{formatTimestamp(detail.timestamp)}</span>
            </div>
            <dl className="grid grid-cols-[88px_1fr] gap-x-3 gap-y-2 text-xs">
              <dt className="text-[var(--text-dim)]">Service</dt>
              <dd className="truncate">{detail.serviceName ?? "—"}</dd>
              <dt className="text-[var(--text-dim)]">Category</dt>
              <dd className="truncate font-mono">{detail.scopeLibraryName ?? "—"}</dd>
              {detail.traceId && (
                <>
                  <dt className="text-[var(--text-dim)]">Trace</dt>
                  <dd>
                    <Link to={`/traces/${detail.traceId}`} className="inline-flex items-center gap-1 font-mono text-[var(--accent)] hover:underline">
                      {detail.traceId.slice(0, 16)}… <ArrowUpRight size={12} />
                    </Link>
                    <CopyButton value={detail.traceId} label="Copy trace id" className="ml-1 align-middle" />
                  </dd>
                </>
              )}
            </dl>
            <div>
              <div className="mb-1.5 flex items-center justify-between text-[11px] font-medium text-[var(--text-dim)]">
                Message
                {detail.body && <CopyButton value={detail.body} label="Copy message" />}
              </div>
              <pre className="rounded-lg border border-[var(--border)] bg-[var(--subtle)] p-3 font-mono text-xs break-all whitespace-pre-wrap">{detail.body ?? "—"}</pre>
            </div>
            <div>
              <div className="mb-1.5 flex items-center justify-between text-[11px] font-medium text-[var(--text-dim)]">
                Attributes
                <CopyButton value={JSON.stringify(detail.attributes, null, 2)} label="Copy attributes as JSON" />
              </div>
              <CodeBlock value={JSON.stringify(detail.attributes)} />
            </div>
          </div>
        )}
      </div>
    </aside>
  )
}
