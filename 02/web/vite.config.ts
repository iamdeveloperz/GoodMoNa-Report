import { defineConfig } from "vite";
import { resolve } from "node:path";

const server = "http://localhost:5012"; // 02 개발 서버 (운영 서비스는 5002)

export default defineConfig({
  build: {
    // ASP.NET Core가 그대로 서빙합니다. 페이지를 추가하면 input에 넣고, 서버 Program.cs에 MapFallbackToFile을 추가하세요.
    outDir: resolve(import.meta.dirname, "../src/GoodMoNa02.Server/wwwroot"),
    emptyOutDir: true,
    // 01도 /assets/를 쓰므로, 같은 주소(goodmona.hsdevs.com)에서 경로로 나눌 수 있게 이름을 다르게 둡니다.
    assetsDir: "intranet-assets",
    rollupOptions: {
      input: {
        login: resolve(import.meta.dirname, "login.html"),
        signup: resolve(import.meta.dirname, "signup.html"),
        intranet: resolve(import.meta.dirname, "intranet.html"),
        "intranet-admin": resolve(import.meta.dirname, "intranet-admin.html"),
      },
    },
  },
  server: {
    port: 5174, // 01 화면 개발 서버(5173)와 동시에 띄울 수 있게
    proxy: {
      "/api": server,
      "/.well-known": server,
    },
  },
});
