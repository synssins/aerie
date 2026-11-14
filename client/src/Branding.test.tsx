import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import type { Brand } from "./api";
import { BrandLogo, DEFAULT_LOGO } from "./Branding";

const brand = (b: Partial<Brand>): Brand => ({ productName: "KB", logoLightUrl: null, logoDarkUrl: null, primaryColor: "#1f5fbf", ...b });

afterEach(cleanup);

describe("BrandLogo", () => {
  it("shows Aerie's icon until a logo is uploaded", () => {
    render(<BrandLogo brand={brand({})} background="dark" />);
    expect(screen.getByRole("img").getAttribute("src")).toBe(DEFAULT_LOGO);
  });

  it("shows the uploaded logo for its background", () => {
    render(<BrandLogo brand={brand({ logoLightUrl: "/l", logoDarkUrl: "/d" })} background="dark" />);
    expect(screen.getByRole("img").getAttribute("src")).toBe("/d");
  });
});
