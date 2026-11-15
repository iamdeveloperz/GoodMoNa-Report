import "../shared/base.css";
import "../shared/intranet.css";
import QRCode from "qrcode";
import { api, errorMessage } from "../shared/api";
import { byId, pad } from "../shared/format";

/**
 * 병원 입구에 둔 태블릿·모니터용 출퇴근 QR 화면 (대표원장·관리자 계정으로 열어 둠).
 * 서버가 30초마다 바뀌는 코드를 주고, 직원은 폰으로 찍어 /intranet/attendance?code=… 에서 출근·퇴근을 누릅니다.
 * 화면이 꺼지지 않게 Wake Lock을 요청합니다(지원하는 브라우저에서).
 */

interface KioskCode {
  code: string;
  url: string;
  expiresAt: string;
  stepSeconds: number;
}

let current: KioskCode | null = null;
let timer: number | undefined;

async function refresh(): Promise<void> {
  window.clearTimeout(timer);
  try {
    current = await api<KioskCode>("GET", "/api/intranet/attendance/kiosk");
    await QRCode.toCanvas(byId<HTMLCanvasElement>("qr"), current.url, { width: 480, margin: 2, errorCorrectionLevel: "M", color: { dark: "#0e0e0e", light: "#ffffff" } });
    byId("status").textContent = "";
    // 코드가 바뀌기 1초 전에 다음 코드를 받습니다.
    const wait = Math.max(1000, Date.parse(current.expiresAt) - Date.now() + 500);
    timer = window.setTimeout(refresh, Math.min(wait, current.stepSeconds * 1000 + 500));
  } catch (e) {
    byId("status").textContent = `${errorMessage(e)} — 10초 뒤 다시 시도합니다.`;
    timer = window.setTimeout(refresh, 10_000);
  }
}

function tick(): void {
  const now = new Date();
  byId("clock").textContent = `${pad(now.getHours())}:${pad(now.getMinutes())}`;
  if (current) {
    const left = Math.max(0, Date.parse(current.expiresAt) - Date.now());
    byId("remain").style.width = `${(left / (current.stepSeconds * 1000)) * 100}%`;
  }
}

async function keepAwake(): Promise<void> {
  try {
    await (navigator as Navigator & { wakeLock?: { request(type: "screen"): Promise<unknown> } }).wakeLock?.request("screen");
  } catch {
    /* 지원하지 않거나 거절: 기기 설정에서 화면 꺼짐을 꺼 두세요 */
  }
}

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") {
    void refresh();
    void keepAwake();
  }
});
window.setInterval(tick, 250);
void refresh();
void keepAwake();
