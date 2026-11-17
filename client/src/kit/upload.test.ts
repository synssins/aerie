import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api";
import { makeHost } from "./host";

/** A stand-in for the browser's XMLHttpRequest: records what is sent, answers what the test says. */
class FakeXhr {
  static last: FakeXhr;
  static answer = { status: 200, text: "" };
  method = ""; url = ""; withCredentials = false; status = 0; statusText = ""; responseText = "";
  headers: Record<string, string> = {};
  body: unknown;
  upload: { onprogress: ((e: { loaded: number; total: number; lengthComputable: boolean }) => void) | null } = { onprogress: null };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  constructor() { FakeXhr.last = this; }
  open(method: string, url: string) { this.method = method; this.url = url; }
  setRequestHeader(k: string, v: string) { this.headers[k] = v; }
  send(body: unknown) {
    this.body = body;
    this.upload.onprogress?.({ loaded: 3, total: 6, lengthComputable: true });
    this.upload.onprogress?.({ loaded: 6, total: 6, lengthComputable: true });
    (this.status = FakeXhr.answer.status, this.responseText = FakeXhr.answer.text);
    this.onload?.();
  }
}

const me = { id: "u", userName: "a", displayName: "A", isAdmin: true, isBreakGlass: false, canAnnounce: false };
afterEach(() => vi.unstubAllGlobals());

describe("kb.api.upload", () => {
  it("posts the file as the raw body to the plugin's route, with its name and progress, and answers the JSON", async () => {
    vi.stubGlobal("XMLHttpRequest", FakeXhr);
    FakeXhr.answer = { status: 200, text: JSON.stringify({ id: "f1" }) };
    const kb = makeHost({ id: "kb.files", version: "1.0.0" }, me);
    const file = new File(["abcdef"], "shapes v2.bin");
    const seen: number[] = [];
    const r = await kb.api.upload<{ id: string }>("/libraries", file, { onProgress: (sent) => seen.push(sent) });
    expect(r).toEqual({ id: "f1" });
    expect([FakeXhr.last.method, FakeXhr.last.url, FakeXhr.last.withCredentials, FakeXhr.last.body]).toEqual(["POST", "/api/v1/plugins/kb.files/libraries", true, file]);
    expect(FakeXhr.last.headers).toEqual({ "Content-Type": "application/octet-stream", "X-File-Name": "shapes%20v2.bin" });
    expect(seen).toEqual([3, 6]);
  });

  it("throws the server's refusal", async () => {
    vi.stubGlobal("XMLHttpRequest", FakeXhr);
    FakeXhr.answer = { status: 413, text: JSON.stringify({ detail: "Too large." }) };
    const kb = makeHost({ id: "kb.files", version: "1.0.0" }, me);
    const e = await kb.api.upload("/libraries", new Blob(["x"]), { name: "a.bin" }).catch((x: ApiError) => x) as ApiError;
    expect(e).toBeInstanceOf(ApiError);
    expect([e.status, e.body]).toEqual([413, { detail: "Too large." }]);
  });
});
