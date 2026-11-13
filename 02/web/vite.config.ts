import { defineConfig } from "vite";
import { resolve } from "node:path";

const server = "http://localhost:5012"; // 02 개발 서버 (운영 서비스는 5002)

export default defineConfig({
  build: {
    // ASP.NET Core가 그대로 서빙합니다. 페이지를 추가하면 input에 넣고, 서버 Program.cs에 MapFallbackToFile을 추가하세요.
    outDir: resolve(import.meta.dirname, "../src/GoodMoNa02.Server/wwwroot"),
    emptyOutDir: true,
    rollupOptions: {
      input: {
        index: resolve(import.meta.dirname, "index.html"),
      },
    },
  },
  server: {
    port: 5174, // 01 화면 개발 서버(5173)와 동시에 띄울 수 있게
    proxy: {
      "/api": server,
      "/hubs": { target: server, ws: true },
    },
  },
});
