import { afterEach, describe, expect, it } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { PluginScreen } from "./PluginScreen";
import { screens } from "./screens";
import { slots } from "./slots";

afterEach(() => { cleanup(); screens.clear(); slots.clear(); });

describe("PluginScreen", () => {
  it("renders a plugin's screen at its path, with its title and its panel", () => {
    screens.add("kb.sample", { path: "reports", title: "Reports", render: (rest) => <p>Report {rest}</p>, panel: "sample.reports.panel" });
    slots.add("sample.reports.panel", { id: "about", title: "About", render: () => <p>from another plugin</p> }, "kb.other");

    render(<PluginScreen at={{ id: "kb.sample", rest: "reports/2026" }} onHome={() => {}} />);

    expect(screen.getByRole("heading", { level: 1, name: "Reports" })).toBeTruthy();
    expect(screen.getByText("Report 2026")).toBeTruthy();
    expect(screen.getByText("from another plugin")).toBeTruthy(); // the screen's own slot takes another plugin's item
  });

  it("an unknown plugin screen shows Not found, and appears when its plugin registers it", () => {
    render(<PluginScreen at={{ id: "kb.sample", rest: "reports" }} onHome={() => {}} />);
    expect(screen.getByText("Not found.")).toBeTruthy();

    act(() => screens.add("kb.sample", { path: "", title: "Sample", render: (rest) => <p>All of {rest}</p> }));

    expect(screen.getByText("All of reports")).toBeTruthy();
  });

  it("the longest matching path wins, and another plugin's screens are not this one's", () => {
    screens.add("kb.sample", { path: "", title: "Sample", render: () => <p>root</p> });
    screens.add("kb.sample", { path: "reports", title: "Reports", render: () => <p>reports</p> });
    screens.add("kb.other", { path: "reports/deep", title: "Other", render: () => <p>other</p> });

    render(<PluginScreen at={{ id: "kb.sample", rest: "reports/deep" }} onHome={() => {}} />);

    expect(screen.getByText("reports")).toBeTruthy();
  });
});
