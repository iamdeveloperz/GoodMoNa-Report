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

/** 밀리초 → M:SS */
export const fmtDuration = (ms: number): string => {
  const s = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(s / 60)}:${pad(s % 60)}`;
};

const WEEKDAY = ["일", "월", "화", "수", "목", "금", "토"];

/** 2026-11-13 → 2026.11.13 (금) */
export const fmtDateLong = (iso: string): string => {
  const [y, m, d] = iso.split("-").map(Number) as [number, number, number];
  return `${y}.${pad(m)}.${pad(d)} (${WEEKDAY[new Date(y, m - 1, d).getDay()]})`;
};

/** 브라우저 기준 오늘 (YYYY-MM-DD). 병원 PC·아이패드는 한국 시간이므로 서버 "오늘"과 같습니다. */
export const todayIso = (): string => {
  const d = new Date();
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
};

export const addDaysIso = (iso: string, days: number): string => {
  const [y, m, d] = iso.split("-").map(Number) as [number, number, number];
  const x = new Date(y, m - 1, d + days);
  return `${x.getFullYear()}-${pad(x.getMonth() + 1)}-${pad(x.getDate())}`;
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

/** 사진을 못 불러오면(파일 손상·삭제) 깨진 아이콘 대신 안내 문구를 보여 줍니다. */
export function installImageFallback(): void {
  document.addEventListener(
    "error",
    (e) => {
      const img = e.target;
      if (!(img instanceof HTMLImageElement) || !img.parentElement?.classList.contains("frame")) return;
      const frame = img.parentElement;
      img.remove();
      frame.append("사진을 불러올 수 없습니다");
    },
    true,
  );
}

export const byId =<T extends HTMLElement = HTMLElement>(id: string): T => {
  const el = document.getElementById(id);
  if (!el) throw new Error(`#${id} 요소가 없습니다.`);
  return el as T;
};
