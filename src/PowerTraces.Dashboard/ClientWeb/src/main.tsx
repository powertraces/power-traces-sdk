import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { BrowserRouter } from "react-router-dom"
import App from "./App"
import "./index.css"

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: 1,
      staleTime: 5_000,
    },
  },
})

// The dashboard is mounted by the host app at an arbitrary prefix (e.g. "/otel"), chosen at
// runtime — not known at build time — so PowerTracesDashboardEndpointRouteBuilderExtensions injects it into the served
// HTML as a global before this script runs.
const basename = window.__POWERTRACES_BASE__ ?? ""

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter basename={basename}>
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
)
