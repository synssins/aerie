import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DialogProvider } from "../Dialogs";
import { AiPanel, type AiSettings } from "./AiSettings";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };
const DEFAULT = "THE HARD DEFAULT PROMPT";

const settings: AiSettings = {
  enabled: false, baseUrl: "", model: "", timeoutSeconds: 120, maxTokens: 4096, temperature: 0, reasoning: false, ruleset: "Do only the task, custom.",
  maxInputChars: 200000, userPerMinute: 10, tokenPerMinute: 10, features: { import: "Off", format: "Off", search: "Off" },
};

function server(apiKeySet = false, saved: Partial<AiSettings> = {}, modelsDown = false) {
  const calls: { key: string; body: unknown }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? "GET"} ${url.replace("/api/v1", "")}`;
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    calls.push({ key, body });
    if (key === "GET /admin/ai") return new Response(JSON.stringify({ settings: { ...settings, ...saved }, apiKeySet, rulesetIsDefault: false, defaultRuleset: DEFAULT }), { status: 200 });
    if (key === "PUT /admin/ai") { const s = (body as { settings: AiSettings }).settings; return new Response(JSON.stringify({ settings: s, apiKeySet: true, rulesetIsDefault: s.ruleset === DEFAULT, defaultRuleset: DEFAULT }), { status: 200 }); }
    if (key === "POST /admin/ai/models") return modelsDown
      ? new Response(JSON.stringify({ status: 502, detail: "The server answered 503 at /v1/models." }), { status: 502, headers: { "Content-Type": "application/problem+json" } })
      : new Response(JSON.stringify(["phi-4b", "qwen3"]), { status: 200 });
    if (key === "POST /admin/ai/test") return new Response(JSON.stringify({ ok: true, model: "qwen3", milliseconds: 812, reasoningEmpty: true, error: null }), { status: 200 });
    return new Response(null, { status: 404 });
  }));
  return calls;
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe("AI settings", () => {
  it("the prompt is editable, says Default or Customized, and Restore to default asks, then saves the default", async () => {
    const calls = server();
    render(<DialogProvider><AiPanel /></DialogProvider>);
    expect((await screen.findByLabelText("Prompt state")).textContent).toBe("Customized");
    await userEvent.click(screen.getByRole("button", { name: "Restore to default" }));
    expect(calls.some((c) => c.key === "PUT /admin/ai")).toBe(false); // asked first
    await userEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Restore to default" }));
    await waitFor(() => expect(calls.find((c) => c.key === "PUT /admin/ai")?.body).toMatchObject({ settings: { ruleset: DEFAULT } }));
    expect((await screen.findByLabelText("Prompt state")).textContent).toBe("Default");
    expect((screen.getByLabelText("Prompt") as HTMLTextAreaElement).value).toBe(DEFAULT);
  });

  it("starts with reasoning off, saves the settings and a new key, and never shows a stored key", async () => {
    const calls = server(true);
    render(<DialogProvider><AiPanel /></DialogProvider>);
    const reasoning = await screen.findByRole("switch", { name: "Reasoning" });
    expect(reasoning.getAttribute("aria-checked")).toBe("false");
    expect(screen.getByText("Set")).toBeTruthy();
    expect((screen.getByLabelText("API key") as HTMLInputElement).value).toBe("");

    await userEvent.click(screen.getByRole("switch", { name: "AI" }));
    await userEvent.type(screen.getByLabelText("Server address"), "http://192.168.1.86:8093");
    await userEvent.tab(); // leaving the address lists its models
    await waitFor(() => expect(calls.find((c) => c.key === "POST /admin/ai/models")?.body).toEqual({ baseUrl: "http://192.168.1.86:8093", apiKey: null }));
    await userEvent.selectOptions(await screen.findByLabelText("Model"), await screen.findByRole("option", { name: "qwen3" }));
    await userEvent.click(within(screen.getByRole("radiogroup", { name: "Import" })).getByRole("radio", { name: "Everyone with access" }));
    await userEvent.type(screen.getByLabelText("API key"), "sk-1");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(calls.find((c) => c.key === "PUT /admin/ai")?.body).toMatchObject({
      apiKey: "sk-1", settings: { enabled: true, baseUrl: "http://192.168.1.86:8093", model: "qwen3", reasoning: false, features: { import: "Everyone" } },
    }));
  });

  it("Test connection shows the model, the time and whether reasoning came back", async () => {
    server();
    render(<DialogProvider><AiPanel /></DialogProvider>);
    await userEvent.click(await screen.findByRole("button", { name: "Test connection" }));
    expect((await screen.findByRole("alert")).textContent).toBe("Connected · qwen3 · 812 ms · reasoning empty");
  });

  it("the saved model stays chosen when the server does not list it, and a failed list says why", async () => {
    const calls = server(false, { baseUrl: "http://lan:8093", model: "old-9b" });
    render(<DialogProvider><AiPanel /></DialogProvider>);
    expect(await screen.findByRole("option", { name: "old-9b (not currently listed)" })).toBeTruthy();
    expect((screen.getByLabelText("Model") as HTMLSelectElement).value).toBe("old-9b");
    expect(screen.getByRole("option", { name: "phi-4b" })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(calls.filter((c) => c.key === "POST /admin/ai/models")).toHaveLength(2));
    cleanup();
    server(false, { baseUrl: "http://lan:8093", model: "old-9b" }, true);
    render(<DialogProvider><AiPanel /></DialogProvider>);
    expect((await screen.findByRole("alert")).textContent).toBe("Models could not be listed: The server answered 503 at /v1/models.");
    expect((screen.getByLabelText("Model") as HTMLSelectElement).value).toBe("old-9b");
  });
});
