import { useState, type ReactNode } from "react"
import { NavLink } from "react-router-dom"
import { Archive, Ban, GitBranch, LayoutDashboard, Menu, Monitor, Moon, PanelLeftClose, PanelLeftOpen, ScrollText, Sun, X } from "lucide-react"
import clsx from "clsx"
import { useTheme, type ThemeChoice } from "@/lib/theme"
import { useLocalStorageState } from "@/lib/useLocalStorageState"

const navItems = [
  { to: "/", label: "Overview", icon: LayoutDashboard, end: true },
  { to: "/traces", label: "Traces", icon: GitBranch, end: false },
  { to: "/logs", label: "Logs", icon: ScrollText, end: false },
  { to: "/ignored", label: "Ignore rules", icon: Ban, end: false },
  { to: "/retention", label: "Retention", icon: Archive, end: false },
]

const themeOptions: { value: ThemeChoice; label: string; icon: typeof Sun }[] = [
  { value: "light", label: "Light", icon: Sun },
  { value: "dark", label: "Dark", icon: Moon },
  { value: "system", label: "System", icon: Monitor },
]

function BrandMark() {
  return (
    <svg viewBox="0 0 32 32" className="h-8 w-8 shrink-0" aria-hidden>
      <rect width="32" height="32" rx="8" fill="var(--accent)" />
      <path d="M7 19h4l2.5-7 4 12 3-9 1.5 4H25" fill="none" stroke="var(--accent-contrast)" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

/** The navigation. `collapsed` is the narrow desktop rail: icons only, with names as tooltips;
 * `onToggle` (desktop only) shows the collapse/expand button. */
function Sidebar({ onNavigate, collapsed = false, onToggle }: { onNavigate?: () => void; collapsed?: boolean; onToggle?: () => void }) {
  const title = window.__POWERTRACES_CONFIG__?.title ?? "PowerTraces"
  const subtitle = window.__POWERTRACES_CONFIG__?.subtitle ?? "Telemetry"
  const [theme, setTheme] = useTheme()

  return (
    <div className="flex h-full flex-col">
      <div className={clsx("flex items-center gap-3 py-5", collapsed ? "justify-center px-2" : "px-4")}>
        <span title={collapsed ? title : undefined}>
          <BrandMark />
        </span>
        {!collapsed && (
          <div className="min-w-0 flex-1">
            <div className="truncate text-[15px] leading-tight font-semibold tracking-tight">{title}</div>
            <div className="truncate text-xs text-[var(--text-dim)]">{subtitle}</div>
          </div>
        )}
      </div>

      <nav className={clsx("flex flex-col gap-0.5", collapsed ? "px-2" : "px-3")} aria-label="Main">
        {navItems.map(({ to, label, icon: Icon, end }) => (
          <NavLink
            key={to}
            to={to}
            end={end}
            onClick={onNavigate}
            title={collapsed ? label : undefined}
            aria-label={collapsed ? label : undefined}
            className={({ isActive }) =>
              clsx(
                "flex h-9 items-center gap-3 rounded-lg text-[13px] font-medium transition-colors",
                collapsed ? "justify-center px-0" : "px-3",
                isActive
                  ? "bg-[var(--accent)]/12 text-[var(--accent)]"
                  : "text-[var(--text-muted)] hover:bg-[var(--hover-strong)] hover:text-[var(--text)]",
              )
            }
          >
            <Icon size={16} className="shrink-0" />
            {!collapsed && label}
          </NavLink>
        ))}
      </nav>

      <div className={clsx("mt-auto space-y-3 border-t border-[var(--border)]", collapsed ? "px-2 py-3" : "p-3")}>
        <div
          role="radiogroup"
          aria-label="Theme"
          className={clsx("grid gap-0.5 rounded-lg border border-[var(--border)] bg-[var(--input-bg)] p-0.5", collapsed ? "grid-cols-1" : "grid-cols-3")}
        >
          {themeOptions.map(({ value, label, icon: Icon }) => (
            <button
              key={value}
              role="radio"
              aria-checked={theme === value}
              title={`${label} theme`}
              onClick={() => setTheme(value)}
              className={clsx(
                "flex h-7 items-center justify-center gap-1 rounded-md text-[11px] font-medium transition-colors",
                theme === value ? "bg-[var(--surface-raised)] text-[var(--text)] ring-1 ring-[var(--border)]" : "text-[var(--text-dim)] hover:text-[var(--text)]",
              )}
            >
              <Icon size={12} />
              {!collapsed && label}
            </button>
          ))}
        </div>
        <div
          className={clsx("flex items-center gap-2 text-[11px] text-[var(--text-dim)]", collapsed ? "justify-center" : "px-1")}
          title={collapsed ? "Receiving telemetry" : undefined}
        >
          <span className="relative flex h-2 w-2">
            <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-[var(--success)] opacity-50" />
            <span className="relative inline-flex h-2 w-2 rounded-full bg-[var(--success)]" />
          </span>
          {!collapsed && "Receiving telemetry"}
        </div>
        {onToggle && (
          <button
            type="button"
            onClick={onToggle}
            aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
            title={collapsed ? "Expand sidebar" : "Collapse sidebar"}
            className={clsx(
              "flex h-8 w-full items-center gap-2 rounded-lg text-xs font-medium text-[var(--text-muted)] transition-colors hover:bg-[var(--hover-strong)] hover:text-[var(--text)]",
              collapsed ? "justify-center" : "px-2",
            )}
          >
            {collapsed ? <PanelLeftOpen size={15} /> : <PanelLeftClose size={15} />}
            {!collapsed && "Collapse"}
          </button>
        )}
      </div>
    </div>
  )
}

export function Layout({ children }: { children: ReactNode }) {
  const [drawerOpen, setDrawerOpen] = useState(false)
  // A per-viewer preference, so the sidebar stays the way it was left across reloads.
  const [collapsed, setCollapsed] = useLocalStorageState<boolean>("powertraces.sidebar.collapsed", false)

  return (
    <div className="flex h-screen bg-[var(--bg)] text-[var(--text)]">
      <aside
        className={clsx(
          "hidden shrink-0 border-r border-[var(--border)] bg-[var(--surface)] transition-[width] duration-200 lg:block",
          collapsed ? "w-16" : "w-60",
        )}
      >
        <Sidebar collapsed={collapsed} onToggle={() => setCollapsed(!collapsed)} />
      </aside>

      {drawerOpen && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <div className="absolute inset-0 bg-black/40" onClick={() => setDrawerOpen(false)} />
          <aside className="absolute inset-y-0 left-0 w-64 border-r border-[var(--border)] bg-[var(--surface)] shadow-[var(--shadow-md)]">
            <button
              onClick={() => setDrawerOpen(false)}
              aria-label="Close menu"
              className="absolute top-5 right-3 rounded-md p-1.5 text-[var(--text-muted)] hover:bg-[var(--hover-strong)]"
            >
              <X size={16} />
            </button>
            <Sidebar onNavigate={() => setDrawerOpen(false)} />
          </aside>
        </div>
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex items-center gap-3 border-b border-[var(--border)] bg-[var(--surface)] px-4 py-3 lg:hidden">
          <button onClick={() => setDrawerOpen(true)} aria-label="Open menu" className="rounded-md p-1.5 text-[var(--text-muted)] hover:bg-[var(--hover-strong)]">
            <Menu size={18} />
          </button>
          <span className="text-sm font-semibold">{window.__POWERTRACES_CONFIG__?.title ?? "PowerTraces"}</span>
        </div>
        <main className="min-h-0 flex-1 overflow-y-auto">
          <div className="mx-auto max-w-[1440px] px-4 py-6 sm:px-6 lg:px-8">{children}</div>
        </main>
      </div>
    </div>
  )
}
