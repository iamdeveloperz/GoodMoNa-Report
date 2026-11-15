import { defineConfig, type Plugin } from "vite";
import { resolve } from "node:path";

const server = "http://localhost:5013"; // 03 개발 서버 (운영 서비스는 5003)
const intranet = "http://localhost:5012"; // 02 인트라넷 개발 서버: 로그인·출입증

/** 개발 서버에서도 /shorts · /shorts/review 주소가 각 페이지를 열도록 합니다. (운영에서는 ASP.NET이 처리) */
const pageRoutes: Plugin = {
  name: "page-routes",
  configureServer(s) {
    s.middlewares.use((req, _res, next) => {
      const path = req.url?.split("?")[0];
      const query = req.url?.includes("?") ? req.url.slice(req.url.indexOf("?")) : "";
      if (path === "/" || path === "/shorts") req.url = "/shorts.html" + query;
      else if (path === "/shorts/review") req.url = "/review.html" + query;
      next();
    });
  },
};

export default defineConfig({
  plugins: [pageRoutes],
  build: {
    outDir: resolve(import.meta.dirname, "../src/GoodMoNa03.Server/wwwroot"),
    emptyOutDir: true,
    // 운영에서는 같은 주소를 터널이 경로로 나눕니다. /assets는 01로 가므로 03 화면 파일은 /shorts-assets 아래에 둡니다(02의 intranet-assets와 같은 이유).
    assetsDir: "shorts-assets",
    rollupOptions: {
      input: {
        shorts: resolve(import.meta.dirname, "shorts.html"),
        review: resolve(import.meta.dirname, "review.html"),
      },
    },
  },
  server: {
    port: 5175,
    proxy: {
      // 02로 가는 경로를 먼저 (운영의 Cloudflare 경로 규칙과 같게)
      "/api/auth": intranet,
      "/api/admin": intranet,
      "/api/intranet": intranet,
      "/.well-known": intranet,
      "/login": intranet,
      "/signup": intranet,
      "/intranet": intranet,
      "/api": server,
    },
  },
});
