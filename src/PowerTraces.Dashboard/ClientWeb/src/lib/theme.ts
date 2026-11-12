import { useEffect } from "react"
import { useLocalStorageState } from "@/lib/useLocalStorageState"

export type ThemeChoice = "system" | "light" | "dark"

/** The viewer's theme choice, persisted per browser. "system" follows the OS setting (the CSS
 * media query in index.css); "light"/"dark" pin it via data-theme on <html>, which the stylesheet
 * lets win over the OS either way. index.html applies the saved choice before first paint. */
export function useTheme() {
  const [theme, setTheme] = useLocalStorageState<ThemeChoice>("powertraces.theme", "system")

  useEffect(() => {
    const root = document.documentElement
    if (theme === "system") delete root.dataset.theme
    else root.dataset.theme = theme
  }, [theme])

  return [theme, setTheme] as const
}
