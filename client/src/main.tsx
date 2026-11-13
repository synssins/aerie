import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { DialogProvider } from "./Dialogs";
import "./kit/shared";
import "./theme.css";

// Sign-in and setup pages follow the theme last used in this browser.
try { document.documentElement.dataset.theme = localStorage.getItem("kb.theme") ?? "light"; } catch { /* storage blocked */ }

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <DialogProvider>
      <App />
    </DialogProvider>
  </StrictMode>,
);
