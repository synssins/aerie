/**
 * A plugin's script failed. It is shown in the browser's console and sent to the server's log; the app carries on without that part.
 * Nothing is sent for the core's own code.
 */
export function reportUiError(plugin: string, where: string, error: unknown): void {
  const message = error instanceof Error ? error.message : String(error);
  console.error(`Plugin ${plugin} failed at ${where}:`, error);
  if (plugin === "core") return;
  void fetch("/api/v1/ui/errors", {
    method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ plugin, where, message: message.slice(0, 500) }),
  }).catch(() => undefined);
}
