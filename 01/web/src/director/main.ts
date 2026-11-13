import "../shared/base.css";
import "./director.css";
import type { HubConnection } from "@microsoft/signalr";
import {
  PROGRESS_LABEL,
  api,
  errorMessage,
  upload,
  type Note,
  type NoteSource,
  type PatientHistory,
  type ReportListItem,
  type Visit,
} from "../shared/api";
import { MAX_RECORD_MS, Chime, canRecordInApp, startRecorder, toWav16k, type Recorder } from "../shared/audio";
import { addDaysIso, byId, esc, fmtBirth, fmtBirthShort, fmtDateLong, fmtDuration, fmtHM, fmtMD, installImageFallback, todayIso, toast } from "../shared/format";
import { connectLive } from "../shared/live";

/* ---------- 상태 ---------- */

interface PendingUpload {
  key: number;
  label: string;
  percent: number | null; // null = 변환 준비 중
}

const state = {
  day: todayIso(),
  list: [] as ReportListItem[],
  selected: null as number | null,
  visit: null as Visit | null,
  notes: [] as Note[],
  history: null as PatientHistory | null,
  uploads: [] as PendingUpload[],
  confirmDelete: null as number | null,
  seen: loadSeen(),
};

/** 이 기기에서 이미 열어 본 보고 (NEW 표시용). 저장이 막힌 브라우저에서도 화면은 그대로 동작합니다. */
function loadSeen(): Set<number> {
  try {
    return new Set(JSON.parse(localStorage.getItem("gm.seen") ?? "[]") as number[]);
  } catch {
    return new Set();
  }
}
function markSeen(visitId: number): void {
  state.seen.add(visitId);
  try {
    localStorage.setItem("gm.seen", JSON.stringify([...state.seen].slice(-500)));
  } catch {
    /* 무시 */
  }
}

const SOURCE_LABEL: Record<NoteSource, string> = { InApp: "화면 녹음", FilePick: "파일", NativeApp: "녹음 앱" };
const isToday = () => state.day === todayIso();

/* ---------- 알림음 ---------- */

const chime = new Chime();
const unlockBar = byId("unlock");
byId("startWork").addEventListener("click", () => {
  chime.unlock();
  unlockBar.hidden = true;
  chime.play();
});

/* ---------- 실시간 ---------- */

const hub: HubConnection = connectLive(byId("live"), async (h) => {
  await h.invoke("JoinDirector");
  await loadList();
  if (state.selected) await loadDetail(state.selected, false);
});

hub.on("ReportSubmitted", (e: { visitId: number; patientName: string }) => {
  if (isToday()) void loadList();
  chime.play();
  toast(`새 보고 · ${e.patientName}`);
});

hub.on("NoteUpdated", (note: Note) => {
  if (note.visitId === state.selected) upsertNote(note);
  scheduleListRefresh();
});

hub.on("NoteDeleted", (visitId: number, noteId: number) => {
  if (visitId === state.selected) {
    state.notes = state.notes.filter((n) => n.id !== noteId);
    renderNotes();
  }
  scheduleListRefresh();
});

let listTimer: number | undefined;
function scheduleListRefresh(): void {
  window.clearTimeout(listTimer);
  listTimer = window.setTimeout(() => void loadList(), 400);
}

/* ---------- 01 받은 보고 ---------- */

const directorEl = byId("director");
const inboxEl = byId<HTMLUListElement>("inbox");

byId("prevDay").addEventListener("click", () => changeDay(addDaysIso(state.day, -1)));
byId("nextDay").addEventListener("click", () => changeDay(addDaysIso(state.day, 1)));
byId("todayBtn").addEventListener("click", () => changeDay(todayIso()));

function changeDay(day: string): void {
  if (day > todayIso()) return;
  state.day = day;
  void loadList();
}

async function loadList(): Promise<void> {
  try {
    state.list = await api<ReportListItem[]>("GET", `/api/director/reports?date=${state.day}`);
    renderList();
    // 넓은 화면에서는 첫 보고를 바로 엽니다.
    if (!state.selected && state.list[0] && matchMedia("(min-width: 961px)").matches) void select(state.list[0].visitId);
  } catch (e) {
    toast(errorMessage(e));
  }
}

function renderList(): void {
  byId("dayLabel").textContent = fmtDateLong(state.day) + (isToday() ? " 오늘" : "");
  byId<HTMLButtonElement>("nextDay").disabled = isToday();
  byId("count").textContent = `${state.list.length}건`;

  if (state.list.length === 0) {
    inboxEl.innerHTML = `<li class="inbox-empty">${isToday() ? "아직 들어온 보고가 없습니다. 보고가 오면 여기에 바로 뜹니다." : "이 날 보고가 없습니다."}</li>`;
    return;
  }
  inboxEl.replaceChildren(
    ...state.list.map((r) => {
      const li = document.createElement("li");
      const isNew = !state.seen.has(r.visitId);
      const notes = r.noteCount ? `소견 ${r.noteCount}${r.pendingNoteCount ? ` · 변환 중 ${r.pendingNoteCount}` : ""}` : "소견 없음";
      li.innerHTML = `<button type="button" aria-pressed="${r.visitId === state.selected}">
          <span class="time">${fmtHM(r.reportedAt)}</span>
          <span class="nm">${esc(r.patientName)}<small>${fmtBirthShort(r.birthDate)}</small></span>
          ${isNew ? '<span class="new"><span class="dot-new" title="새 보고"></span></span>' : "<span></span>"}
          <span class="sub"><span class="mark ${r.progress}">${PROGRESS_LABEL[r.progress]}</span><span>${notes}</span></span>
        </button>`;
      li.querySelector("button")!.addEventListener("click", () => void select(r.visitId));
      return li;
    }),
  );
}

/* ---------- 상세 ---------- */

const detailEl = byId("detail");

async function select(visitId: number): Promise<void> {
  if (state.selected !== visitId) {
    state.confirmDelete = null;
    state.uploads = [];
  }
  state.selected = visitId;
  markSeen(visitId);
  renderList();
  directorEl.classList.add("has-detail");
  await loadDetail(visitId, true);
}

async function loadDetail(visitId: number, scrollTop: boolean): Promise<void> {
  try {
    const visit = await api<Visit>("GET", `/api/visits/${visitId}`);
    const [notes, history] = await Promise.all([
      api<Note[]>("GET", `/api/visits/${visitId}/notes`),
      api<PatientHistory>("GET", `/api/patients/${visit.patient.id}/history`),
    ]);
    if (state.selected !== visitId) return; // 그새 다른 보고를 눌렀으면 버림
    state.visit = visit;
    state.notes = notes;
    state.history = history;
    renderDetail();
    if (scrollTop) window.scrollTo({ top: 0 });
  } catch (e) {
    toast(errorMessage(e));
  }
}

function renderDetail(): void {
  const v = state.visit;
  if (!v) {
    detailEl.innerHTML = '<p class="detail-empty">왼쪽에서 보고를 선택하세요.</p>';
    return;
  }
  const fig = (title: string, sub: string, right: string, url: string | null, empty: string) => `<figure class="figure">
      ${url ? `<div class="frame"><img src="${esc(url)}" alt="${title} 사진" decoding="async"></div>` : `<div class="frame">${empty}</div>`}
      <figcaption class="caption"><span><b>${title}</b> ${sub}</span><span>${right}</span></figcaption></figure>`;

  const before = v.previous
    ? fig("직전", fmtMD(v.previous.visitDate), `${v.previous.daysAgo}일 전`, v.previous.photo.url, "")
    : fig("직전", "", "", null, "첫 방문");
  const today = fig("오늘", fmtMD(v.visitDate), v.photo ? `${fmtHM(v.photo.takenAt)} 촬영` : "", v.photo?.url ?? null, "사진 없음");

  detailEl.innerHTML = `
    <div class="detail-head">
      <button class="textbtn back" id="back" type="button">← 목록</button>
      <h1>${esc(v.patient.name)}<small>${fmtBirth(v.patient.birthDate)}</small></h1>
      <div class="state">
        ${v.progress ? `<span class="mark ${v.progress}">${PROGRESS_LABEL[v.progress]}</span>` : ""}
        <span class="meta">${fmtDateLong(v.visitDate)} ${v.reportedAt ? fmtHM(v.reportedAt) : ""} · ${esc(v.reportedBy ?? "")} 보고</span>
      </div>
    </div>
    <section class="block">
      <div class="section-head"><span class="index">02</span><h2>전후 사진</h2></div>
      <div class="photos">${before}${today}</div>
    </section>
    <section class="block">
      <div class="section-head"><span class="index">03</span><h2>간호사 메모</h2></div>
      <p class="memo">${v.memo ? esc(v.memo) : '<span class="muted">메모 없음</span>'}</p>
    </section>
    <section class="block">
      <div class="section-head"><span class="index">04</span><h2>원장 소견</h2><span class="grow"></span>
        <button class="btn primary rec-btn" id="openRec" type="button">녹음</button></div>
      <div class="notes" id="notes"></div>
    </section>
    <section class="block">
      <div class="section-head"><span class="index">05</span><h2>환자 기록</h2><span class="grow"></span><span class="meta" id="histCount"></span></div>
      <div class="history" id="history"></div>
    </section>`;

  byId("back").addEventListener("click", () => {
    directorEl.classList.remove("has-detail");
    window.scrollTo({ top: 0 });
  });
  byId("openRec").addEventListener("click", openChooser);
  renderNotes();
  renderHistory();
}

/* ---------- 04 원장 소견 ---------- */

/** 카드를 통째로 다시 그리면 재생 중인 소리가 끊기므로, 바뀐 카드만 교체합니다. */
const cardSig = new Map<number, string>();
const signature = (n: Note) => `${n.status}|${n.transcript?.createdAt ?? ""}|${n.error ?? ""}|${state.confirmDelete === n.id}`;

function upsertNote(note: Note): void {
  const i = state.notes.findIndex((n) => n.id === note.id);
  if (i >= 0) state.notes[i] = note;
  else state.notes.push(note);
  state.notes.sort((a, b) => a.seq - b.seq);
  renderNotes();
}

function renderNotes(): void {
  const box = document.getElementById("notes");
  if (!box) return;

  if (state.notes.length === 0 && state.uploads.length === 0) {
    box.innerHTML = '<p class="notes-empty">아직 소견이 없습니다. <b>녹음</b>을 누르면 녹음이 끝나는 대로 자동으로 텍스트가 됩니다.</p>';
    cardSig.clear();
    return;
  }
  box.querySelector(".notes-empty")?.remove();

  const wanted = new Set<string>();
  let prev: Element | null = null;
  for (const n of state.notes) {
    const key = `n${n.id}`;
    wanted.add(key);
    let card = box.querySelector<HTMLElement>(`[data-key="${key}"]`);
    const sig = signature(n);
    if (!card || cardSig.get(n.id) !== sig) {
      const fresh = buildNoteCard(n);
      if (card) {
        // 소리 재생 위치를 유지합니다.
        const oldAudio = card.querySelector("audio");
        const newAudio = fresh.querySelector("audio");
        if (oldAudio && newAudio) newAudio.replaceWith(oldAudio);
        card.replaceWith(fresh);
      }
      card = fresh;
      cardSig.set(n.id, sig);
    }
    if (prev ? prev.nextElementSibling !== card : box.firstElementChild !== card) {
      if (prev) prev.after(card);
      else box.prepend(card);
    }
    prev = card;
  }
  for (const u of state.uploads) {
    const key = `u${u.key}`;
    wanted.add(key);
    const card = buildUploadCard(u);
    const old = box.querySelector(`[data-key="${key}"]`);
    if (old) old.replaceWith(card);
    else box.append(card);
  }
  box.querySelectorAll<HTMLElement>("[data-key]").forEach((el) => {
    if (!wanted.has(el.dataset.key!)) el.remove();
  });
}

function buildNoteCard(n: Note): HTMLElement {
  const card = document.createElement("article");
  card.className = "note";
  card.dataset.key = `n${n.id}`;

  const statusHtml = {
    Queued: '<span class="status"><span class="spinner"></span>변환 대기</span>',
    Transcribing: '<span class="status"><span class="spinner"></span>텍스트로 바꾸는 중</span>',
    Transcribed: `<span class="status">텍스트 완료${n.transcript ? ` · ${(n.transcript.elapsedMs / 1000).toFixed(1)}초` : ""}</span>`,
    Failed: '<span class="status failed">변환 실패</span>',
  }[n.status];

  let body: string;
  if (n.status === "Transcribed" && n.transcript) {
    const segs = n.transcript.segments;
    body = segs.length > 1
      ? segs.map((s) => `<p><button class="ts" type="button" data-ms="${s.startMs}">${fmtDuration(s.startMs)}</button>${esc(s.text)}</p>`).join("")
      : `<p>${n.transcript.text ? esc(n.transcript.text) : '<span class="muted">말소리를 찾지 못했습니다.</span>'}</p>`;
  } else if (n.status === "Failed") {
    body = `<p class="small">${esc(n.error ?? "알 수 없는 오류")}</p>`;
  } else {
    body = `<div class="pending"><span class="spinner"></span>${n.status === "Queued" ? "앞 녹음이 끝나면 바로 변환합니다." : "이 PC의 Whisper가 변환하고 있습니다."}</div>`;
  }

  const foot = state.confirmDelete === n.id
    ? `<div class="confirm"><p>녹음과 연결된 스크립트를 함께 삭제합니다. 되돌릴 수 없습니다.</p>
         <button class="btn danger solid" type="button" data-act="delete-yes">삭제</button>
         <button class="btn" type="button" data-act="delete-no">취소</button></div>`
    : `${n.transcript?.text ? '<button class="textbtn" type="button" data-act="copy">텍스트 복사</button>' : ""}
       ${n.status === "Failed" ? '<button class="textbtn" type="button" data-act="retry">다시 변환</button>' : ""}
       <span class="grow"></span>
       <button class="textbtn danger" type="button" data-act="delete">삭제</button>`;

  card.innerHTML = `
    <header class="note-head"><b>소견 ${String(n.seq).padStart(2, "0")}</b>
      <span class="meta">${fmtHM(n.createdAt)} · ${fmtDuration(n.durationMs)} · ${SOURCE_LABEL[n.source]}</span>
      <span class="grow"></span>${statusHtml}</header>
    <audio controls preload="none" src="${esc(n.audioUrl)}"></audio>
    <div class="transcript">${body}</div>
    <footer class="note-foot">${foot}</footer>`;

  card.addEventListener("click", (e) => {
    const t = (e.target as HTMLElement).closest<HTMLElement>("[data-act], .ts");
    if (!t) return;
    if (t.classList.contains("ts")) {
      const audio = card.querySelector("audio")!;
      audio.currentTime = Number(t.dataset.ms) / 1000;
      void audio.play();
      return;
    }
    void act(n, t.dataset.act!);
  });
  return card;
}

function buildUploadCard(u: PendingUpload): HTMLElement {
  const card = document.createElement("article");
  card.className = "note uploading";
  card.dataset.key = `u${u.key}`;
  const label = u.percent === null ? "녹음 정리 중" : `올리는 중 ${u.percent}%`;
  card.innerHTML = `<header class="note-head"><b>${esc(u.label)}</b><span class="grow"></span><span class="status"><span class="spinner"></span>${label}</span></header>
    <div class="bar"><i style="width:${u.percent ?? 0}%"></i></div>`;
  return card;
}

async function act(n: Note, action: string): Promise<void> {
  switch (action) {
    case "copy":
      try {
        await navigator.clipboard.writeText(n.transcript?.text ?? "");
        toast("텍스트를 복사했습니다");
      } catch {
        toast("복사하지 못했습니다. 텍스트를 길게 눌러 선택해 주세요.");
      }
      break;
    case "retry":
      try {
        upsertNote(await api<Note>("POST", `/api/notes/${n.id}/retry`));
      } catch (e) {
        toast(errorMessage(e));
      }
      break;
    case "delete":
      state.confirmDelete = n.id;
      renderNotes();
      break;
    case "delete-no":
      state.confirmDelete = null;
      renderNotes();
      break;
    case "delete-yes":
      try {
        await api<void>("DELETE", `/api/notes/${n.id}`);
        state.confirmDelete = null;
        state.notes = state.notes.filter((x) => x.id !== n.id);
        renderNotes();
        scheduleListRefresh();
        toast("녹음과 스크립트를 삭제했습니다");
      } catch (e) {
        toast(errorMessage(e));
      }
      break;
  }
}

/* ---------- 녹음 ---------- */

const chooser = byId("recChooser");
const recorderEl = byId("recorder");

function openChooser(): void {
  byId<HTMLButtonElement>("recInApp").disabled = !canRecordInApp();
  chooser.hidden = false;
}
byId("recChooserClose").addEventListener("click", () => (chooser.hidden = true));
chooser.addEventListener("click", (e) => {
  if (e.target === chooser) chooser.hidden = true;
});

byId("recNative").addEventListener("change", (e) => pickFile(e, "NativeApp"));
byId("recFile").addEventListener("change", (e) => pickFile(e, "FilePick"));

function pickFile(e: Event, source: NoteSource): void {
  const input = e.target as HTMLInputElement;
  const file = input.files?.[0];
  input.value = "";
  chooser.hidden = true;
  if (file) void submitAudio(file, source);
}

let recorder: Recorder | null = null;
let raf = 0;

byId("recInApp").addEventListener("click", async () => {
  chooser.hidden = true;
  if (!state.visit) return;
  try {
    recorder = await startRecorder();
  } catch {
    toast("마이크를 쓸 수 없습니다. 브라우저 설정에서 마이크 권한을 허용해 주세요.");
    return;
  }
  byId("recWho").textContent = `${state.visit.patient.name} · ${fmtBirth(state.visit.patient.birthDate)}`;
  recorderEl.hidden = false;
  const clock = byId("recClock");
  const level = byId("recLevel");
  const loop = () => {
    if (!recorder) return;
    const ms = performance.now() - recorder.startedAt;
    clock.textContent = fmtDuration(ms);
    level.style.width = `${Math.round(recorder.level() * 100)}%`;
    if (ms >= MAX_RECORD_MS) {
      void stopRecording();
      return;
    }
    raf = requestAnimationFrame(loop);
  };
  raf = requestAnimationFrame(loop);
});

async function stopRecording(): Promise<void> {
  if (!recorder) return;
  cancelAnimationFrame(raf);
  const r = recorder;
  recorder = null;
  recorderEl.hidden = true;
  const blob = await r.stop();
  void submitAudio(blob, "InApp");
}

byId("recStop").addEventListener("click", () => void stopRecording());
byId("recCancel").addEventListener("click", () => {
  cancelAnimationFrame(raf);
  recorder?.cancel();
  recorder = null;
  recorderEl.hidden = true;
  toast("녹음을 취소했습니다");
});

let uploadSeq = 0;
async function submitAudio(file: Blob, source: NoteSource): Promise<void> {
  const visitId = state.selected;
  if (!visitId) return;
  const u: PendingUpload = { key: ++uploadSeq, label: "새 소견", percent: null };
  state.uploads.push(u);
  renderNotes();
  const done = () => {
    state.uploads = state.uploads.filter((x) => x !== u);
    if (state.selected === visitId) renderNotes();
  };

  try {
    const { wav, durationMs } = await toWav16k(file);
    if (durationMs < 500) throw new Error("녹음이 너무 짧습니다.");
    if (durationMs > 12 * 60 * 1000) throw new Error("12분이 넘는 녹음은 올릴 수 없습니다. 나눠서 녹음해 주세요.");
    u.label = `새 소견 · ${fmtDuration(durationMs)}`;
    u.percent = 0;
    renderNotes();

    const form = new FormData();
    form.append("audio", wav, "note.wav");
    form.append("source", source);
    const note = await upload<Note>(`/api/visits/${visitId}/notes`, form, (p) => {
      u.percent = p;
      const card = document.querySelector<HTMLElement>(`[data-key="u${u.key}"]`);
      if (card) {
        card.querySelector<HTMLElement>(".bar i")!.style.width = `${p}%`;
        card.querySelector(".status")!.lastChild!.textContent = `올리는 중 ${p}%`;
      }
    });
    done();
    if (state.selected === visitId) upsertNote(note);
    toast("올렸습니다. 텍스트로 바꾸는 중입니다");
  } catch (e) {
    done();
    toast(errorMessage(e));
  }
}

/* ---------- 05 환자 기록 ---------- */

function renderHistory(): void {
  const box = document.getElementById("history");
  if (!box || !state.history) return;
  const others = state.history.visits.filter((h) => h.visitId !== state.selected);
  byId("histCount").textContent = `지난 방문 ${others.length}회`;
  if (others.length === 0) {
    box.innerHTML = '<p class="notes-empty">지난 방문 기록이 없습니다.</p>';
    return;
  }
  box.innerHTML = others
    .map((h) => {
      const notes = h.notes
        .map((n) => `<div class="hist-note"><span class="meta">소견 ${String(n.seq).padStart(2, "0")} · ${fmtDuration(n.durationMs)}</span>
            <p>${n.transcript?.text ? esc(n.transcript.text) : '<span class="muted">텍스트 없음</span>'}</p></div>`)
        .join("");
      return `<details class="hist">
          <summary><span class="date">${fmtDateLong(h.visitDate).slice(2)}</span>
            ${h.progress ? `<span class="mark ${h.progress}">${PROGRESS_LABEL[h.progress]}</span>` : `<span class="mark Draft">보고 전</span>`}
            <span class="excerpt">${esc(h.memo || (h.notes.length ? `소견 ${h.notes.length}개` : ""))}</span></summary>
          <div class="hist-body">
            <div class="frame">${h.photo ? `<img src="${esc(h.photo.url)}" alt="${h.visitDate} 사진" loading="lazy">` : "사진 없음"}</div>
            <div class="hist-text"><p class="memo">${h.memo ? esc(h.memo) : '<span class="muted">메모 없음</span>'}</p>${notes}</div>
          </div></details>`;
    })
    .join("");
}

/* ---------- 시작 ---------- */

installImageFallback();
renderList();
renderDetail();
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") chooser.hidden = true;
});
