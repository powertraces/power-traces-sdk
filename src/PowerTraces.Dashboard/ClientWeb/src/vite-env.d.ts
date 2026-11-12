/// <reference types="vite/client" />

interface Window {
  /** The path prefix PowerTracesDashboardEndpointRouteBuilderExtensions mounted the dashboard under, e.g. "/otel" (or "" at the root). */
  __POWERTRACES_BASE__?: string
  /** Branding set via `MapPowerTracesDashboard(..., configureUi: o => ...)` — falls back to the default "PowerTraces"/"Exporter" when unset. */
  __POWERTRACES_CONFIG__?: { title?: string; subtitle?: string }
}
