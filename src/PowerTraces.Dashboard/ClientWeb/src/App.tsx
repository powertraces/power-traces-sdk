import { Route, Routes } from "react-router-dom"
import { Layout } from "@/components/Layout"
import { Dashboard } from "@/pages/Dashboard"
import { Logs } from "@/pages/Logs"
import { Traces } from "@/pages/Traces"
import { TraceDetail } from "@/pages/TraceDetail"
import { Ignored } from "@/pages/Ignored"
import { Retention } from "@/pages/Retention"

export default function App() {
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<Dashboard />} />
        <Route path="/logs" element={<Logs />} />
        <Route path="/traces" element={<Traces />} />
        <Route path="/traces/:traceId" element={<TraceDetail />} />
        <Route path="/ignored" element={<Ignored />} />
        <Route path="/retention" element={<Retention />} />
      </Routes>
    </Layout>
  )
}
