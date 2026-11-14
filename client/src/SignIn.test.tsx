import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import type { Brand } from "./api";
import { SignIn } from "./SignIn";

const brand: Brand = { productName: "KB", logoLightUrl: null, logoDarkUrl: null, primaryColor: "#1b4f8a" };

afterEach(cleanup);

describe("SignIn", () => {
  it("shows the brand's sign-in note when there is one", () => {
    render(<SignIn brand={{ ...brand, signInNote: "Read-Only Preview: demo/demo" }} onSignedIn={() => {}} />);
    expect(screen.getByText("Read-Only Preview: demo/demo")).toBeTruthy();
  });

  it("shows no note when the brand has none", () => {
    const { container } = render(<SignIn brand={brand} onSignedIn={() => {}} />);
    expect(container.querySelector(".muted")).toBeNull();
  });
});
