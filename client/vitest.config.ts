import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";

// The app's own copy of a package, by its file: a script outside this folder (a plugin's) has no node_modules of its own to find it in.
const own = (library: string) => fileURLToPath(import.meta.resolve(library));

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: [
      // A plugin's script takes React and the editor from these addresses; in a test they are the app's own packages, as in the browser.
      { find: /^\/shared\/v1\/react\.js$/, replacement: own("react") },
      { find: /^\/shared\/v1\/react-jsx-runtime\.js$/, replacement: own("react/jsx-runtime") },
      { find: /^\/shared\/v1\/react-dom\.js$/, replacement: own("react-dom") },
      { find: /^\/shared\/v1\/tiptap-react\.js$/, replacement: own("@tiptap/react") },
      // The sample plugin's script as it is shipped, for the test that runs it.
      { find: "kb-sample-ui", replacement: fileURLToPath(new URL("../plugins/Kb.Plugin.Sample/ui/index.js", import.meta.url)) },
    ],
  },
  server: { fs: { allow: [".."] } },
  test: { environment: "jsdom", include: ["src/**/*.test.{ts,tsx}"] },
});
