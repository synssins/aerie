import { createElement as h, useState } from "/shared/v1/react.js";
import { Node } from "/shared/v1/tiptap-react.js";

function Counter() {
  const [n, setN] = useState(0);
  return h("button", { type: "button", className: "tb", onClick: () => setN(n + 1) }, `Probe clicks: ${n}`);
}

// An editor node of the probe's own: a box with a label that holds other blocks. The server reads it through the probe's renderer.
const ProbeBox = Node.create({
  name: "probeBox",
  group: "block",
  content: "block+",
  addAttributes: () => ({ label: { default: "Box", parseHTML: (el) => el.getAttribute("data-label"), renderHTML: (a) => ({ "data-label": a.label }) } }),
  parseHTML: () => [{ tag: "div[data-probe-box]" }],
  renderHTML: ({ HTMLAttributes }) => ["div", { ...HTMLAttributes, "data-probe-box": "", class: "note-row" }, 0],
});

export default function (kb) {
  kb.slots.add("page.panel", { id: "probe.counter", title: "Probe", render: () => h(Counter) });
  kb.slots.add("page.tools", { id: "probe.hello", label: "Probe", onClick: (c) => kb.navigate(`/x/${kb.plugin.id}/page/${c.page.id}`) });
  kb.screens.add({ path: "", title: "Probe", render: (rest) => h("p", null, `Probe screen: ${rest}`) });
  kb.editor.extend(ProbeBox);
  kb.slots.add("editor.toolbar", {
    id: "probe.box",
    label: "Box",
    onClick: (c) => c.editor.chain().focus().insertContent({ type: "probeBox", attrs: { label: "Box" }, content: [{ type: "paragraph" }] }).run(),
  });
}
