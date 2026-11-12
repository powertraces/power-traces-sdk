import { useMemo } from "react"
import hljs from "highlight.js/lib/core"
import json from "highlight.js/lib/languages/json"
import sql from "highlight.js/lib/languages/sql"
import xml from "highlight.js/lib/languages/xml"

hljs.registerLanguage("json", json)
hljs.registerLanguage("sql", sql)
hljs.registerLanguage("xml", xml)

const SQL_KEYWORD_PREFIX = /^\s*(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|WITH|EXEC|MERGE)\b/i
const SQL_HINT_KEYS = ["db.statement", "db.query.text"]

interface CodeBlockProps {
  value: string
  attributeKey?: string
  className?: string
  /** Flush under a list row: no rounded border, only a top divider, and plain text kept as a
   * wrapped block too. */
  bare?: boolean
}

/** Formats+highlights a value that looks like JSON, XML, or SQL; falls back to plain text
 * (unchanged from before this component existed) for ordinary scalar attribute values. JSON
 * covers NoSQL command/document values too — they're JSON on the wire (e.g. MongoDB). */
export function CodeBlock({ value, attributeKey, className, bare = false }: CodeBlockProps) {
  const formatted = useMemo(() => detectAndFormat(value, attributeKey), [value, attributeKey])

  const bareBox = "max-h-[420px] overflow-y-auto border-t border-[var(--border)] bg-[var(--subtle)] px-3 py-2.5 font-mono text-xs"

  if (!formatted && bare) {
    return <pre className={`${bareBox} break-all whitespace-pre-wrap ${className ?? ""}`}>{value}</pre>
  }

  if (!formatted) {
    return <span className={`font-mono text-xs break-all ${className ?? ""}`}>{value}</span>
  }

  return (
    <pre className={`overflow-x-auto ${bare ? bareBox : "rounded-md border border-[var(--border)] bg-[var(--subtle)] p-2.5 font-mono text-xs"} ${className ?? ""}`}>
      <code
        className={`hljs language-${formatted.language}`}
        // Safe: the HTML here is highlight.js's own escaped-and-tagged output of `formatted.code`,
        // not unescaped user content.
        dangerouslySetInnerHTML={{ __html: hljs.highlight(formatted.code, { language: formatted.language }).value }}
      />
    </pre>
  )
}

function detectAndFormat(value: string, attributeKey?: string): { code: string; language: "json" | "xml" | "sql" } | null {
  const trimmed = value.trim()
  if (!trimmed) {
    return null
  }

  if (trimmed.startsWith("{") || trimmed.startsWith("[")) {
    try {
      return { code: JSON.stringify(JSON.parse(trimmed), null, 2), language: "json" }
    } catch {
      // Not actually valid JSON (e.g. a value that just happens to start with "{") — fall through.
    }
  }

  if (trimmed.startsWith("<")) {
    return { code: trimmed, language: "xml" }
  }

  if ((attributeKey && SQL_HINT_KEYS.includes(attributeKey)) || SQL_KEYWORD_PREFIX.test(trimmed)) {
    return { code: trimmed, language: "sql" }
  }

  return null
}
