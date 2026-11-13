// The sample plugin's script for the browser. A plain ES module: no build step. It gets React from the app.
// #region guide:ui
import { createElement as h, useEffect, useState } from "/shared/v1/react.js";

// The Acknowledge button tells the panel part to read again.
const listeners = new Set();
const changed = () => listeners.forEach((l) => l());

/** A part of the page's right panel: how many people acknowledged the page, and who, for those who may see that. */
function Acknowledged({ kb, page }) {
  const [count, setCount] = useState(null);
  const [people, setPeople] = useState(null);
  useEffect(() => {
    const load = () => {
      kb.api.get(`/pages/${page.id}/ack`).then(setCount).catch(() => setCount(null));
      // The plugin's route answers 403 to someone without the permission: they see the count and no names.
      kb.api.get(`/pages/${page.id}/acks`).then((r) => setPeople(r.people)).catch(() => setPeople(null));
    };
    load();
    listeners.add(load);
    return () => { listeners.delete(load); };
  }, [kb, page.id, page.version]);

  if (!count) return null;
  return h("div", null,
    h("div", null, count.count === 1 ? "1 person" : `${count.count} people`),
    (people ?? []).map((p) => h("div", { key: p.name, className: "muted small" }, `${p.name}, version ${p.version}`)));
}

/** Called once, after sign-in, with the host: everything the script may use of the app. */
export default function (kb) {
  kb.slots.add("page.panel", { id: "acknowledged", title: "Acknowledged", render: (c) => h(Acknowledged, { kb, page: c.page }) });
  kb.slots.add("page.tools", {
    id: "acknowledge",
    label: "Acknowledge",
    onClick: (c) => kb.api.post(`/pages/${c.page.id}/ack`).then(changed).catch(() => undefined),
  });
  addNotice(kb); // the editor block, below
}
// #endregion

// #region guide:editor
import { Node } from "/shared/v1/tiptap-react.js";

/** A block of the plugin's own: a line of text shown as a notice. Its type in the stored page is "sampleNotice". */
const Notice = Node.create({
  name: "sampleNotice",
  group: "block",
  content: "inline*",
  // Before the editor's own rule for <p>, or a copied notice would be pasted as a plain paragraph.
  parseHTML: () => [{ tag: "p[data-sample-notice]", priority: 60 }],
  renderHTML: ({ HTMLAttributes }) => ["p", { ...HTMLAttributes, "data-sample-notice": "", class: "sample-notice" }, 0],
});

function addNotice(kb) {
  // A stylesheet of the plugin's own ui folder, beside this script.
  const css = document.createElement("link");
  css.rel = "stylesheet";
  css.href = new URL("./notice.css", import.meta.url).href;
  document.head.append(css);

  kb.editor.extend(Notice);
  // The button turns the line the cursor is in into a notice, and back.
  kb.slots.add("editor.toolbar", { id: "notice", label: "Notice", onClick: (c) => c.editor.chain().focus().toggleNode("sampleNotice", "paragraph").run() });
}
// #endregion
