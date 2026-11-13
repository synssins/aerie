import { Editor, flattenExtensions, getSchema, type AnyExtension, type JSONContent } from "@tiptap/react";
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

type Added = { source: string; extensions: AnyExtension[] }; // as passed in one call: a set may name each other in any order
const added: Added[] = [];
const listeners = new Set<() => void>();
let version = 0;

/** What plugins add to the editor: nodes, marks, paste and drop handlers. Every editor made afterwards has them. */
export const editorExtensions = {
  add(source: string, ...extensions: AnyExtension[]): void {
    added.push({ source, extensions });
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
 * The plugin extensions the editor can be made with. One that takes a name of the editor's own, or with which no editor can be
 * made, is left out and reported: a plugin's mistake must not take the editor away. The check makes an editor and throws it
 * away, because an extension can have a sound schema and still fail when the editor starts.
 */
function pluginExtensions(): AnyExtension[] {
  if (checked?.version === version) return checked.ok;
  const base = core("plaintext");
  const taken = new Set(flattenExtensions(base).map((e) => e.name));
  const ok: AnyExtension[] = [];
  const fits = (set: AnyExtension[]): void => {
    const names = flattenExtensions(set).map((e) => e.name);
    const clash = names.find((n, i) => taken.has(n) || names.indexOf(n) !== i);
    if (clash) throw new Error(`${clash} is a part of the editor.`);
    getSchema([...base, ...ok, ...set]);
    new Editor({ extensions: [...base, ...ok, ...set], content: "" }).destroy();
    ok.push(...set);
    names.forEach((n) => taken.add(n));
  };
  for (const { source, extensions } of added) {
    try { fits(extensions); }
    catch (whole) {
      // Not as a set: then one by one, so the sound ones of a call are kept. A single extension's error is the set's error.
      if (extensions.length === 1) { reportUiError(source, "editor", whole); continue; }
      for (const extension of extensions) {
        try { fits([extension]); } catch (e) { reportUiError(source, "editor", e); }
      }
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

/** A stored document, as an editor made with these extensions can hold it. Whatever shape it has, this does not throw: a page must open. */
export const toEditor = (json: string, extensions: AnyExtension[]): JSONContent => wrapUnknown(JSON.parse(json) as JSONContent, schemaOf(extensions));

/** The editor's document, as it is stored. */
export const fromEditor = (editor: Editor): string => JSON.stringify(unwrapUnknown(editor.getJSON()));
