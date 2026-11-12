/** Copies text to the clipboard. `navigator.clipboard` only exists in secure contexts (HTTPS or
 * localhost) — a dashboard served over plain HTTP on an internal host doesn't have it — so fall
 * back to the legacy hidden-textarea + execCommand route there. */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // Permission denied etc. — try the fallback below.
  }

  const textarea = document.createElement("textarea")
  textarea.value = text
  textarea.setAttribute("readonly", "")
  textarea.style.position = "fixed"
  textarea.style.opacity = "0"
  document.body.appendChild(textarea)
  textarea.select()
  try {
    return document.execCommand("copy")
  } catch {
    return false
  } finally {
    document.body.removeChild(textarea)
  }
}
