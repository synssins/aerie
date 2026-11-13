import { createElement as h, useState } from "/shared/v1/react.js";

function Counter() {
  const [n, setN] = useState(0);
  return h("button", { type: "button", className: "tb", onClick: () => setN(n + 1) }, `Probe clicks: ${n}`);
}

export default function (kb) {
  kb.slots.add("page.panel", { id: "probe.counter", title: "Probe", render: () => h(Counter) });
  kb.slots.add("page.tools", { id: "probe.hello", label: "Probe", onClick: (c) => kb.navigate(`/x/${kb.plugin.id}/page/${c.page.id}`) });
  kb.screens.add({ path: "", title: "Probe", render: (rest) => h("p", null, `Probe screen: ${rest}`) });
}
