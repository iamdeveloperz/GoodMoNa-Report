export const pad = (n: number): string => String(n).padStart(2, "0");

/** 밀리초 → M:SS */
export const fmtDuration = (ms: number): string => {
  const s = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(s / 60)}:${pad(s % 60)}`;
};

/** 밀리초 → M:SS.s (자막·자르기 위치) */
export const fmtTime = (ms: number): string => {
  const t = Math.max(0, ms) / 1000;
  return `${Math.floor(t / 60)}:${(t % 60).toFixed(1).padStart(4, "0")}`;
};

/** ISO 시각 → MM.DD HH:MM (브라우저 로컬) */
export const fmtStamp = (iso: string): string => {
  const d = new Date(iso);
  return `${pad(d.getMonth() + 1)}.${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

export const fmtSize = (bytes: number): string =>
  bytes >= 1024 * 1024 * 1024 ? `${(bytes / 1024 / 1024 / 1024).toFixed(1)}GB` : `${Math.max(1, Math.round(bytes / 1024 / 1024))}MB`;

export const esc = (s: unknown): string =>
  String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);

let toastTimer: number | undefined;
export function toast(message: string): void {
  const t = document.getElementById("toast");
  if (!t) return;
  t.textContent = message;
  t.hidden = false;
  window.clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => (t.hidden = true), 2800);
}

export const byId = <T extends HTMLElement = HTMLElement>(id: string): T => {
  const el = document.getElementById(id);
  if (!el) throw new Error(`#${id} 요소가 없습니다.`);
  return el as T;
};
