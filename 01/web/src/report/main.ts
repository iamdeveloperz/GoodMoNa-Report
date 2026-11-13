import * as signalR from "@microsoft/signalr";
import QRCode from "qrcode";
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
import { byId, esc, fmtBirth, fmtHM, fmtMD, fmtRemain, toast } from "../shared/format";

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

/* ---------- 실시간 연결 (SignalR) ---------- */

const hub = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/visits")
  .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
  .configureLogging(signalR.LogLevel.Warning)
  .build();

const conn = byId("conn");
const setConn = (s: "connecting" | "connected" | "disconnected", text: string) => {
  conn.dataset.state = s;
  conn.textContent = text;
};

hub.on("PhotoArrived", (visitId: number, photo: Photo) => {
  if (state.visit?.id !== visitId) return;
  state.visit.photo = photo;
  state.justArrived = true;
  closeQr();
  renderVisit();
  toast("오늘 사진이 도착했습니다");
});

hub.on("VisitReported", (visitId: number) => {
  // 다른 PC에서 같은 환자를 보고한 경우 화면을 맞춥니다.
  if (state.visit?.id === visitId && state.visit.status !== "Reported") void reloadVisit();
});

hub.onreconnecting(() => setConn("connecting", "다시 연결 중"));
hub.onreconnected(async () => {
  setConn("connected", "실시간 연결됨");
  // 끊긴 동안 도착한 사진을 놓치지 않도록 그룹에 다시 들어가고 새로 읽습니다.
  if (state.visit) {
    await hub.invoke("JoinVisit", state.visit.id);
    await reloadVisit();
  }
});
hub.onclose(() => {
  setConn("disconnected", "연결 끊김");
  window.setTimeout(startHub, 5000);
});

async function startHub(): Promise<void> {
  try {
    setConn("connecting", "연결 중");
    await hub.start();
    setConn("connected", "실시간 연결됨");
    if (state.visit) await hub.invoke("JoinVisit", state.visit.id);
  } catch {
    setConn("disconnected", "연결 끊김");
    window.setTimeout(startHub, 5000);
  }
}

async function joinVisitGroup(next: number, prev: number | undefined): Promise<void> {
  if (hub.state !== signalR.HubConnectionState.Connected) return; // 연결되면 startHub가 다시 넣어 줍니다.
  if (prev !== undefined && prev !== next) await hub.invoke("LeaveVisit", prev).catch(() => {});
  await hub.invoke("JoinVisit", next).catch(() => {});
}

/* ---------- 환자 검색 ---------- */

const qInput = byId<HTMLInputElement>("q");
const resultsEl = byId<HTMLUListElement>("results");
const noResult = byId("noResult");
let searchSeq = 0;
let searchTimer: number | undefined;

qInput.addEventListener("input", () => {
  state.q = qInput.value;
  window.clearTimeout(searchTimer);
  searchTimer = window.setTimeout(() => void search(), 200);
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
      const chip =
        p.todayStatus === "Reported" ? '<span class="chip done">보고 완료</span>'
        : p.todayStatus === "Draft" ? '<span class="chip draft">작성 중</span>'
        : "";
      li.innerHTML = `<button type="button" aria-pressed="${state.visit?.patient.id === p.id}">
          <span><span class="nm">${esc(p.name)}</span><span class="bd">${fmtBirth(p.birthDate)}</span></span>${chip}
          <span class="sub">${p.lastVisitDate ? `최근 방문 ${fmtMD(p.lastVisitDate)}` : "첫 방문"}</span></button>`;
      li.querySelector("button")!.addEventListener("click", () => void openVisit(p.id));
      return li;
    }),
  );
  noResult.hidden = !(q && state.results.length === 0);
  noResult.textContent = `‘${q}’ 환자가 없습니다. 아래에서 신규 등록할 수 있습니다.`;
}

/* ---------- 신규 환자 등록 ---------- */

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

/* ---------- 오늘 방문 ---------- */

const panel = byId("visitPanel");

async function openVisit(patientId: number): Promise<void> {
  try {
    const prev = state.visit?.id;
    const visit = await api<Visit>("POST", `/api/patients/${patientId}/visits/today`);
    state.visit = visit;
    state.justArrived = false;
    renderVisit();
    await joinVisitGroup(visit.id, prev);
    // 방문이 새로 생기면 목록의 "작성 중" 표시가 바뀌므로 다시 읽습니다.
    void search();
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

interface Slot {
  label: string;
  sub?: string;
  right?: string;
  photo: Photo | null;
  emptyText: string;
  dashed?: boolean;
  arrive?: boolean;
}

function photoSlot(s: Slot): string {
  return `<div class="slot">
      <div class="slot-head"><span><b>${s.label}</b>${s.sub ? ` · ${s.sub}` : ""}</span><span>${s.right ?? ""}</span></div>
      ${s.photo
        ? `<div class="ph ${s.arrive ? "arrive" : ""}"><img src="${esc(s.photo.url)}" alt="${s.label} 사진" decoding="async"></div>`
        : `<div class="ph ${s.dashed ? "empty" : ""}">${s.emptyText}</div>`}
    </div>`;
}

function renderVisit(): void {
  const v = state.visit;
  if (!v) {
    panel.innerHTML = '<p class="muted">왼쪽에서 환자를 검색해 선택하세요.</p>';
    return;
  }
  const reported = v.status === "Reported";
  const d = draftOf(v.id);

  const before = v.previous
    ? photoSlot({ label: "직전", sub: fmtMD(v.previous.visitDate), right: `${v.previous.daysAgo}일 전`, photo: v.previous.photo, emptyText: "" })
    : photoSlot({ label: "직전", photo: null, emptyText: "첫 방문 — 비교할 사진이 없습니다" });
  const today = photoSlot({
    label: "오늘",
    sub: fmtMD(v.visitDate),
    right: v.photo ? `${fmtHM(v.photo.takenAt)} 촬영` : "",
    photo: v.photo,
    emptyText: "QR로 촬영하세요",
    dashed: true,
    arrive: state.justArrived,
  });
  state.justArrived = false;

  const status = reported
    ? `<span class="chip done">보고 완료 ${v.reportedAt ? fmtHM(v.reportedAt) : ""}</span>`
    : '<span class="chip draft">작성 중</span>';

  const form = reported
    ? `<div class="readonly-report">
         <div class="row"><span class="label">경과</span><span class="chip ${v.progress}">${v.progress ? PROGRESS_LABEL[v.progress] : ""}</span><span class="label">· ${esc(v.reportedBy ?? "")} 보고</span></div>
         <div>${v.memo ? esc(v.memo) : '<span class="muted">메모 없음</span>'}</div></div>`
    : `<form class="form-grid" id="reportForm" novalidate>
         <div class="row"><span class="label" id="progLabel" style="min-width:36px">경과</span>
           <div class="seg" role="radiogroup" aria-labelledby="progLabel">
             ${(Object.keys(PROGRESS_LABEL) as Progress[])
               .map((p) => `<label><input type="radio" name="progress" value="${p}" ${d.progress === p ? "checked" : ""}><span>${PROGRESS_LABEL[p]}</span></label>`)
               .join("")}
           </div></div>
         <label class="label" for="memo">메모</label>
         <textarea id="memo" maxlength="500" placeholder="예: 정수리 밀도 좋아짐, 두피 붉은기 감소">${esc(d.memo)}</textarea>
         <div class="counter" id="memoCount">${d.memo.length} / 500</div>
         <div class="row between">
           <span class="reason" id="reason"></span>
           <button class="btn primary" type="submit" id="submitReport">원장님께 보고</button>
         </div></form>`;

  panel.innerHTML = `
    <div class="visit-head"><h2>${esc(v.patient.name)}<span>${fmtBirth(v.patient.birthDate)}</span></h2>
      <div class="row"><span class="label">오늘 ${fmtMD(v.visitDate)}</span>${status}</div></div>
    <div class="photos">${before}${today}</div>
    ${reported ? "" : `<div class="row"><button class="btn" type="button" id="openQr">${v.photo ? "다시 촬영 (QR)" : "촬영 QR 띄우기"}</button>
      <span class="muted small">폰으로 QR을 스캔하면 촬영 화면이 열립니다</span></div>`}
    ${form}`;

  // 목록의 선택 표시 갱신
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
    await QRCode.toCanvas(qrCanvas, t.captureUrl, { width: 240, margin: 2, errorCorrectionLevel: "M" });
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
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && !qrScrim.hidden) closeQr();
});

/* ---------- 시작 ---------- */

renderVisit();
void startHub();
