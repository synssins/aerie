import { describe, expect, it } from "vitest";
import * as React from "react";
import * as jsxRuntime from "react/jsx-runtime";
import * as ReactDOM from "react-dom";
import * as tiptap from "@tiptap/react";
import { fileOf, LIBRARIES, moduleFor } from "../../tools/gen-shared.mjs";
import { SHARED } from "./shared";

describe("shared modules", () => {
  it("the app shares exactly the libraries the generator writes modules for", () => {
    expect(Object.keys(SHARED).sort()).toEqual([...LIBRARIES].sort());
    expect((window as unknown as { __kbShared: unknown }).__kbShared).toBe(SHARED);
  });

  it("each module exports every name of its library, taken from the app's copy", async () => {
    for (const [library, live] of [["react", React], ["react/jsx-runtime", jsxRuntime], ["react-dom", ReactDOM], ["@tiptap/react", tiptap]] as const) {
      const text = await moduleFor(library);
      expect(text).toContain(`const m = window.__kbShared[${JSON.stringify(library)}];`);
      // "module.exports" is the test runner's own key for a CommonJS library, not a name of the library.
      for (const name of Object.keys(live).filter((n) => n !== "default" && /^[A-Za-z_$][\w$]*$/.test(n))) expect(text).toContain(`export const ${name} = m.${name};`);
    }
  });

  it("names a module's file after its library", () => {
    expect(LIBRARIES.map(fileOf)).toEqual(["react.js", "react-jsx-runtime.js", "react-dom.js", "tiptap-react.js"]);
  });
});
