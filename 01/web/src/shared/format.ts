export const pad = (n: number): string => String(n).padStart(2, "0");

/** 1988-04-17 → 1988.04.17 */
export const fmtBirth = (iso: string): string => iso.replaceAll("-", ".");

/** 1988-04-17 → 88.04.17 */
export const fmtBirthShort = (iso: string): string => iso.slice(2).replaceAll("-", ".");

/** 2026-11-13 → 11.13 */
export const fmtMD = (iso: string): string => iso.slice(5).replace("-", ".");

/** ISO 시각 → 브라우저 로컬 HH:MM */
export const fmtHM = (iso: string): string => {
  const d = new Date(iso);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

/** 남은 ms → MM:SS (음수는 00:00) */
export const fmtRemain = (ms: number): string => {
  const s = Math.max(0, Math.ceil(ms / 1000));
  return `${pad(Math.floor(s / 60))}:${pad(s % 60)}`;
};

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
