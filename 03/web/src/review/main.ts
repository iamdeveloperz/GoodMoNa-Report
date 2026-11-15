import "../shared/base.css";
import "../shared/shorts.css";
import "./review.css";
import { api, errorMessage } from "../shared/api";
import { byId, esc, fmtDuration, fmtSize, fmtStamp, fmtTime, toast } from "../shared/format";
import { showWho } from "../shared/session";
import { isBusy, STATUS_LABEL, type Cut, type Draft, type Finding, type SystemStatus } from "../shared/types";

const id = Number(new URLSearchParams(location.search).get("id"));
const base = `/api/shorts/drafts/${id}`;

let draft: Draft | null = null;
let status: SystemStatus | null = null;
let dirty = false;
let pollTimer: number | undefined;
let shownPreview = -1;

const el = {
  title: byId<HTMLInputElement>("title"),
  hook: byId<HTMLInputElement>("hook"),
  description: byId<HTMLTextAreaElement>("description"),
  hashtags: byId<HTMLInputElement>("hashtags"),
  hasPatient: byId<HTMLInputElement>("hasPatient"),
  consent: byId<HTMLInputElement>("consent"),
  video: byId<HTMLVideoElement>("video"),
  cues: byId("cues"),
  cuts: byId("cuts"),
  findings: byId("findings"),
  actions: byId("actions"),
};

void showWho(byId("who"), true);
void start();

async function start(): Promise<void> {
  if (!Number.isInteger(id) || id <= 0) {
    byId("code").textContent = "주소에 초안 번호가 없습니다";
    return;
  }
  try {
    status = await api<SystemStatus>("GET", "/api/shorts/status");
  } catch {
    /* 승인 버튼만 안 보임 */
  }
  await reload();
}

async function reload(): Promise<void> {
  try {
    draft = await api<Draft>("GET", base);
  } catch (e) {
    byId("code").textContent = errorMessage(e);
    return;
  }
  render();
  window.clearTimeout(pollTimer);
  if (isBusy(draft.status)) pollTimer = window.setTimeout(() => void reload(), 2000);
}

const editable = (d: Draft): boolean => d.status === "Review" || d.status === "Rendering";

/* ---------- 그리기 ---------- */

function render(): void {
  const d = draft!;
  document.title = `${d.code} 검토 — GoodMoNa`;
  byId("code").textContent = d.code;
  const state = byId("state");
  state.className = `state ${d.status}`;
  state.textContent = STATUS_LABEL[d.status];
  byId("stageNow").innerHTML = isBusy(d.status) && d.stage ? `<span class="spinner"></span>${esc(d.stage)}` : "";
  byId("savedBy").textContent = d.updatedByName ? `마지막 수정 ${d.updatedByName} · ${fmtStamp(d.updatedAt)}` : "";
  const error = byId("error");
  error.hidden = !(d.status === "Failed" && d.error);
  error.textContent = d.error ?? "";

  renderPlayer(d);
  if (!dirty) renderForm(d);
  renderFindings(d);
  renderActions(d);
}

function renderPlayer(d: Draft): void {
  if (d.previewVersion > 0 && d.previewVersion !== shownPreview) {
    shownPreview = d.previewVersion;
    el.video.src = `${base}/preview.mp4?v=${d.previewVersion}`;
    el.video.hidden = false;
    byId("noPreview").hidden = true;
  }
  const s = d.source;
  const facts: [string, string][] = [
    ["원본", `${s.originalName} · ${fmtSize(s.sizeBytes)}`],
    ["길이", s.durationMs > 0 ? `${fmtDuration(s.durationMs)}${d.outputDurationMs > 0 ? ` → ${fmtDuration(d.outputDurationMs)} (무음 ${fmtDuration(s.durationMs - d.outputDurationMs)} 자름)` : ""}` : "—"],
    ["화면", s.width > 0 ? `${s.width}×${s.height}${s.height > s.width ? " 세로" : " 가로 — 가운데를 세로로 잘라 씁니다"}` : "—"],
    ["받아쓰기", s.sttModel ? `${s.sttModel} · ${s.sttRuntime} · ${(s.sttElapsedMs / 1000).toFixed(1)}초` : "—"],
    ["올린 사람", `${s.uploadedByName} · ${fmtStamp(s.createdAt)}`],
  ];
  byId("facts").innerHTML = facts.map(([k, v]) => `<dt>${k}</dt><dd>${esc(v)}</dd>`).join("");
  const note = byId("aiNote");
  note.hidden = !d.aiNote;
  note.textContent = d.aiNote ?? "";
}

function renderForm(d: Draft): void {
  const can = editable(d);
  el.title.value = d.title;
  el.hook.value = d.hook;
  el.description.value = d.description;
  el.hashtags.value = d.hashtags;
  el.hasPatient.checked = d.hasPatient;
  el.consent.checked = d.consentConfirmed;
  byId("consentRow").hidden = !d.hasPatient;
  for (const input of [el.title, el.hook, el.description, el.hashtags, el.hasPatient, el.consent]) input.disabled = !can;

  byId("titleChips").innerHTML = chips(d.titleCandidates, d.title, "title", can);
  byId("hookChips").innerHTML = chips(d.hookCandidates, d.hook, "hook", can);

  el.cues.innerHTML = d.cues
    .map((c) => {
      const out = toOutput(c.startMs, d);
      const time = out === null ? `<span class="t">${fmtTime(c.startMs)}</span>` : `<button class="t" type="button" data-seek="${out}" title="미리보기에서 이 자막으로">${fmtTime(c.startMs)}</button>`;
      return `<li data-cue="${c.id}">${time}<textarea rows="${c.text.split("\n").length}" ${can ? "" : "disabled"}>${esc(c.text)}</textarea></li>`;
    })
    .join("");

  el.cuts.innerHTML = d.cuts.length === 0
    ? '<li class="meta">자를 무음이 없습니다.</li>'
    : d.cuts
        .map((c) => `<li data-cut="${c.index}" class="${c.removed ? "" : "kept"}"><label class="check"><input type="checkbox" ${c.removed ? "checked" : ""} ${can ? "" : "disabled"}> 자름</label>
          <span>${fmtTime(c.startMs)} – ${fmtTime(c.endMs)}</span><span class="grow"></span><span class="meta">${((c.endMs - c.startMs) / 1000).toFixed(1)}초</span></li>`)
        .join("");
  const removed = d.cuts.filter((c) => c.removed);
  byId("cutSummary").textContent = `${removed.length}곳 · ${(removed.reduce((a, c) => a + c.endMs - c.startMs, 0) / 1000).toFixed(1)}초`;
}

function chips(list: string[], current: string, field: string, can: boolean): string {
  return list
    .map((t) => `<button class="chip" type="button" data-fill="${field}" aria-pressed="${t === current}" ${can ? "" : "disabled"}>${esc(t)}</button>`)
    .join("");
}

function renderFindings(d: Draft): void {
  if (d.findings.length === 0) {
    el.findings.innerHTML = '<li class="none">걸린 표현이 없습니다.</li>';
    return;
  }
  const can = editable(d);
  el.findings.innerHTML = d.findings.map((f) => finding(f, can)).join("");
}

const SOURCE_LABEL: Record<Finding["source"], string> = { Rule: "규칙", Ai: "AI", Similar: "반복 제목" };

function finding(f: Finding, can: boolean): string {
  const cls = !f.blocking ? "info" : f.resolution ? "done" : "";
  const head = `<div class="what"><b>${esc(f.rule)}</b><span class="meta">${esc(f.where)} · ${SOURCE_LABEL[f.source]}</span></div>
    <blockquote>${esc(f.excerpt)}</blockquote><div class="why">${esc(f.reason)}</div>`;
  let tail = "";
  if (f.resolution) tail = `<div class="resolved">해소: ${esc(f.resolution)} — ${esc(f.resolvedByName ?? "")} ${f.resolvedAt ? fmtStamp(f.resolvedAt) : ""}</div>`;
  else if (f.blocking && can)
    tail = `<div class="resolve" data-finding="${f.id}"><input type="text" maxlength="300" placeholder="문제없다고 본 이유 (또는 위에서 글을 고친 뒤 저장)"><button class="btn compact" type="button">해소</button></div>`;
  return `<li class="${cls}">${head}${tail}</li>`;
}

function renderActions(d: Draft): void {
  const approver = status?.canApprove === true;
  const blockers = byId("blockers");
  blockers.innerHTML = d.status === "Review" ? d.approvalBlockers.map((b) => `<li>${esc(b)}</li>`).join("") : "";
  byId("publish").hidden = d.status !== "Ready";

  const parts: string[] = [];
  switch (d.status) {
    case "Review":
      if (approver) {
        parts.push(`<button class="btn primary" type="button" data-act="approve" ${d.approvalBlockers.length > 0 || dirty ? "disabled" : ""}>승인 (완성본 만들기)</button>`);
        parts.push('<button class="btn" type="button" data-act="reject-open">반려</button>');
      } else {
        parts.push('<span class="small muted">원장 승인을 기다립니다. 점검을 모두 해소해 두면 원장이 바로 승인할 수 있습니다.</span>');
      }
      break;
    case "Ready":
      parts.push(`<a class="btn primary" href="${base}/final.mp4?download=true">완성본 받기 (${esc(d.code)}.mp4)</a>`);
      parts.push('<button class="btn" type="button" data-act="copy-title">제목 복사</button>');
      parts.push('<button class="btn" type="button" data-act="copy-desc">설명·해시태그 복사</button>');
      parts.push(`<span class="small muted">승인 ${esc(d.approvedByName ?? "")} · ${d.approvedAt ? fmtStamp(d.approvedAt) : ""}</span>`);
      if (approver) parts.push('<button class="textbtn" type="button" data-act="reopen">승인 취소하고 다시 검토</button>');
      break;
    case "Published":
      parts.push(`<a class="btn" href="${esc(d.youTubeUrl ?? "#")}" target="_blank" rel="noopener">유튜브에서 보기</a>`);
      parts.push(`<span class="small">${d.relatedVideoLinked ? "관련 동영상 연결함" : '<span class="flag">관련 동영상 연결 안 함</span>'} · 게시 ${esc(d.publishedByName ?? "")} ${d.publishedAt ? fmtStamp(d.publishedAt) : ""}</span>`);
      break;
    case "Rejected":
      parts.push(`<span class="small">반려: <b>${esc(d.rejectReason ?? "")}</b> — ${esc(d.rejectedByName ?? "")}</span>`);
      if (approver) parts.push('<button class="btn" type="button" data-act="reopen">다시 검토로</button>');
      break;
    case "Failed":
      parts.push('<button class="btn primary" type="button" data-act="retry">다시 시도</button>');
      break;
    default:
      parts.push(`<span class="small muted">${esc(d.stage ?? "처리 중")} — 끝나면 자동으로 바뀝니다.</span>`);
  }
  if (!isBusy(d.status) && d.status !== "Published") {
    parts.push('<span class="grow"></span><button class="textbtn danger" type="button" data-act="delete">초안 지우기</button>');
  }
  el.actions.innerHTML = parts.join("");
}

/* ---------- 편집본 시간(미리보기 위치) ---------- */

function keeps(d: Draft): [number, number][] {
  const out: [number, number][] = [];
  let cursor = 0;
  for (const c of [...d.cuts].filter((x) => x.removed).sort((a, b) => a.startMs - b.startMs)) {
    if (c.startMs > cursor) out.push([cursor, c.startMs]);
    cursor = Math.max(cursor, c.endMs);
  }
  if (cursor < d.source.durationMs) out.push([cursor, d.source.durationMs]);
  return out;
}

/** 원본 시각 → 미리보기 시각(잘린 자리면 다음 남은 구간 시작). 서버 TimeMap과 같은 규칙 */
function toOutput(ms: number, d: Draft): number | null {
  let offset = 0;
  for (const [s, e] of keeps(d)) {
    if (ms <= e) return offset + Math.max(0, ms - s);
    offset += e - s;
  }
  return null;
}

/* ---------- 수정 · 저장 ---------- */

function markDirty(): void {
  if (!draft || !editable(draft)) return;
  dirty = true;
  byId("savebar").hidden = false;
  byId("consentRow").hidden = !el.hasPatient.checked;
  renderActions(draft);
}

byId("page").addEventListener("input", (e) => {
  const t = e.target as HTMLElement;
  if (t.closest(".resolve") || t.id === "ytUrl" || t.closest(".reject-box")) return;
  if (t.closest("[data-cue]")) t.closest("li")!.classList.add("changed");
  markDirty();
});
byId("page").addEventListener("change", (e) => {
  const t = e.target as HTMLElement;
  const cut = t.closest<HTMLLIElement>("[data-cut]");
  if (cut) {
    cut.classList.toggle("kept", !(t as HTMLInputElement).checked);
    cut.classList.add("changed");
  }
  if (t.id === "related") return;
  if (t instanceof HTMLInputElement && t.type === "checkbox") markDirty();
});

byId("discard").addEventListener("click", () => {
  dirty = false;
  byId("savebar").hidden = true;
  render();
});

byId("save").addEventListener("click", () => void save());

async function save(): Promise<void> {
  const d = draft!;
  const cues = [...el.cues.querySelectorAll<HTMLLIElement>("li[data-cue]")]
    .map((li) => ({ id: Number(li.dataset.cue), text: li.querySelector("textarea")!.value.trim() }))
    .filter((c) => d.cues.find((x) => x.id === c.id)?.text !== c.text);
  const cuts = [...el.cuts.querySelectorAll<HTMLLIElement>("li[data-cut]")]
    .map((li) => ({ index: Number(li.dataset.cut), removed: li.querySelector("input")!.checked }))
    .filter((c) => (d.cuts[c.index] as Cut | undefined)?.removed !== c.removed);
  try {
    draft = await api<Draft>("PUT", base, {
      version: d.version,
      title: el.title.value,
      hook: el.hook.value,
      description: el.description.value,
      hashtags: el.hashtags.value,
      hasPatient: el.hasPatient.checked,
      consentConfirmed: el.consent.checked,
      cues,
      cuts,
    });
    dirty = false;
    byId("savebar").hidden = true;
    toast(draft.status === "Rendering" ? "저장했습니다 — 미리보기를 다시 만듭니다." : "저장했습니다.");
    render();
    if (isBusy(draft.status)) {
      window.clearTimeout(pollTimer);
      pollTimer = window.setTimeout(() => void reload(), 1500);
    }
  } catch (e) {
    toast(errorMessage(e));
  }
}

/* ---------- 버튼들 ---------- */

let deleteArmed = false;

byId("page").addEventListener("click", async (e) => {
  const t = e.target as HTMLElement;
  const d = draft;
  if (!d) return;

  const seek = t.closest<HTMLElement>("[data-seek]");
  if (seek && !el.video.hidden) {
    el.video.currentTime = Number(seek.dataset.seek) / 1000;
    void el.video.play();
    return;
  }

  const chip = t.closest<HTMLButtonElement>("[data-fill]");
  if (chip) {
    const target = chip.dataset.fill === "title" ? el.title : el.hook;
    target.value = chip.textContent ?? "";
    chip.parentElement!.querySelectorAll(".chip").forEach((c) => c.setAttribute("aria-pressed", String(c === chip)));
    markDirty();
    return;
  }

  const resolve = t.closest<HTMLElement>(".resolve");
  if (resolve && t.tagName === "BUTTON") {
    if (dirty) return toast("먼저 수정한 내용을 저장해 주세요.");
    const note = resolve.querySelector("input")!.value.trim();
    if (note.length < 2) return toast("문제없다고 본 이유를 적어 주세요.");
    await act(() => api<Draft>("POST", `${base}/findings/${resolve.dataset.finding}/resolve`, { note }), "해소했습니다.");
    return;
  }

  const action = t.closest<HTMLElement>("[data-act]")?.dataset.act;
  switch (action) {
    case "approve":
      await act(() => api<Draft>("POST", `${base}/approve`, { version: d.version }), "승인했습니다 — 완성본을 만듭니다.");
      break;
    case "reject-open": {
      el.actions.insertAdjacentHTML("beforeend",
        '<div class="reject-box"><div class="field" style="flex:1"><span class="label">반려 이유</span><input type="text" id="rejectReason" maxlength="300"></div><button class="btn danger" type="button" data-act="reject">반려하기</button></div>');
      t.remove();
      byId<HTMLInputElement>("rejectReason").focus();
      break;
    }
    case "reject": {
      const reason = byId<HTMLInputElement>("rejectReason").value.trim();
      if (reason.length < 2) return toast("반려 이유를 적어 주세요.");
      await act(() => api<Draft>("POST", `${base}/reject`, { reason }), "반려했습니다.");
      break;
    }
    case "reopen":
      await act(() => api<Draft>("POST", `${base}/reopen`), "다시 검토 상태로 돌렸습니다.");
      break;
    case "retry":
      await act(() => api<Draft>("POST", `${base}/retry`), "다시 시도합니다.");
      break;
    case "copy-title":
      await copy(d.title, "제목을 복사했습니다.");
      break;
    case "copy-desc":
      await copy(d.description.includes(d.hashtags) ? d.description : `${d.description}\n\n${d.hashtags}`, "설명·해시태그를 복사했습니다.");
      break;
    case "delete":
      if (!deleteArmed) {
        deleteArmed = true;
        t.textContent = "한 번 더 누르면 원본·영상까지 지웁니다";
        window.setTimeout(() => {
          deleteArmed = false;
          if (t.isConnected) t.textContent = "초안 지우기";
        }, 4000);
        return;
      }
      try {
        await api("DELETE", base);
        location.href = "/shorts";
      } catch (err) {
        toast(errorMessage(err));
      }
      break;
  }
});

byId("publishBtn").addEventListener("click", () =>
  void act(
    () => api<Draft>("POST", `${base}/publish`, { youTubeUrl: byId<HTMLInputElement>("ytUrl").value.trim(), relatedVideoLinked: byId<HTMLInputElement>("related").checked }),
    "게시 기록을 남겼습니다.",
  ));

async function act(call: () => Promise<Draft>, done: string): Promise<void> {
  try {
    draft = await call();
    toast(done);
    render();
    window.clearTimeout(pollTimer);
    if (isBusy(draft.status)) pollTimer = window.setTimeout(() => void reload(), 1500);
  } catch (e) {
    toast(errorMessage(e));
    if (e instanceof Error && "status" in e && (e as { status: number }).status === 409) void reload();
  }
}

async function copy(text: string, done: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    toast(done);
  } catch {
    toast("복사하지 못했습니다. 직접 선택해 복사해 주세요.");
  }
}

window.addEventListener("beforeunload", (e) => {
  if (dirty) e.preventDefault();
});
