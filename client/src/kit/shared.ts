import * as React from "react";
import * as jsxRuntime from "react/jsx-runtime";
import * as ReactDOM from "react-dom";

/**
 * The app's own copies of the libraries a plugin's script shares with it. A script imports them from /shared/v1/<name>.js,
 * small modules (written at build time by tools/gen-shared.mjs) that hand back these objects. So there is one React on the page:
 * a plugin that bundled its own would break hooks.
 */
export const SHARED = { "react": React, "react/jsx-runtime": jsxRuntime, "react-dom": ReactDOM };

(window as unknown as { __kbShared: typeof SHARED }).__kbShared = SHARED;
