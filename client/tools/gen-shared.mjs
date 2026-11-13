// Writes one small ES module per shared library into the built app (wwwroot/shared/v1/). Each re-exports every name of its
// library from window.__kbShared, which the app fills with its own copies (src/kit/shared.ts). A plugin's script imports
// "/shared/v1/react.js" and gets the app's React. Run after "vite build"; the names are read from the installed packages.
import { mkdirSync, realpathSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// "@tiptap/react" is the editor: it also has everything of @tiptap/core (Node, Mark, Extension, mergeAttributes).
export const LIBRARIES = ["react", "react/jsx-runtime", "react-dom", "@tiptap/react"];
/** "react/jsx-runtime" -> "react-jsx-runtime.js". */
export const fileOf = (library) => library.replace(/^@/, "").replace(/[/@]/g, "-") + ".js";

export async function moduleFor(library) {
  const names = Object.keys(await import(library)).filter((n) => n !== "default" && /^[A-Za-z_$][\w$]*$/.test(n)).sort();
  return [
    `// The app's own ${library}. Written by tools/gen-shared.mjs; do not edit.`,
    `const m = window.__kbShared[${JSON.stringify(library)}];`,
    "export default m.default ?? m;",
    ...names.map((n) => `export const ${n} = m.${n};`),
    "",
  ].join("\n");
}

export async function generate(outDir) {
  mkdirSync(outDir, { recursive: true });
  for (const library of LIBRARIES) writeFileSync(join(outDir, fileOf(library)), await moduleFor(library));
}

// Run as a script (not imported by a test). The path is compared as the file system has it: the folder may be reached through a link.
if (process.argv[1] && realpathSync(fileURLToPath(import.meta.url)) === realpathSync(process.argv[1])) {
  const out = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "src", "Kb.Web", "wwwroot", "shared", "v1");
  await generate(out);
  console.log(`shared modules written to ${out}`);
}
