import { afterEach, describe, expect, it } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { DialogProvider, useDialogs } from "./Dialogs";

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute("open", ""); };
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute("open"); };

afterEach(cleanup);

describe("DialogProvider", () => {
  it("a question that another question replaces is answered as cancelled, so nobody waits for it for good", async () => {
    let dialogs!: ReturnType<typeof useDialogs>;
    function Grab() { dialogs = useDialogs(); return null; }
    render(<DialogProvider><Grab /></DialogProvider>);

    let first: boolean | undefined, asked: string | null | undefined;
    act(() => { void dialogs.confirm({ title: "Discard unsaved changes?" }).then((v) => { first = v; }); });
    expect(screen.getByText("Discard unsaved changes?")).toBeTruthy();
    act(() => { void dialogs.ask({ title: "Name", label: "Name" }).then((v) => { asked = v; }); });
    await act(async () => { await Promise.resolve(); });

    expect(first).toBe(false);
    expect(screen.queryByText("Discard unsaved changes?")).toBeNull(); // the second question is the one shown

    act(() => { void dialogs.confirm({ title: "Second?" }).then(() => {}); });
    await act(async () => { await Promise.resolve(); });
    expect(asked).toBeNull();
  });
});
