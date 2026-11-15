import "../shared/base.css";
import "../shared/shorts.css";
import "./shorts.css";
import { api, errorMessage, uploadFile } from "../shared/api";
import { byId, esc, fmtDuration, fmtSize, fmtStamp, toast } from "../shared/format";
import { showWho } from "../shared/session";
import { isBusy, STATUS_LABEL, type Draft, type DraftListItem, type SystemStatus, type TitleImportResult } from "../shared/types";

const rows = byId("rows");
let status: SystemStatus | null = null;
let pollTimer: number | undefined;

void showWho(byId("who"), true);
void loadStatus();
void loadDrafts();

/* ---------- 설정 안내 ---------- */

async function loadStatus(): Promise<void> {
  try {
    status = await api<SystemStatus>("GET", "/api/shorts/status");
  } catch {
    return;
  }
  byId("limit").textContent = `최대 ${status.maxUploadMegabytes.toLocaleString()}MB`;
  byId("titleCount").textContent = status.titleHistoryCount > 0 ? `${status.titleHistoryCount.toLocaleString()}개 등록됨` : "아직 없음";

  const notes: string[] = [];
  if (!status.ffmpeg)
    notes.push('<div class="notice bad"><b>ffmpeg 없음</b>서버 PC에 ffmpeg를 설치해야 영상을 만들 수 있습니다. (<code>winget install --id Gyan.FFmpeg -e</code> 후 서버 재시작)</div>');
  if (!status.whisperModel)
    notes.push('<div class="notice bad"><b>Whisper 모델 없음</b>음성 인식 모델 파일이 설정 경로에 없습니다. 01과 같은 <code>C:\\GoodMoNa\\models</code>를 확인해 주세요.</div>');
  if (!status.aiEnabled)
    notes.push('<div class="notice"><b>AI 꺼짐</b>제목은 첫 문장으로, 훅은 비워서 초안을 만듭니다. 켜려면 데이터 폴더 설정의 <code>App:Ai:Mode</code>를 Ollama 또는 Claude로.</div>');
  const box = byId("notices");
  box.innerHTML = notes.join("");
  box.hidden = notes.length === 0;
}

/* ---------- 목록 ---------- */

async function loadDrafts(): Promise<void> {
  let list: DraftListItem[];
  try {
    list = await api<DraftListItem[]>("GET", "/api/shorts/drafts");
  } catch (e) {
    toast(errorMessage(e));
    return;
  }
  byId("count").textContent = `${list.length}편`;
  byId("empty").hidden = list.length > 0;
  rows.innerHTML = list.map(row).join("");

  window.clearTimeout(pollTimer);
  if (list.some((d) => isBusy(d.status))) pollTimer = window.setTimeout(() => void loadDrafts(), 2500);
}

function row(d: DraftListItem): string {
  const href = `/shorts/review?id=${d.id}`;
  const stage = isBusy(d.status) && d.stage ? `<span class="stage"><span class="spinner"></span>${esc(d.stage)}</span>` : "";
  const error = d.status === "Failed" && d.error ? `<span class="err">${esc(d.error)}</span>` : "";
  const length = d.durationMs > 0
    ? d.outputDurationMs > 0 ? `${fmtDuration(d.durationMs)} → <b>${fmtDuration(d.outputDurationMs)}</b>` : fmtDuration(d.durationMs)
    : "—";
  const findings = d.openFindings > 0 ? `<span class="flag">${d.openFindings}건 남음</span>` : d.status === "Processing" ? "—" : "없음";
  return `<tr data-href="${href}">
    <td class="code">${esc(d.code)}</td>
    <td class="title"><a href="${href}">${esc(d.title || "(제목 없음)")}</a><small>${esc(d.originalName)}</small></td>
    <td><span class="state ${d.status}">${STATUS_LABEL[d.status]}</span>${stage}${error}</td>
    <td class="num">${length}</td>
    <td class="num">${findings}</td>
    <td class="num">${esc(d.uploadedByName)}<br><span class="meta">${fmtStamp(d.createdAt)}</span></td>
  </tr>`;
}

rows.addEventListener("click", (e) => {
  const tr = (e.target as HTMLElement).closest<HTMLTableRowElement>("tr[data-href]");
  if (tr && !(e.target as HTMLElement).closest("a")) location.href = tr.dataset.href!;
});

/* ---------- 올리기 ---------- */

const drop = byId("drop");
const input = byId<HTMLInputElement>("file");
let uploadingNow = false;

input.addEventListener("change", () => {
  const f = input.files?.[0];
  input.value = "";
  if (f) void send(f);
});
drop.addEventListener("dragover", (e) => {
  e.preventDefault();
  drop.classList.add("over");
});
drop.addEventListener("dragleave", () => drop.classList.remove("over"));
drop.addEventListener("drop", (e) => {
  e.preventDefault();
  drop.classList.remove("over");
  const f = e.dataTransfer?.files[0];
  if (f) void send(f);
});

async function send(file: File): Promise<void> {
  if (uploadingNow) {
    toast("올리는 중입니다. 끝난 뒤 다음 파일을 올려 주세요.");
    return;
  }
  // 외부 주소(Cloudflare 터널)는 요청 본문 100MB 제한 → 큰 촬영본은 서버 PC에서 직접
  const local = ["localhost", "127.0.0.1", "[::1]"].includes(location.hostname);
  if (!local && file.size > 95 * 1024 * 1024) {
    toast(`외부 주소로는 95MB까지 올릴 수 있습니다(${fmtSize(file.size)}). 서버 PC에서 http://localhost:5003/shorts 로 올려 주세요.`);
    return;
  }
  const max = (status?.maxUploadMegabytes ?? 2048) * 1024 * 1024;
  if (file.size > max) {
    toast(`파일이 너무 큽니다(${fmtSize(file.size)}). 최대 ${status?.maxUploadMegabytes ?? 2048}MB.`);
    return;
  }
  uploadingNow = true;
  const box = byId("uploading");
  const bar = byId("uploadBar");
  byId("uploadName").textContent = `${file.name} · ${fmtSize(file.size)}`;
  box.hidden = false;
  try {
    const d = await uploadFile<Draft>(`/api/shorts/sources?name=${encodeURIComponent(file.name)}`, file, (p) => {
      bar.style.width = `${p}%`;
      byId("uploadPct").textContent = `${p}%`;
    });
    toast(`${d.code} 올림 — 자동으로 초안을 만들고 있습니다.`);
    await loadDrafts();
  } catch (e) {
    toast(errorMessage(e));
  } finally {
    uploadingNow = false;
    box.hidden = true;
    bar.style.width = "0";
  }
}

/* ---------- 기존 제목 가져오기 ---------- */

byId("titlesImport").addEventListener("click", async () => {
  const area = byId<HTMLTextAreaElement>("titlesText");
  if (!area.value.trim()) {
    toast("붙여 넣은 목록이 없습니다.");
    return;
  }
  try {
    const r = await api<TitleImportResult>("POST", "/api/shorts/titles/import", { text: area.value });
    toast(`새로 ${r.added}개 · 갱신 ${r.updated}개 · 모두 ${r.total}개`);
    area.value = "";
    byId("titleCount").textContent = `${r.total.toLocaleString()}개 등록됨`;
  } catch (e) {
    toast(errorMessage(e));
  }
});
