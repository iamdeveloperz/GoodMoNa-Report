import { defineConfig, type Plugin } from "vite";
import { resolve } from "node:path";

const server = "http://localhost:5001";

/** 개발 서버에서도 /capture/{token} 주소가 촬영 페이지를 열도록 합니다. (운영에서는 ASP.NET이 처리) */
const captureRoute: Plugin = {
  name: "capture-route",
  configureServer(s) {
    s.middlewares.use((req, _res, next) => {
      if (req.url?.startsWith("/capture/")) req.url = "/capture.html";
      next();
    });
  },
};

export default defineConfig({
  plugins: [captureRoute],
  build: {
    // ASP.NET Core가 그대로 서빙합니다.
    outDir: resolve(import.meta.dirname, "../src/GoodMoNa.Server/wwwroot"),
    emptyOutDir: true,
    rollupOptions: {
      input: {
        report: resolve(import.meta.dirname, "index.html"),
        capture: resolve(import.meta.dirname, "capture.html"),
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": server,
      "/hubs": { target: server, ws: true },
    },
  },
});
