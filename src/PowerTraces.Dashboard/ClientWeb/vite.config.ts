import path from "path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig } from "vite"

export default defineConfig({
  plugins: [react(), tailwindcss()],
  // Relative, not "/": PowerTracesDashboardEndpointRouteBuilderExtensions serves this SPA from a host-chosen prefix
  // (e.g. "/otel") decided at runtime, not at build time, so asset URLs must resolve via the
  // page's injected <base href> rather than being baked in as absolute from origin root.
  base: "./",
  resolve: {
    alias: {
      "@": path.resolve(import.meta.dirname, "./src"),
    },
  },
  build: {
    // Output directly into the assembly's embedded wwwroot
    outDir: "../wwwroot",
    emptyOutDir: true,
  },
  server: {
    port: 5176,
    proxy: {
      "/api": { target: "http://localhost:5080", changeOrigin: true },
    },
  },
})
