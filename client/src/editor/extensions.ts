import { flattenExtensions, getSchema, type AnyExtension, type Editor, type JSONContent } from "@tiptap/react";
import StarterKit from "@tiptap/starter-kit";
import { TaskItem, TaskList } from "@tiptap/extension-list";
import { TableKit } from "@tiptap/extension-table";
import CodeBlockLowlight from "@tiptap/extension-code-block-lowlight";
import Image from "@tiptap/extension-image";
import type { Schema } from "@tiptap/pm/model";
import { common, createLowlight } from "lowlight";
import powershell from "highlight.js/lib/languages/powershell";
import { reportUiError } from "../kit/errors";
import { unknownExtensions, unwrapUnknown, wrapUnknown } from "./unknown";

const lowlight = createLowlight(common);
lowlight.register("powershell", powershell);

/** The editor's own parts. */
const core = (codeLanguage: string): AnyExtension[] => [
  StarterKit.configure({
    codeBlock: false,
    heading: { levels: [2, 3, 4] },
    link: { openOnClick: false, autolink: true, protocols: ["http", "https", "mailto"], defaultProtocol: "https" },
  }),
  TaskList,
  TaskItem.configure({ nested: true }),
  TableKit.configure({ table: { resizable: true } }),
  CodeBlockLowlight.configure({ lowlight, defaultLanguage: codeLanguage }),
  Image.configure({ allowBase64: false, resize: { enabled: true, directions: ["bottom-right"], minWidth: 48, minHeight: 48, alwaysPreserveAspectRatio: true } }),
  ...unknownExtensions,
];

type Added = { source: string; extension: AnyExtension };
const added: Added[] = [];
const listeners = new Set<() => void>();
let version = 0;

/** What plugins add to the editor: nodes, marks, paste and drop handlers. Every editor made afterwards has them. */
export const editorExtensions = {
  add(source: string, ...extensions: AnyExtension[]): void {
    for (const extension of extensions) added.push({ source, extension });
    version++;
    listeners.forEach((l) => l());
  },
  /** Changes when a plugin adds an extension; a page that is not being edited makes its editor again. */
  version: (): number => version,
  subscribe(fn: () => void): () => void { listeners.add(fn); return () => listeners.delete(fn); },
  /** For tests. */
  clear(): void { added.length = 0; version++; listeners.forEach((l) => l()); },
};

let checked: { version: number; ok: AnyExtension[] } | null = null;

/**
 * The plugin extensions the editor can be made with. One that takes a name of the editor's own, or with which no schema can be
 * built, is left out and reported: a plugin's mistake must not take the editor away.
 */
function pluginExtensions(): AnyExtension[] {
  if (checked?.version === version) return checked.ok;
  const base = core("plaintext");
  const taken = new Set(flattenExtensions(base).map((e) => e.name));
  const ok: AnyExtension[] = [];
  for (const { source, extension } of added) {
    try {
      const names = flattenExtensions([extension]).map((e) => e.name);
      const clash = names.find((n) => taken.has(n));
      if (clash) throw new Error(`${clash} is a part of the editor.`);
      getSchema([...base, ...ok, extension]);
      ok.push(extension);
      names.forEach((n) => taken.add(n));
    } catch (e) {
      reportUiError(source, "editor", e);
    }
  }
  checked = { version, ok };
  return ok;
}

export const makeExtensions = (codeLanguage: string): AnyExtension[] => [...core(codeLanguage), ...pluginExtensions()];

const schemas = new WeakMap<AnyExtension[], Schema>();
export function schemaOf(extensions: AnyExtension[]): Schema {
  let schema = schemas.get(extensions);
  if (!schema) schemas.set(extensions, schema = getSchema(extensions));
  return schema;
}

/** A stored document, as an editor made with these extensions can hold it. */
export const toEditor = (json: string, extensions: AnyExtension[]): JSONContent => wrapUnknown(JSON.parse(json) as JSONContent, schemaOf(extensions));

/** The editor's document, as it is stored. */
export const fromEditor = (editor: Editor): string => JSON.stringify(unwrapUnknown(editor.getJSON()));
