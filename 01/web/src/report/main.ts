import "../shared/base.css";
import "./report.css";
import QRCode from "qrcode";
import type { HubConnection } from "@microsoft/signalr";
import {
  ApiError,
  PROGRESS_LABEL,
  api,
  errorMessage,
  serverNow,
  type CaptureToken,
  type PatientSummary,
  type Photo,
  type Progress,
  type Visit,
} from "../shared/api";
import { byId, esc, fmtBirth, fmtHM, fmtMD, fmtRemain, installImageFallback, toast } from "../shared/format";
import { connectLive, isConnected } from "../shared/live";

/* ---------- 상태 ---------- */

interface Draft {
  progress: Progress | null;
  memo: string;
}

const state = {
  q: "",
  results: [] as PatientSummary[],
  visit: null as Visit | null,
  /** 보고 전까지 화면에만 들고 있는 입력값 (방문별) */
  drafts: new Map<number, Draft>(),
  token: null as CaptureToken | null,
  justArrived: false,
  submitting: false,
};

const draftOf = (visitId: number): Draft => {
  let d = state.drafts.get(visitId);
  if (!d) state.drafts.set(visitId, (d = { progress: null, memo: "" }));
  return d;
};

/* ---------- 실시간 ---------- */

let joined: number | undefined;
const hub: HubConnection = connectLive(byId("live"), async (h) => {
  joined = undefined;
  if (state.visit) {
    await h.invoke("JoinVisit", state.visit.id);
    joined = state.visit.id;
    await reloadVisit(); // 끊긴 동안 도착한 사진을 놓치지 않도록
  }
});

hub.on("PhotoArrived", (visitId: number, photo: Photo) => {
  if (state.visit?.id !== visitId) return;
  state.visit.photo = photo;
  state.justArrived = true;
  closeQr();
  renderVisit();
  toast("오늘 사진이 도착했습니다");
});

hub.on("VisitReported", (visitId: number) => {
  if (state.visit?.id === visitId && state.visit.status !== "Reported") void reloadVisit();
});

async function follow(visitId: number): Promise<void> {
  if (!isConnected(hub) || joined === visitId) return;
  if (joined !== undefined) await hub.invoke("LeaveVisit", joined).catch(() => {});
  await hub.invoke("JoinVisit", visitId).catch(() => {});
  joined = visitId;
}

/* ---------- 01 환자 검색 ---------- */

const qInput = byId<HTMLInputElement>("q");
const resultsEl = byId<HTMLUListElement>("results");
const noResult = byId("noResult");
let searchSeq = 0;
let searchTimer: number | undefined;

qInput.addEventListener("input", () => {
  state.q = qInput.value;
  window.clearTimeout(searchTimer);
  searchTimer = window.setTimeout(() => void search(), 180);
});

async function search(): Promise<void> {
  const q = state.q.trim();
  const seq = ++searchSeq;
  if (!q) {
    state.results = [];
    renderResults();
    return;
  }
  try {
    const list = await api<PatientSummary[]>("GET", `/api/patients?q=${encodeURIComponent(q)}`);
    if (seq !== searchSeq) return; // 늦게 온 이전 검색 결과는 버립니다.
    state.results = list;
    renderResults();
  } catch (e) {
    toast(errorMessage(e));
  }
}

function renderResults(): void {
  const q = state.q.trim();
  resultsEl.replaceChildren(
    ...state.results.map((p) => {
      const li = document.createElement("li");
      const status = p.todayStatus === "Reported" ? '<span class="mark Reported">보고 완료</span>'
        : p.todayStatus === "Draft" ? '<span class="mark Draft">작성 중</span>' : "";
      li.innerHTML = `<button type="button" aria-pressed="${state.visit?.patient.id === p.id}">
          <span><span class="nm">${esc(p.name)}</span><span class="bd">${fmtBirth(p.birthDate)}</span></span>${status}
          <span class="sub">${p.lastVisitDate ? `최근 방문 ${fmtMD(p.lastVisitDate)}` : "첫 방문"}</span></button>`;
      li.querySelector("button")!.addEventListener("click", () => void openVisit(p.id));
      return li;
    }),
  );
  noResult.hidden = !(q && state.results.length === 0);
  noResult.textContent = `‘${q}’ 환자가 없습니다. 신규 등록할 수 있습니다.`;
}

/* ---------- 신규 환자 ---------- */

const regForm = byId<HTMLFormElement>("regForm");
const toggleReg = byId<HTMLButtonElement>("toggleReg");
const regErr = byId("regErr");

toggleReg.addEventListener("click", () => {
  regForm.hidden = !regForm.hidden;
  toggleReg.setAttribute("aria-expanded", String(!regForm.hidden));
  if (!regForm.hidden) {
    const name = byId<HTMLInputElement>("regName");
    if (!name.value) name.value = state.q.trim();
    name.focus();
  }
});

regForm.addEventListener("submit", async (e) => {
  e.preventDefault();
  regErr.hidden = true;
  const name = byId<HTMLInputElement>("regName").value;
  const birth = byId<HTMLInputElement>("regBirth").value;
  try {
    const p = await api<PatientSummary>("POST", "/api/patients", { name, birth });
    afterRegister(p.id, p.name, "등록했습니다");
  } catch (err) {
    const existing = err instanceof ApiError && err.status === 409 ? (err.body?.existing as { id: number; name: string } | undefined) : undefined;
    if (existing) {
      afterRegister(existing.id, existing.name, "이미 등록된 환자라 그 환자를 열었습니다");
      return;
    }
    regErr.textContent = errorMessage(err);
    regErr.hidden = false;
  }
});

function afterRegister(patientId: number, name: string, message: string): void {
  regForm.reset();
  regForm.hidden = true;
  toggleReg.setAttribute("aria-expanded", "false");
  qInput.value = state.q = name;
  toast(message);
  void openVisit(patientId).then(search);
}

/* ---------- 02 오늘 방문 ---------- */

const panel = byId("visit");

async function openVisit(patientId: number): Promise<void> {
  try {
    const visit = await api<Visit>("POST", `/api/patients/${patientId}/visits/today`);
    state.visit = visit;
    state.justArrived = false;
    renderVisit();
    await follow(visit.id);
    void search(); // "작성 중" 표시 갱신
  } catch (e) {
    toast(errorMessage(e));
  }
}

async function reloadVisit(): Promise<void> {
  if (!state.visit) return;
  try {
    state.visit = await api<Visit>("GET", `/api/visits/${state.visit.id}`);
    renderVisit();
  } catch (e) {
    toast(errorMessage(e));
  }
}

interface Fig {
  title: string;
  sub?: string;
  right?: string;
  photo: Photo | null;
  empty: string;
  dashed?: boolean;
  arrive?: boolean;
}

const figure = (f: Fig): string => `<figure class="figure">
    ${f.photo
      ? `<div class="frame ${f.arrive ? "arrive" : ""}"><img src="${esc(f.photo.url)}" alt="${f.title} 사진" decoding="async"></div>`
      : `<div class="frame ${f.dashed ? "empty" : ""}">${f.empty}</div>`}
    <figcaption class="caption"><span><b>${f.title}</b>${f.sub ? ` ${f.sub}` : ""}</span><span>${f.right ?? ""}</span></figcaption>
  </figure>`;

function renderVisit(): void {
  const v = state.visit;
  if (!v) {
    panel.innerHTML = '<p class="visit-empty">왼쪽에서 환자를 찾아 선택하세요.</p>';
    return;
  }
  const reported = v.status === "Reported";
  const d = draftOf(v.id);

  const before = v.previous
    ? figure({ title: "직전", sub: fmtMD(v.previous.visitDate), right: `${v.previous.daysAgo}일 전`, photo: v.previous.photo, empty: "" })
    : figure({ title: "직전", photo: null, empty: "첫 방문<br>비교할 사진 없음" });
  const today = figure({
    title: "오늘",
    sub: fmtMD(v.visitDate),
    right: v.photo ? `${fmtHM(v.photo.takenAt)} 촬영` : "",
    photo: v.photo,
    empty: "아래 QR로 촬영",
    dashed: true,
    arrive: state.justArrived,
  });
  state.justArrived = false;

  const status = reported
    ? `<span class="mark Reported">보고 완료 ${v.reportedAt ? fmtHM(v.reportedAt) : ""}</span>`
    : '<span class="mark Draft">작성 중</span>';

  const form = reported
    ? `<div class="reported">
         <span class="mark ${v.progress}">${v.progress ? PROGRESS_LABEL[v.progress] : ""}</span>
         <span>${v.memo ? esc(v.memo) : '<span class="muted">메모 없음</span>'}</span>
         <span class="meta">${esc(v.reportedBy ?? "")} 보고 · 원장 화면에 전달됨</span>
       </div>`
    : `<form class="report-form" id="reportForm" novalidate>
         <div class="field">
           <span class="label" id="progLabel">경과</span>
           <div class="seg" role="radiogroup" aria-labelledby="progLabel">
             ${(Object.keys(PROGRESS_LABEL) as Progress[])
               .map((p) => `<label><input type="radio" name="progress" value="${p}" ${d.progress === p ? "checked" : ""}><span class="${p}">${PROGRESS_LABEL[p]}</span></label>`)
               .join("")}
           </div>
         </div>
         <div class="field">
           <label class="label" for="memo">메모</label>
           <textarea id="memo" maxlength="500" placeholder="예: 정수리 밀도 좋아짐, 두피 붉은기 감소">${esc(d.memo)}</textarea>
           <span class="counter" id="memoCount">${d.memo.length} / 500</span>
         </div>
         <div class="submit-row">
           <span class="reason" id="reason"></span>
           <button class="btn primary big" type="submit" id="submitReport">원장님께 보고</button>
         </div>
       </form>`;

  panel.innerHTML = `
    <div class="visit-title">
      <h1>${esc(v.patient.name)}<small>${fmtBirth(v.patient.birthDate)}</small></h1>
      <div class="state"><span class="meta">오늘 ${fmtMD(v.visitDate)}</span>${status}</div>
    </div>
    <section class="block">
      <div class="section-head"><span class="index">02</span><h2>사진</h2></div>
      <div class="photos">${before}${today}</div>
      ${reported ? "" : `<div class="capture-row"><button class="btn" type="button" id="openQr">${v.photo ? "다시 촬영 · QR" : "촬영 QR 띄우기"}</button>
        <span class="meta">폰 카메라로 QR을 스캔하면 촬영 화면이 열립니다</span></div>`}
    </section>
    <section class="block">
      <div class="section-head"><span class="index">03</span><h2>보고</h2></div>
      ${form}
    </section>`;

  resultsEl.querySelectorAll("button[aria-pressed]").forEach((b, i) =>
    b.setAttribute("aria-pressed", String(state.results[i]?.id === v.patient.id)),
  );

  if (reported) return;

  byId("openQr").addEventListener("click", () => void openQr());
  const formEl = byId<HTMLFormElement>("reportForm");
  formEl.addEventListener("change", (e) => {
    const t = e.target as HTMLInputElement;
    if (t.name === "progress") {
      d.progress = t.value as Progress;
      updateReason();
    }
  });
  byId<HTMLTextAreaElement>("memo").addEventListener("input", (e) => {
    d.memo = (e.target as HTMLTextAreaElement).value;
    byId("memoCount").textContent = `${d.memo.length} / 500`;
  });
  formEl.addEventListener("submit", (e) => {
    e.preventDefault();
    void submitReport();
  });
  updateReason();
}

function updateReason(): void {
  const v = state.visit;
  if (!v) return;
  const d = draftOf(v.id);
  const reason = !v.photo ? "오늘 사진을 먼저 촬영해 주세요." : !d.progress ? "경과를 선택해 주세요." : "";
  byId("reason").textContent = reason;
  byId<HTMLButtonElement>("submitReport").disabled = Boolean(reason) || state.submitting;
}

async function submitReport(): Promise<void> {
  const v = state.visit;
  if (!v || state.submitting) return;
  const d = draftOf(v.id);
  state.submitting = true;
  updateReason();
  try {
    state.visit = await api<Visit>("PUT", `/api/visits/${v.id}/report`, { progress: d.progress, memo: d.memo });
    state.drafts.delete(v.id);
    renderVisit();
    void search();
    toast("원장님께 보고했습니다");
  } catch (e) {
    toast(errorMessage(e));
  } finally {
    state.submitting = false;
    if (state.visit?.status !== "Reported") updateReason();
  }
}

/* ---------- 촬영 QR ---------- */

const qrScrim = byId("qrScrim");
const qrCanvas = byId<HTMLCanvasElement>("qrCanvas");
const qrTimer = byId("qrTimer");
const qrRenew = byId<HTMLButtonElement>("qrRenew");
let qrInterval: number | undefined;

async function openQr(): Promise<void> {
  const v = state.visit;
  if (!v) return;
  try {
    const t = await api<CaptureToken>("POST", `/api/visits/${v.id}/capture-tokens`);
    state.token = t;
    await QRCode.toCanvas(qrCanvas, t.captureUrl, { width: 280, margin: 2, errorCorrectionLevel: "M", color: { dark: "#0e0e0e", light: "#ffffff" } });
    qrCanvas.classList.remove("expired");
    byId("qrWho").textContent = `${v.patient.name} · ${fmtBirth(v.patient.birthDate)} · 오늘`;
    byId("qrUrl").textContent = t.captureUrl;
    qrRenew.hidden = true;
    qrScrim.hidden = false;
    tickQr();
    window.clearInterval(qrInterval);
    qrInterval = window.setInterval(tickQr, 1000);
  } catch (e) {
    toast(errorMessage(e));
  }
}

function tickQr(): void {
  if (!state.token) return;
  const left = Date.parse(state.token.expiresAt) - serverNow();
  qrTimer.textContent = fmtRemain(left);
  if (left <= 0) {
    qrCanvas.classList.add("expired");
    qrRenew.hidden = false;
    window.clearInterval(qrInterval);
  }
}

function closeQr(): void {
  qrScrim.hidden = true;
  window.clearInterval(qrInterval);
}

byId("qrClose").addEventListener("click", closeQr);
qrRenew.addEventListener("click", () => void openQr());
qrScrim.addEventListener("click", (e) => {
  if (e.target === qrScrim) closeQr();
});
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !qrScrim.hidden) closeQr();
});

installImageFallback();
renderVisit();
