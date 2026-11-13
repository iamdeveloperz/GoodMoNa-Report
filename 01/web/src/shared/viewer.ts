/**
 * 사진 확대 모드와 전후 비교 모드.
 * - 확대: 원장이 사진 한 장을 화면 가득 띄워 핀치·휠·더블탭으로 확대합니다. 다른 날짜로 바꿔도 확대 위치가 그대로라
 *   같은 부위를 날짜별로 넘겨 볼 수 있습니다.
 * - 비교: 환자에게 보여 주는 화면. 전·후 사진 두 장만 5:5로 나란히 띄우고 나머지 화면 요소는 모두 감춥니다.
 *   한쪽을 확대하면 다른 쪽도 같은 위치가 함께 확대됩니다.
 */
import "./viewer.css";
import { esc, fmtDateLong } from "./format";

export interface ViewPhoto {
  url: string;
  date: string; // YYYY-MM-DD
  /** 첫 방문 · 직전 · 이번 방문 같은 짧은 표시 */
  tag?: string;
}

const MAX_SCALE = 8;
const DOUBLE_TAP_SCALE = 2.5;

const daysBetween = (a: string, b: string): number =>
  Math.round((Date.parse(`${b}T00:00:00Z`) - Date.parse(`${a}T00:00:00Z`)) / 86_400_000);

/**
 * 확대 상태(배율·이동량). 비교 모드에서는 두 칸이 같은 상태를 나눠 쓰므로 한쪽을 확대하면 다른 쪽도 같이 움직입니다.
 * 칸 크기가 같다는 전제라 이동량을 px로 그대로 씁니다.
 */
class Zoom {
  s = 1;
  x = 0;
  y = 0;
  w = 1;
  h = 1;
  private readonly targets = new Set<HTMLImageElement>();
  private readonly listeners = new Set<() => void>();

  attach(img: HTMLImageElement): void {
    this.targets.add(img);
    this.apply();
  }
  onChange(fn: () => void): void {
    this.listeners.add(fn);
  }
  resize(w: number, h: number): void {
    this.w = Math.max(1, w);
    this.h = Math.max(1, h);
    this.reset();
  }
  reset(): void {
    this.s = 1;
    this.x = 0;
    this.y = 0;
    this.apply();
  }
  zoomAt(px: number, py: number, factor: number): void {
    const s2 = Math.min(MAX_SCALE, Math.max(1, this.s * factor));
    const k = s2 / this.s;
    this.x = px - (px - this.x) * k;
    this.y = py - (py - this.y) * k;
    this.s = s2;
    this.apply();
  }
  pan(dx: number, dy: number): void {
    this.x += dx;
    this.y += dy;
    this.apply();
  }
  private apply(): void {
    if (this.s <= 1.001) {
      this.s = 1;
      this.x = 0;
      this.y = 0;
    } else {
      // 사진이 칸 밖으로 빠져나가지 않게
      this.x = Math.min(0, Math.max(this.w - this.w * this.s, this.x));
      this.y = Math.min(0, Math.max(this.h - this.h * this.s, this.y));
    }
    const t = `translate(${this.x}px, ${this.y}px) scale(${this.s})`;
    for (const img of this.targets) img.style.transform = t;
    for (const fn of this.listeners) fn();
  }
}

/** 칸 하나에 핀치(두 손가락)·드래그·휠·더블탭 제스처를 붙입니다. 한 번 탭하면 onTap. */
function bindGestures(pane: HTMLElement, zoom: Zoom, onTap?: () => void): void {
  const pts = new Map<number, { x: number; y: number }>();
  let pinch: { d: number; mx: number; my: number } | null = null;
  let travel = 0;
  let downAt = 0;
  let lastTap = 0;
  let tapTimer: number | undefined;

  const rel = (e: PointerEvent | WheelEvent) => {
    const r = pane.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  };
  const pinchOf = () => {
    const [a, b] = [...pts.values()] as [{ x: number; y: number }, { x: number; y: number }];
    return { d: Math.hypot(a.x - b.x, a.y - b.y) || 1, mx: (a.x + b.x) / 2, my: (a.y + b.y) / 2 };
  };
  const glide = () => {
    pane.parentElement?.classList.add("glide");
    window.setTimeout(() => pane.parentElement?.classList.remove("glide"), 240);
  };

  pane.addEventListener("pointerdown", (e) => {
    pane.setPointerCapture(e.pointerId);
    pts.set(e.pointerId, rel(e));
    if (pts.size === 1) {
      travel = 0;
      downAt = performance.now();
    }
    pinch = pts.size === 2 ? pinchOf() : null;
    pane.classList.add("grabbing");
  });

  pane.addEventListener("pointermove", (e) => {
    const prev = pts.get(e.pointerId);
    if (!prev) return;
    const p = rel(e);
    pts.set(e.pointerId, p);
    if (pts.size === 1) {
      travel += Math.hypot(p.x - prev.x, p.y - prev.y);
      zoom.pan(p.x - prev.x, p.y - prev.y);
    } else if (pts.size === 2) {
      travel = 99;
      const m = pinchOf();
      if (pinch) {
        zoom.zoomAt(m.mx, m.my, m.d / pinch.d);
        zoom.pan(m.mx - pinch.mx, m.my - pinch.my);
      }
      pinch = m;
    }
  });

  const end = (e: PointerEvent) => {
    if (!pts.has(e.pointerId)) return;
    const p = pts.get(e.pointerId)!;
    pts.delete(e.pointerId);
    pinch = pts.size === 2 ? pinchOf() : null;
    if (pts.size > 0) return;
    pane.classList.remove("grabbing");
    if (e.type === "pointercancel" || travel > 8 || performance.now() - downAt > 350) return;

    // 탭: 연달아 두 번이면 확대/원래대로, 한 번이면 onTap
    const now = performance.now();
    if (now - lastTap < 300) {
      window.clearTimeout(tapTimer);
      lastTap = 0;
      glide();
      if (zoom.s > 1.05) zoom.reset();
      else zoom.zoomAt(p.x, p.y, DOUBLE_TAP_SCALE);
    } else {
      lastTap = now;
      if (onTap) tapTimer = window.setTimeout(onTap, 300);
    }
  };
  pane.addEventListener("pointerup", end);
  pane.addEventListener("pointercancel", end);

  // 마우스 휠 · 트랙패드 핀치(ctrlKey)
  pane.addEventListener(
    "wheel",
    (e) => {
      e.preventDefault();
      const p = rel(e);
      zoom.zoomAt(p.x, p.y, Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0025)));
    },
    { passive: false },
  );
}

/* ---------- 화면 전체 오버레이 공통 ---------- */

interface FullscreenDoc {
  webkitFullscreenElement?: Element | null;
  webkitExitFullscreen?: () => void;
}
interface FullscreenEl {
  webkitRequestFullscreen?: () => void;
}

/** iPad Safari는 webkit 접두사로만 되는 버전이 있어 둘 다 시도합니다. 안 되면 그냥 화면 덮개로만 씁니다. */
function enterFullscreen(el: HTMLElement): void {
  try {
    if (el.requestFullscreen) void el.requestFullscreen({ navigationUI: "hide" }).catch(() => {});
    else (el as HTMLElement & FullscreenEl).webkitRequestFullscreen?.();
  } catch {
    /* 지원하지 않으면 무시 */
  }
}
function exitFullscreen(): void {
  const d = document as Document & FullscreenDoc;
  try {
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
    else if (d.webkitFullscreenElement) d.webkitExitFullscreen?.();
  } catch {
    /* 무시 */
  }
}

let closeOpen: (() => void) | null = null;

function mountOverlay(el: HTMLElement, onKey: (e: KeyboardEvent) => void): () => void {
  closeOpen?.();
  const lastFocus = document.activeElement as HTMLElement | null;
  document.documentElement.classList.add("viewer-open");
  document.body.append(el);
  const key = (e: KeyboardEvent) => {
    if (e.key === "Escape") close();
    else onKey(e);
  };
  document.addEventListener("keydown", key);
  const close = () => {
    document.removeEventListener("keydown", key);
    el.remove();
    document.documentElement.classList.remove("viewer-open");
    exitFullscreen();
    closeOpen = null;
    lastFocus?.focus?.();
  };
  closeOpen = close;
  return close;
}

function watchSize(pane: HTMLElement, zoom: Zoom): void {
  const sync = () => zoom.resize(pane.clientWidth, pane.clientHeight);
  new ResizeObserver(sync).observe(pane);
  sync();
}

const label = (p: ViewPhoto) => `${fmtDateLong(p.date)}${p.tag ? ` · ${p.tag}` : ""}`;

/* ---------- 확대 모드 (원장용) ---------- */

export function openZoom(photos: ViewPhoto[], start: number): void {
  if (photos.length === 0) return;
  let i = Math.max(0, Math.min(photos.length - 1, start));

  const el = document.createElement("div");
  el.className = "viewer";
  el.setAttribute("role", "dialog");
  el.setAttribute("aria-modal", "true");
  el.setAttribute("aria-label", "사진 확대");
  el.innerHTML = `
    <div class="viewer-top">
      <span class="viewer-title" id="vTitle"></span>
      <span class="grow"></span>
      <span class="viewer-level" id="vLevel">100%</span>
      <button class="vbtn" type="button" data-act="out" aria-label="축소">−</button>
      <button class="vbtn" type="button" data-act="in" aria-label="확대">+</button>
      <button class="vbtn" type="button" data-act="fit">맞춤</button>
      <button class="vbtn" type="button" data-act="close">닫기</button>
    </div>
    <div class="viewer-stage"><div class="pane"><img alt="" draggable="false"></div>
      <button class="viewer-nav prev" type="button" data-act="prev" aria-label="이전 날짜">←</button>
      <button class="viewer-nav next" type="button" data-act="next" aria-label="다음 날짜">→</button>
    </div>
    <div class="viewer-foot">
      <p class="viewer-hint">두 손가락 · 휠 · 두 번 탭으로 확대 — 날짜를 바꿔도 확대한 위치는 그대로입니다</p>
      <div class="viewer-strip" role="tablist" aria-label="날짜"></div>
    </div>`;

  const pane = el.querySelector<HTMLElement>(".pane")!;
  const img = pane.querySelector("img")!;
  const strip = el.querySelector<HTMLElement>(".viewer-strip")!;
  const zoom = new Zoom();
  zoom.attach(img);
  zoom.onChange(() => (el.querySelector("#vLevel")!.textContent = `${Math.round(zoom.s * 100)}%`));
  bindGestures(pane, zoom);

  strip.innerHTML = photos
    .map((p, k) => `<button type="button" role="tab" data-k="${k}"><b>${esc(p.date.replaceAll("-", "."))}</b><small>${esc(p.tag ?? "")}</small></button>`)
    .join("");

  const show = (k: number) => {
    i = (k + photos.length) % photos.length;
    const p = photos[i]!;
    img.src = p.url;
    img.alt = `${p.date} 사진`;
    el.querySelector("#vTitle")!.textContent = label(p);
    strip.querySelectorAll<HTMLElement>("button").forEach((b) => b.setAttribute("aria-selected", String(Number(b.dataset.k) === i)));
    strip.querySelector<HTMLElement>(`[data-k="${i}"]`)?.scrollIntoView({ block: "nearest", inline: "nearest" });
    el.querySelectorAll<HTMLButtonElement>(".viewer-nav").forEach((b) => (b.hidden = photos.length < 2));
  };

  const center = (f: number) => {
    pane.parentElement!.classList.add("glide");
    zoom.zoomAt(zoom.w / 2, zoom.h / 2, f);
    window.setTimeout(() => pane.parentElement!.classList.remove("glide"), 240);
  };

  el.addEventListener("click", (e) => {
    const t = (e.target as HTMLElement).closest<HTMLElement>("[data-act], [data-k]");
    if (!t) return;
    if (t.dataset.k) return show(Number(t.dataset.k));
    switch (t.dataset.act) {
      case "close": return close();
      case "prev": return show(i - 1);
      case "next": return show(i + 1);
      case "in": return center(1.6);
      case "out": return center(1 / 1.6);
      case "fit": return zoom.reset();
    }
  });

  const close = mountOverlay(el, (e) => {
    if (e.key === "ArrowLeft") show(i - 1);
    else if (e.key === "ArrowRight") show(i + 1);
    else if (e.key === "+" || e.key === "=") center(1.6);
    else if (e.key === "-") center(1 / 1.6);
    else if (e.key === "0") zoom.reset();
  });
  watchSize(pane, zoom);
  show(i);
  el.querySelector<HTMLButtonElement>('[data-act="close"]')!.focus();
}

/* ---------- 비교 모드 (환자에게 보여 주는 화면) ---------- */

export function openCompare(photos: ViewPhoto[], before: number, after: number): void {
  if (photos.length < 2) return;
  let a = before;
  let b = after;

  const el = document.createElement("div");
  el.className = "compare";
  el.setAttribute("role", "dialog");
  el.setAttribute("aria-modal", "true");
  el.setAttribute("aria-label", "전후 비교");
  const options = photos.map((p, k) => `<option value="${k}">${esc(label(p))}</option>`).join("");
  el.innerHTML = `
    <div class="compare-panes">
      <div class="pane"><img alt="" draggable="false"><span class="pane-tag" id="tagA"></span></div>
      <div class="pane"><img alt="" draggable="false"><span class="pane-tag" id="tagB"></span></div>
    </div>
    <div class="compare-bar">
      <label class="pick"><span>전</span><select id="pickA" aria-label="전 사진 날짜">${options}</select></label>
      <label class="pick"><span>후</span><select id="pickB" aria-label="후 사진 날짜">${options}</select></label>
      <span class="grow"></span>
      <span class="viewer-hint">한쪽을 확대하면 양쪽이 함께 확대됩니다</span>
      <button class="vbtn" type="button" data-act="fit">맞춤</button>
      <button class="vbtn" type="button" data-act="close">닫기</button>
    </div>`;

  const panes = [...el.querySelectorAll<HTMLElement>(".pane")] as [HTMLElement, HTMLElement];
  const imgs = panes.map((p) => p.querySelector("img")!) as [HTMLImageElement, HTMLImageElement];
  const bar = el.querySelector<HTMLElement>(".compare-bar")!;
  const pickA = el.querySelector<HTMLSelectElement>("#pickA")!;
  const pickB = el.querySelector<HTMLSelectElement>("#pickB")!;

  const zoom = new Zoom();
  imgs.forEach((img) => zoom.attach(img));

  // 조작 막대는 잠시 뒤 숨기고, 화면을 한 번 탭하면 다시 보입니다. (환자에게는 사진만 보이도록)
  let hideTimer: number | undefined;
  const showBar = () => {
    bar.classList.remove("hide");
    window.clearTimeout(hideTimer);
    hideTimer = window.setTimeout(() => bar.classList.add("hide"), 3000);
  };
  const toggleBar = () => (bar.classList.contains("hide") ? showBar() : bar.classList.add("hide"));
  panes.forEach((p) => bindGestures(p, zoom, toggleBar));
  bar.addEventListener("pointerdown", showBar);
  bar.addEventListener("change", showBar);

  const render = () => {
    const pa = photos[a]!;
    const pb = photos[b]!;
    imgs[0].src = pa.url;
    imgs[0].alt = `전 ${pa.date}`;
    imgs[1].src = pb.url;
    imgs[1].alt = `후 ${pb.date}`;
    const gap = daysBetween(pa.date, pb.date);
    el.querySelector("#tagA")!.innerHTML = `<b>전</b> ${esc(pa.date.replaceAll("-", "."))}`;
    el.querySelector("#tagB")!.innerHTML = `<b>후</b> ${esc(pb.date.replaceAll("-", "."))}${gap > 0 ? `<small>${gap}일 뒤</small>` : ""}`;
    pickA.value = String(a);
    pickB.value = String(b);
  };
  pickA.addEventListener("change", () => {
    a = Number(pickA.value);
    render();
  });
  pickB.addEventListener("change", () => {
    b = Number(pickB.value);
    render();
  });

  el.addEventListener("click", (e) => {
    const act = (e.target as HTMLElement).closest<HTMLElement>("[data-act]")?.dataset.act;
    if (act === "close") close();
    else if (act === "fit") zoom.reset();
  });

  const close = mountOverlay(el, (e) => {
    if (e.key === "0") zoom.reset();
  });
  watchSize(panes[0], zoom);
  render();
  enterFullscreen(el);
  showBar();
}
