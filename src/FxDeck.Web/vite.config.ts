import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";

/**
 * @mdi/font ships one @font-face with eot/woff2/woff/ttf sources; keep only woff2 so a single
 * ~400 KB file is embedded in the exe instead of ~3.5 MB.
 */
function mdiWoff2Only(): Plugin {
  return {
    name: "fxdeck-mdi-woff2-only",
    enforce: "pre", // must run before vite:css rewrites the url() references
    transform(code, id) {
      if (!id.includes("materialdesignicons") || !id.endsWith(".css")) return null;
      const woff2 = code.match(/url\(([^)]*\.woff2[^)]*)\)\s*format\("woff2"\)/);
      if (!woff2) return null;
      const face = code.replace(/src:\s*url\([^;]*\.eot[^;]*;/, "").replace(/src:\s*url\([^;]*;/, `src: url(${woff2[1]}) format("woff2");`);
      return { code: face, map: null };
    },
  };
}

export default defineConfig({
  plugins: [react(), mdiWoff2Only()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: "http://127.0.0.1:20200", ws: true },
    },
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1500,
  },
});
