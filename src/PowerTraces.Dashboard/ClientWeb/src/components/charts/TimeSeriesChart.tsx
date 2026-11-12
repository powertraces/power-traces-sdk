import { useLayoutEffect, useMemo, useRef, useState } from "react"

export interface Series {
  key: string
  label: string
  /** A CSS color — pass a token, e.g. "var(--series-1)". */
  color: string
  /** null = no data in that bucket: a gap in a line (not a drop to zero), nothing in a stack. */
  values: (number | null)[]
}

interface TimeSeriesChartProps {
  /** Bucket start times (ISO strings), one per value. */
  times: string[]
  series: Series[]
  /** "stacked" columns (series stack bottom-up in order) or one "line" per series. */
  kind: "stacked" | "line"
  height?: number
  formatValue?: (v: number) => string
  /** Extra per-bucket rows for the tooltip (values not plotted, e.g. warning counts). */
  extraTooltipRows?: (index: number) => { label: string; value: string }[]
  ariaLabel: string
}

const PAD = { top: 12, right: 12, bottom: 24, left: 44 }

/** Rounds the max up to a "nice" number and returns ~4 evenly spaced ticks from zero. */
function niceTicks(max: number): number[] {
  if (max <= 0) return [0, 1]
  const rough = max / 4
  const pow = 10 ** Math.floor(Math.log10(rough))
  const step = [1, 2, 2.5, 5, 10].map((m) => m * pow).find((s) => s >= rough) ?? rough
  const ticks: number[] = []
  for (let v = 0; v <= max + step * 0.999; v += step) ticks.push(Math.round(v * 1000) / 1000)
  return ticks
}

function formatTick(t: Date, spanMs: number) {
  return spanMs > 2 * 86_400_000
    ? t.toLocaleDateString(undefined, { month: "short", day: "numeric" })
    : t.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })
}

/** A dependency-free SVG time-series chart: recessive hairline grid, one y-axis, columns capped
 * at 24px with 2px surface gaps between stacked segments and a 4px rounded top, 2px lines, and a
 * crosshair + tooltip on hover. The data table view is the page's own table/list panels. */
export function TimeSeriesChart({ times, series, kind, height = 200, formatValue = (v) => v.toLocaleString(), extraTooltipRows, ariaLabel }: TimeSeriesChartProps) {
  const ref = useRef<HTMLDivElement>(null)
  const [width, setWidth] = useState(600)
  const [hover, setHover] = useState<number | null>(null)

  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const observer = new ResizeObserver(([entry]) => setWidth(Math.max(200, entry.contentRect.width)))
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  const n = times.length
  const innerW = width - PAD.left - PAD.right
  const innerH = height - PAD.top - PAD.bottom
  const slot = n > 0 ? innerW / n : innerW

  const max = useMemo(() => {
    if (kind === "stacked") return Math.max(0, ...times.map((_, i) => series.reduce((s, x) => s + (x.values[i] ?? 0), 0)))
    return Math.max(0, ...series.flatMap((s) => s.values.map((v) => v ?? 0)))
  }, [kind, series, times])

  const ticks = niceTicks(max)
  const top = ticks[ticks.length - 1] || 1
  const y = (v: number) => PAD.top + innerH - (v / top) * innerH
  const xCenter = (i: number) => PAD.left + slot * i + slot / 2

  const spanMs = n > 1 ? new Date(times[n - 1]).getTime() - new Date(times[0]).getTime() : 0
  const xTickEvery = Math.max(1, Math.ceil(n / Math.max(2, Math.floor(innerW / 90))))
  const barW = Math.max(1, Math.min(24, slot - 2))

  const onMove = (e: React.MouseEvent<SVGRectElement>) => {
    const rect = e.currentTarget.getBoundingClientRect()
    const i = Math.floor(((e.clientX - rect.left) / rect.width) * n)
    setHover(i >= 0 && i < n ? i : null)
  }

  const tooltipLeft = hover === null ? 0 : xCenter(hover)
  const flip = tooltipLeft > width * 0.6

  return (
    <div ref={ref} className="relative w-full" style={{ height }}>
      <svg width={width} height={height} role="img" aria-label={ariaLabel} className="block overflow-visible">
        {/* grid + y ticks */}
        {ticks.map((t) => (
          <g key={t}>
            <line x1={PAD.left} x2={width - PAD.right} y1={y(t)} y2={y(t)} stroke={t === 0 ? "var(--axis)" : "var(--grid)"} strokeWidth={1} />
            <text x={PAD.left - 8} y={y(t)} dy="0.32em" textAnchor="end" fontSize={10} fill="var(--text-dim)" style={{ fontVariantNumeric: "tabular-nums" }}>
              {t === 0 ? "0" : formatValue(t)}
            </text>
          </g>
        ))}

        {/* x ticks */}
        {times.map((t, i) =>
          i % xTickEvery === 0 ? (
            <text key={t} x={xCenter(i)} y={height - 6} textAnchor="middle" fontSize={10} fill="var(--text-dim)">
              {formatTick(new Date(t), spanMs)}
            </text>
          ) : null,
        )}

        {hover !== null && <rect x={PAD.left + slot * hover} y={PAD.top} width={slot} height={innerH} fill="var(--hover-strong)" />}

        {kind === "stacked" &&
          times.map((_, i) => {
            let base = 0
            const segments = series
              .map((s) => ({ s, v: s.values[i] ?? 0 }))
              .filter((x) => x.v > 0)
            return segments.map(({ s, v }, j) => {
              const y0 = y(base)
              const y1 = y(base + v)
              base += v
              const isTop = j === segments.length - 1
              // 2px surface gap between stacked segments; only the top segment gets the rounded end.
              const h = Math.max(1, y0 - y1 - (j > 0 ? 2 : 0))
              const x = xCenter(i) - barW / 2
              const r = isTop ? Math.min(4, barW / 2, h) : 0
              const path = `M${x},${y1 + h} V${y1 + r} Q${x},${y1} ${x + r},${y1} H${x + barW - r} Q${x + barW},${y1} ${x + barW},${y1 + r} V${y1 + h} Z`
              return <path key={s.key} d={path} fill={s.color} />
            })
          })}

        {kind === "line" &&
          series.map((s) => {
            // One path per run of consecutive buckets that have data — empty buckets are gaps.
            const runs: number[][] = []
            s.values.forEach((v, i) => {
              if (v === null) return
              if (i === 0 || s.values[i - 1] === null) runs.push([])
              runs[runs.length - 1].push(i)
            })
            const hovered = hover === null ? null : s.values[hover]
            return (
              <g key={s.key}>
                {runs.map((run) => {
                  const d = run.map((i, k) => `${k === 0 ? "M" : "L"}${xCenter(i)},${y(s.values[i] ?? 0)}`).join(" ")
                  const area = `${d} L${xCenter(run[run.length - 1])},${y(0)} L${xCenter(run[0])},${y(0)} Z`
                  return (
                    <g key={run[0]}>
                      <path d={area} fill={s.color} opacity={0.1} />
                      {run.length === 1 ? (
                        <circle cx={xCenter(run[0])} cy={y(s.values[run[0]] ?? 0)} r={2} fill={s.color} />
                      ) : (
                        <path d={d} fill="none" stroke={s.color} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
                      )}
                    </g>
                  )
                })}
                {hovered != null && hover !== null && (
                  <circle cx={xCenter(hover)} cy={y(hovered)} r={4} fill={s.color} stroke="var(--surface)" strokeWidth={2} />
                )}
              </g>
            )
          })}

        <rect
          x={PAD.left}
          y={PAD.top}
          width={innerW}
          height={innerH}
          fill="transparent"
          onMouseMove={onMove}
          onMouseLeave={() => setHover(null)}
        />
      </svg>

      {hover !== null && (
        <div
          className="pointer-events-none absolute z-10 min-w-[150px] rounded-lg border border-[var(--border)] bg-[var(--surface-raised)] px-3 py-2 text-xs shadow-[var(--shadow-md)]"
          style={{ top: PAD.top, left: flip ? undefined : tooltipLeft + 12, right: flip ? width - tooltipLeft + 12 : undefined }}
        >
          <div className="mb-1.5 font-medium text-[var(--text-muted)]">
            {new Date(times[hover]).toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" })}
          </div>
          {[...series].reverse().map((s) => (
            <div key={s.key} className="flex items-center justify-between gap-4 py-0.5">
              <span className="flex items-center gap-1.5 text-[var(--text-muted)]">
                <span className="h-2 w-2 rounded-sm" style={{ background: s.color }} />
                {s.label}
              </span>
              <span className="font-medium text-[var(--text)] tabular-nums">{s.values[hover] == null ? "—" : formatValue(s.values[hover]!)}</span>
            </div>
          ))}
          {extraTooltipRows?.(hover).map((row) => (
            <div key={row.label} className="flex items-center justify-between gap-4 py-0.5">
              <span className="pl-3.5 text-[var(--text-muted)]">{row.label}</span>
              <span className="font-medium text-[var(--text)] tabular-nums">{row.value}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

/** One series' legend row — swatch plus a text-token label (the text never takes the series color). */
export function Legend({ items }: { items: { label: string; color: string }[] }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-[var(--text-muted)]">
      {items.map((i) => (
        <span key={i.label} className="flex items-center gap-1.5">
          <span className="h-2.5 w-2.5 rounded-sm" style={{ background: i.color }} />
          {i.label}
        </span>
      ))}
    </div>
  )
}

/** A tiny trend line for KPI tiles — decoration beside the number, so no axes or hover. */
export function Sparkline({ values, color, height = 32 }: { values: number[]; color: string; height?: number }) {
  if (values.length < 2) return <div style={{ height }} />
  const w = 120
  const max = Math.max(...values, 1)
  const pts = values.map((v, i) => [(i / (values.length - 1)) * w, height - 2 - (v / max) * (height - 4)] as const)
  const d = pts.map(([x, yy], i) => `${i === 0 ? "M" : "L"}${x.toFixed(1)},${yy.toFixed(1)}`).join(" ")
  return (
    <svg viewBox={`0 0 ${w} ${height}`} preserveAspectRatio="none" className="h-8 w-full" aria-hidden>
      <path d={`${d} L${w},${height} L0,${height} Z`} fill={color} opacity={0.1} />
      <path d={d} fill="none" stroke={color} strokeWidth={2} vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
    </svg>
  )
}
