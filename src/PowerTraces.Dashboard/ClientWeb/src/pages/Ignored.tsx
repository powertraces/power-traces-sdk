import { useState } from "react"
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"
import { Ban, GitBranch, Plus, Trash2, Waypoints } from "lucide-react"
import { createIgnoreRule, deleteIgnoreRule, getIgnoreRules, type IgnoreRuleKind } from "@/lib/api"
import { EmptyState, Loading, Panel } from "@/components/Panel"
import { PageHeader } from "@/components/PageHeader"
import { Button, Segmented } from "@/components/ui"
import { formatRelative } from "@/lib/format"

export function Ignored() {
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<IgnoreRuleKind>("trace")
  const [name, setName] = useState("")
  const { data: rules, isLoading } = useQuery({ queryKey: ["ignore-rules"], queryFn: getIgnoreRules })

  const refresh = () => queryClient.invalidateQueries({ queryKey: ["ignore-rules"] })
  const add = useMutation({
    mutationFn: () => createIgnoreRule(kind, name.trim()),
    onSuccess: () => {
      setName("")
      refresh()
    },
  })
  const remove = useMutation({ mutationFn: deleteIgnoreRule, onSuccess: refresh })

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title="Ignore rules"
        subtitle="Stop exporting noise such as health checks and chatty spans. Rules apply to telemetry exported from now on; nothing already stored is deleted."
      />

      <Panel title="Add a rule" icon={Plus}>
        <form
          className="flex flex-wrap items-center gap-2 border-t border-[var(--border)] p-4"
          onSubmit={(e) => {
            e.preventDefault()
            if (name.trim()) add.mutate()
          }}
        >
          <Segmented
            label="Rule kind"
            value={kind}
            onChange={setKind}
            options={[
              { value: "trace", label: "Whole trace" },
              { value: "span", label: "Single span" },
            ]}
          />
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={kind === "trace" ? "Root span name, e.g. GET /health" : "Span name, e.g. SELECT 1"}
            aria-label={kind === "trace" ? "Root span name" : "Span name"}
            className="control min-w-[240px] flex-1 font-mono"
          />
          <Button type="submit" variant="primary" disabled={!name.trim() || add.isPending}>
            <Ban size={13} /> Ignore
          </Button>
          <p className="w-full text-xs text-[var(--text-dim)]">
            {kind === "trace"
              ? "Drops every span of a trace whose root span has exactly this name, including child spans already written."
              : "Drops only spans with exactly this name; the rest of their trace is still exported."}
          </p>
        </form>
      </Panel>

      <Panel title="Active rules" subtitle={rules ? `${rules.length} rule${rules.length === 1 ? "" : "s"}` : undefined} icon={Ban}>
        {isLoading ? (
          <Loading />
        ) : !rules?.length ? (
          <EmptyState label="No ignore rules yet." hint="Add one above, or use “Ignore” on a trace or span in the trace view." icon={Ban} />
        ) : (
          <ul className="border-t border-[var(--border)]">
            {rules.map((rule) => (
              <li key={rule.id} className="flex items-center gap-3 border-b border-[var(--border)] px-4 py-3 last:border-0 hover:bg-[var(--hover)]">
                <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg bg-[var(--warn)]/12 text-[var(--warn)]">
                  {rule.kind === "trace" ? <GitBranch size={14} /> : <Waypoints size={14} />}
                </span>
                <span className="min-w-0 flex-1">
                  <code className="block truncate font-mono text-[13px] text-[var(--text)]" title={rule.name}>
                    {rule.name}
                  </code>
                  <span className="text-[11px] text-[var(--text-dim)]">
                    {rule.kind === "trace" ? "Whole trace" : "Single span"} · added {formatRelative(rule.createdAt)}
                  </span>
                </span>
                <Button
                  variant="danger"
                  size="sm"
                  onClick={() => remove.mutate(rule.id)}
                  disabled={remove.isPending}
                  title="Remove the rule and resume exporting matching telemetry"
                >
                  <Trash2 size={13} /> Remove
                </Button>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  )
}
