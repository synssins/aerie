import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Builds into the web app's wwwroot so ASP.NET Core serves it. Dev server proxies the API.
export default defineConfig({
  plugins: [react()],
  build: { outDir: "../src/Kb.Web/wwwroot", emptyOutDir: true },
  server: { proxy: { "/api": { target: "https://localhost:7180", secure: false } } },
});
