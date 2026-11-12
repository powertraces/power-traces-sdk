import { useState, type ReactNode } from "react"
import { Link } from "react-router-dom"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { isAxiosError } from "axios"
import { Archive, CircleAlert, CircleCheck, History, Pin, PinOff, Play, Save } from "lucide-react"
import clsx from "clsx"
import { getPinnedTraces, getRetention, runRetention, saveRetention, unpinTrace, type RetentionSettings } from "@/lib/api"
import { EmptyState, Loading, Panel } from "@/components/Panel"
import { PageHeader } from "@/components/PageHeader"
import { Button } from "@/components/ui"
import { formatDuration, formatRelative, formatTimestamp } from "@/lib/format"

const MAX_DAYS = 3650

function errorMessage(error: unknown): string {
  if (isAxiosError(error)) {
    const data = error.response?.data
    if (typeof data === "string" && data) return data
    if (data && typeof data === "object" && "detail" in data && typeof data.detail === "string") return data.detail
  }
  return "Something went wrong."
}

function Toggle({ checked, onChange, label, description, disabled }: { checked: boolean; onChange: (v: boolean) => void; label: string; description: string; disabled?: boolean }) {
  return (
    <label className={clsx("flex items-start gap-3", disabled ? "opacity-50" : "cursor-pointer")}>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        disabled={disabled}
        onClick={() => onChange(!checked)}
        className={clsx(
          "relative mt-0.5 inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors",
          checked ? "bg-[var(--accent)]" : "bg-[var(--border-strong)]",
        )}
      >
        <span className={clsx("inline-block h-4 w-4 rounded-full bg-white shadow-sm transition-transform", checked ? "translate-x-[18px]" : "translate-x-0.5")} />
      </button>
      <span className="flex flex-col gap-0.5">
        <span className="text-[13px] font-medium text-[var(--text)]">{label}</span>
        <span className="text-xs text-[var(--text-dim)]">{description}</span>
      </span>
    </label>
  )
}

function DaysField({ label, hint, value, onChange, disabled }: { label: string; hint: string; value: number; onChange: (v: number) => void; disabled?: boolean }) {
  const invalid = !Number.isInteger(value) || value < 1 || value > MAX_DAYS
  return (
    <label className={clsx("flex flex-col gap-1.5", disabled && "opacity-50")}>
      <span className="text-[13px] font-medium text-[var(--text)]">{label}</span>
      <span className="flex items-center gap-2">
        <input
          type="number"
          min={1}
          max={MAX_DAYS}
          value={Number.isNaN(value) ? "" : value}
          disabled={disabled}
          onChange={(e) => onChange(e.target.valueAsNumber)}
          aria-invalid={invalid}
          className={clsx("control w-28 tabular-nums", invalid && "border-[var(--error)]")}
        />
        <span className="text-xs text-[var(--text-muted)]">days</span>
      </span>
      <span className="text-xs text-[var(--text-dim)]">{hint}</span>
    </label>
  )
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5">
      <dt className="text-[11px] font-medium tracking-wide text-[var(--text-dim)] uppercase">{label}</dt>
      <dd className="text-[13px] text-[var(--text)] tabular-nums">{children}</dd>
    </div>
  )
}

export function Retention() {
  const { data, isLoading, isError, error } = useQuery({ queryKey: ["retention"], queryFn: getRetention, retry: false })

  if (isLoading) return <Loading label="Loading retention…" />
  if (isError || !data) {
    return (
      <div className="flex flex-col gap-4">
        <PageHeader title="Retention" />
        <Panel>
          <EmptyState label="Retention isn't available." hint={errorMessage(error)} icon={Archive} />
        </Panel>
      </div>
    )
  }
  // Keyed on the saved version, so the form resets to what's stored after every save.
  return <RetentionForm key={data.updatedAt ?? "unsaved"} saved={data.settings} updatedAt={data.updatedAt} />
}

function RetentionForm({ saved, updatedAt }: { saved: RetentionSettings; updatedAt: string | null }) {
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState<RetentionSettings>(saved)
  const set = <K extends keyof RetentionSettings>(key: K, value: RetentionSettings[K]) => setDraft((d) => ({ ...d, [key]: value }))

  const { data: state } = useQuery({ queryKey: ["retention"], queryFn: getRetention, retry: false })
  const { data: pinned, isLoading: pinnedLoading } = useQuery({ queryKey: ["pinned-traces"], queryFn: getPinnedTraces })

  const refresh = () => queryClient.invalidateQueries({ queryKey: ["retention"] })
  const save = useMutation({ mutationFn: () => saveRetention(draft), onSuccess: (next) => queryClient.setQueryData(["retention"], next) })
  const run = useMutation({ mutationFn: runRetention, onSettled: refresh })
  const unpin = useMutation({ mutationFn: unpinTrace, onSuccess: () => queryClient.invalidateQueries({ queryKey: ["pinned-traces"] }) })

  const daysValid = [draft.traceRetentionDays, draft.logRetentionDays].every((d) => Number.isInteger(d) && d >= 1 && d <= MAX_DAYS)
  const dirty = JSON.stringify(draft) !== JSON.stringify(saved)
  const lastRun = state?.lastRun ?? null

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title="Retention"
        subtitle="Delete old traces and logs automatically, while keeping failures and the traces you pin. Applies to every app writing to this storage."
        actions={
          <Button onClick={() => run.mutate()} disabled={!saved.enabled || dirty || run.isPending} title={saved.enabled ? "Delete everything that has expired now, instead of at the next scheduled cleanup" : "Enable and save retention first"}>
            <Play size={13} /> {run.isPending ? "Cleaning up…" : "Run cleanup now"}
          </Button>
        }
      />

      <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]">
        <Panel title="Policy" icon={Archive} subtitle={updatedAt ? `Last changed ${formatRelative(updatedAt)}` : "Not configured yet"}>
          <form
            className="flex flex-col gap-5 border-t border-[var(--border)] p-4"
            onSubmit={(e) => {
              e.preventDefault()
              if (daysValid && dirty) save.mutate()
            }}
          >
            <Toggle
              checked={draft.enabled}
              onChange={(v) => set("enabled", v)}
              label="Delete old telemetry automatically"
              description="Cleanup runs at startup and about hourly in the app that hosts this dashboard. While off, nothing is ever deleted."
            />

            <div className="grid gap-5 sm:grid-cols-2">
              <DaysField
                label="Keep traces for"
                hint="Traces that started longer ago are deleted, with all their spans."
                value={draft.traceRetentionDays}
                onChange={(v) => set("traceRetentionDays", v)}
                disabled={!draft.enabled}
              />
              <DaysField
                label="Keep logs for"
                hint="Log records written longer ago are deleted."
                value={draft.logRetentionDays}
                onChange={(v) => set("logRetentionDays", v)}
                disabled={!draft.enabled}
              />
            </div>

            <div className="flex flex-col gap-4 rounded-lg bg-[var(--subtle)] p-4 ring-1 ring-[var(--border)]">
              <span className="text-xs font-semibold text-[var(--text-muted)]">Never delete</span>
              <Toggle
                checked={draft.keepErrorTraces}
                onChange={(v) => set("keepErrorTraces", v)}
                label="Failed traces"
                description="Traces with at least one failing span are kept however old they are."
                disabled={!draft.enabled}
              />
              <Toggle
                checked={draft.keepErrorLogs}
                onChange={(v) => set("keepErrorLogs", v)}
                label="Error logs"
                description="Log records at Error severity or above are kept."
                disabled={!draft.enabled}
              />
              <p className="flex items-start gap-2 text-xs text-[var(--text-dim)]">
                <Pin size={12} className="mt-0.5 shrink-0" aria-hidden="true" />
                Pinned traces — and the logs that belong to them — are always kept. Pin one with “Keep forever” on its trace page.
              </p>
            </div>

            <div className="flex flex-wrap items-center gap-3">
              <Button type="submit" variant="primary" disabled={!dirty || !daysValid || save.isPending}>
                <Save size={13} /> {save.isPending ? "Saving…" : "Save policy"}
              </Button>
              {dirty && (
                <Button type="button" variant="ghost" onClick={() => setDraft(saved)}>
                  Discard changes
                </Button>
              )}
              {save.isError && <span className="text-xs text-[var(--error)]">{errorMessage(save.error)}</span>}
              {!daysValid && <span className="text-xs text-[var(--error)]">Use a whole number of days between 1 and {MAX_DAYS.toLocaleString()}.</span>}
            </div>
          </form>
        </Panel>

        <Panel title="Last cleanup" icon={History}>
          <div className="border-t border-[var(--border)] p-4">
            {run.isError ? (
              <p className="flex items-start gap-2 text-[13px] text-[var(--error)]">
                <CircleAlert size={14} className="mt-0.5 shrink-0" /> {errorMessage(run.error)}
              </p>
            ) : !lastRun ? (
              <p className="text-[13px] text-[var(--text-muted)]">{saved.enabled ? "No cleanup has run yet — the first one starts within the hour." : "Retention is off, so nothing has been cleaned up."}</p>
            ) : (
              <div className="flex flex-col gap-4">
                <p className={clsx("flex items-start gap-2 text-[13px]", lastRun.error ? "text-[var(--error)]" : "text-[var(--success)]")}>
                  {lastRun.error ? <CircleAlert size={14} className="mt-0.5 shrink-0" /> : <CircleCheck size={14} className="mt-0.5 shrink-0" />}
                  <span className="break-words">{lastRun.error ? `Failed: ${lastRun.error}` : "Completed"}</span>
                </p>
                <dl className="grid grid-cols-2 gap-4">
                  <Fact label="Traces deleted">{lastRun.tracesDeleted.toLocaleString()}</Fact>
                  <Fact label="Logs deleted">{lastRun.logsDeleted.toLocaleString()}</Fact>
                  <Fact label="Ran">
                    <span title={formatTimestamp(lastRun.startedAt)}>{formatRelative(lastRun.startedAt)}</span>
                  </Fact>
                  <Fact label="Took">{formatDuration(new Date(lastRun.finishedAt).getTime() - new Date(lastRun.startedAt).getTime())}</Fact>
                </dl>
              </div>
            )}
          </div>
        </Panel>
      </div>

      <Panel title="Pinned traces" icon={Pin} subtitle={pinned ? `${pinned.length} kept forever` : undefined}>
        {pinnedLoading ? (
          <Loading />
        ) : !pinned?.length ? (
          <EmptyState label="No pinned traces." hint="Use “Keep forever” on a trace to exempt it, and its logs, from retention." icon={Pin} />
        ) : (
          <ul className="border-t border-[var(--border)]">
            {pinned.map((p) => (
              <li key={p.traceId} className="flex items-center gap-3 border-b border-[var(--border)] px-4 py-3 last:border-0 hover:bg-[var(--hover)]">
                <span
                  className={clsx(
                    "flex h-8 w-8 shrink-0 items-center justify-center rounded-lg",
                    p.hasError ? "bg-[var(--error)]/12 text-[var(--error)]" : "bg-[var(--accent)]/12 text-[var(--accent)]",
                  )}
                  title={p.hasError ? "Failed trace" : "Trace"}
                >
                  {p.hasError ? <CircleAlert size={14} /> : <Pin size={14} />}
                </span>
                <Link to={`/traces/${p.traceId}`} className="group min-w-0 flex-1">
                  <span className="block truncate font-mono text-[13px] text-[var(--text)] group-hover:text-[var(--accent)]" title={p.rootName ?? p.traceId}>
                    {p.rootName ?? p.traceId}
                  </span>
                  <span className="block truncate text-[11px] text-[var(--text-dim)]">
                    {[p.serviceName, p.startTime && `started ${formatTimestamp(p.startTime)}`, `pinned ${formatRelative(p.pinnedAt)}`].filter(Boolean).join(" · ")}
                  </span>
                </Link>
                <Button variant="danger" size="sm" onClick={() => unpin.mutate(p.traceId)} disabled={unpin.isPending} title="Let retention delete this trace again once it expires">
                  <PinOff size={13} /> Unpin
                </Button>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  )
}
