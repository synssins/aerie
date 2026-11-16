import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { aiChanged, useAiFeatures } from "./aiFeatures";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function Probe({ signedIn }: { signedIn: boolean }) {
  const ai = useAiFeatures(signedIn);
  return <p>{`${ai.canImport} ${ai.canFormat} ${ai.canAssist}`}</p>;
}

describe("AI feature flags", () => {
  it("are read when signed in, and read again when the AI settings change, so a feature turned on shows without reloading", async () => {
    let assist = false;
    const fetch = vi.fn(async () => new Response(JSON.stringify({ canImport: false, canFormat: true, canAssist: assist }), { status: 200 }));
    vi.stubGlobal("fetch", fetch);
    render(<Probe signedIn />);
    await waitFor(() => expect(screen.getByText("false true false")).toBeTruthy());
    assist = true;
    act(() => aiChanged());
    await waitFor(() => expect(screen.getByText("false true true")).toBeTruthy());
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("are not read when signed out", () => {
    const fetch = vi.fn();
    vi.stubGlobal("fetch", fetch);
    render(<Probe signedIn={false} />);
    act(() => aiChanged());
    expect(fetch).not.toHaveBeenCalled();
  });
});
