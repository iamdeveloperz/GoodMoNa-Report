import { defineConfig, type Plugin } from "vite";
import { resolve } from "node:path";

const server = "http://localhost:5011"; // 개발 서버 (운영 서비스는 5001)

/** 개발 서버에서도 /report · /director · /capture/{token} 주소가 각 페이지를 열도록 합니다. (운영에서는 ASP.NET이 처리) */
const pageRoutes: Plugin = {
  name: "page-routes",
  configureServer(s) {
    s.middlewares.use((req, _res, next) => {
      const path = req.url?.split("?")[0];
      if (path?.startsWith("/capture/")) req.url = "/capture.html";
      else if (path === "/report") req.url = "/report.html";
      else if (path === "/director") req.url = "/director.html";
      next();
    });
  },
};

export default defineConfig({
  plugins: [pageRoutes],
  build: {
    // ASP.NET Core가 그대로 서빙합니다.
    outDir: resolve(import.meta.dirname, "../src/GoodMoNa.Server/wwwroot"),
    emptyOutDir: true,
    rollupOptions: {
      input: {
        index: resolve(import.meta.dirname, "index.html"),
        report: resolve(import.meta.dirname, "report.html"),
        director: resolve(import.meta.dirname, "director.html"),
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
