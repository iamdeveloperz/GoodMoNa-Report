import "../shared/base.css";
import "./import.css";
import { ApiError, api, errorMessage, toApiError } from "../shared/api";
import { byId, esc, toast } from "../shared/format";
import { showWho } from "../shared/session";

void showWho(byId("who"), true);

/* ---------- 서버 DTO ---------- */

interface Issue {
  row: number;
  message: string;
}
interface Preview {
  encoding: string;
  rows: number;
  patients: number;
  newPatients: number;
  visits: number;
  newVisits: number;
  photos: number;
  errorRows: number;
  errors: Issue[];
  warnings: Issue[];
}
interface Result {
  newPatients: number;
  newVisits: number;
  updatedVisits: number;
  photosAdded: number;
  photosSkipped: number;
  skippedRows: number;
}
interface Status {
  state: "Idle" | "Running" | "Succeeded" | "Failed";
  done: number;
  total: number;
  photosDone: number;
  photosTotal: number;
  result: Result | null;
  error: string | null;
}
interface Folder {
  root: string;
  count: number;
  totalBytes: number;
  files: { path: string; size: number }[];
}

const PHOTO_EXT = /\.(jpe?g|png|webp)$/i;
const fmtBytes = (n: number) =>
  n >= 1 << 30 ? `${(n / (1 << 30)).toFixed(1)}GB` : n >= 1 << 20 ? `${(n / (1 << 20)).toFixed(1)}MB` : `${Math.ceil(n / 1024)}KB`;
const num = (n: number) => n.toLocaleString("ko-KR");

/* ---------- 02 사진 ---------- */

let folder: Folder | null = null;

async function loadFolder(): Promise<void> {
  try {
    folder = await api<Folder>("GET", "/api/import/files");
    byId("folderInfo").textContent = `가져오기 폴더에 ${num(folder.count)}장 · ${fmtBytes(folder.totalBytes)}`;
    byId("importRoot").textContent = folder.root;
  } catch (e) {
    if (e instanceof ApiError && e.status === 403) {
      // 외부(Cloudflare)로 열었을 때: 화면 전체를 안내로 바꿈
      document.querySelector(".import")!.innerHTML = `<p class="import-lead">기록 가져오기는 서버 PC에서만 쓸 수 있습니다.</p><p class="err">${esc(e.message)}</p>`;
      return;
    }
    byId("folderInfo").textContent = errorMessage(e);
  }
}

let stopUpload = false;
byId("upStop").addEventListener("click", () => (stopUpload = true));
byId("pickFolder").addEventListener("change", (e) => void pickPhotos(e, true));
byId("pickFiles").addEventListener("change", (e) => void pickPhotos(e, false));

async function pickPhotos(e: Event, isFolder: boolean): Promise<void> {
  const input = e.target as HTMLInputElement;
  const all = [...(input.files ?? [])];
  input.value = "";
  if (all.length === 0) return;
  await loadFolder();

  // 폴더를 고르면 경로가 "고른폴더/하위/파일.jpg"로 오므로 맨 앞 폴더 이름은 뺍니다 (CSV에는 그 안쪽 경로를 씀).
  const relOf = (f: File) => (isFolder && f.webkitRelativePath ? f.webkitRelativePath.split("/").slice(1).join("/") : f.name);
  const photos = all.filter((f) => PHOTO_EXT.test(f.name));
  const others = all.length - photos.length;
  const have = new Map((folder?.files ?? []).map((f) => [f.path.toLowerCase(), f.size]));
  const todo = photos.filter((f) => have.get(relOf(f).toLowerCase()) !== f.size);

  const issues: string[] = [];
  if (others > 0) issues.push(`사진이 아닌 파일 ${num(others)}개는 건너뜀 (HEIC는 JPG로 바꿔 주세요)`);
  const totalBytes = todo.reduce((s, f) => s + f.size, 0);
  let done = 0;
  let sent = 0;
  stopUpload = false;

  const box = byId("upload");
  box.hidden = false;
  const draw = () => {
    byId("upLabel").textContent = `올리는 중 ${num(done)} / ${num(todo.length)}장`;
    byId("upBar").style.width = `${todo.length ? Math.round((done / todo.length) * 100) : 100}%`;
    byId("upMeta").textContent = `${fmtBytes(sent)} / ${fmtBytes(totalBytes)} · 이미 있어 건너뜀 ${num(photos.length - todo.length)}장`;
  };
  draw();

  // 동시에 4장씩. 한 장 실패해도 나머지는 계속 올립니다.
  let next = 0;
  const worker = async () => {
    while (!stopUpload && next < todo.length) {
      const f = todo[next++]!;
      const rel = relOf(f);
      try {
        const res = await fetch(`/api/import/files/${rel.split("/").map(encodeURIComponent).join("/")}`, { method: "PUT", body: f });
        if (!res.ok) throw await toApiError(res);
        sent += f.size;
      } catch (err) {
        issues.push(`${rel}: ${errorMessage(err)}`);
      }
      done++;
      draw();
    }
  };
  await Promise.all([worker(), worker(), worker(), worker()]);

  byId("upLabel").textContent = stopUpload ? `멈춤 · ${num(done)} / ${num(todo.length)}장` : `올리기 끝 · ${num(done)}장`;
  showIssues(byId("upIssues"), issues);
  await loadFolder();
  if (lastCsv) void runPreview(lastCsv); // 사진이 바뀌었으니 검사를 다시
}

function showIssues(el: HTMLElement, items: string[]): void {
  el.hidden = items.length === 0;
  el.innerHTML = items.slice(0, 200).map((m) => `<li>${esc(m)}</li>`).join("") + (items.length > 200 ? `<li>…외 ${num(items.length - 200)}건</li>` : "");
}

let confirmClear = false;
byId("clearFolder").addEventListener("click", async (e) => {
  const btn = e.currentTarget as HTMLButtonElement;
  if (!confirmClear) {
    confirmClear = true;
    btn.textContent = "정말 비울까요? 한 번 더 누르면 지웁니다 (DB에 들어간 사진은 그대로)";
    window.setTimeout(() => {
      confirmClear = false;
      btn.textContent = "가져오기 폴더 비우기";
    }, 4000);
    return;
  }
  confirmClear = false;
  btn.textContent = "가져오기 폴더 비우기";
  try {
    await api<void>("DELETE", "/api/import/files");
    toast("가져오기 폴더를 비웠습니다");
    await loadFolder();
  } catch (err) {
    toast(errorMessage(err));
  }
});

/* ---------- 03 검사 ---------- */

let lastCsv: File | null = null;
let lastPreview: Preview | null = null;

byId("pickCsv").addEventListener("change", (e) => {
  const input = e.target as HTMLInputElement;
  const f = input.files?.[0];
  input.value = "";
  if (f) void runPreview(f);
});

async function postCsv(url: string, file: File): Promise<Response> {
  const form = new FormData();
  form.append("file", file);
  try {
    return await fetch(url, { method: "POST", body: form });
  } catch {
    throw new ApiError("서버에 연결할 수 없습니다.", 0, null);
  }
}

async function runPreview(file: File): Promise<void> {
  lastCsv = file;
  byId("csvName").textContent = `${file.name} · ${fmtBytes(file.size)} · 검사 중…`;
  try {
    const res = await postCsv("/api/import/preview", file);
    if (!res.ok) throw await toApiError(res);
    lastPreview = (await res.json()) as Preview;
    byId("csvName").textContent = `${file.name} · ${lastPreview.encoding}`;
    renderPreview(lastPreview);
  } catch (e) {
    byId("csvName").textContent = file.name;
    lastPreview = null;
    byId("preview").innerHTML = `<p class="err">${esc(errorMessage(e))}</p>`;
  }
  renderRun();
}

function renderPreview(p: Preview): void {
  const stat = (label: string, value: string, sub = "") => `<div class="stat"><span class="label">${label}</span><b>${value}</b><small>${sub}</small></div>`;
  const list = (title: string, items: Issue[], total: number, cls: string) =>
    items.length === 0 ? "" : `<div class="issue-block ${cls}"><h3>${title} ${num(total)}건</h3><table class="issues-table"><tbody>
      ${items.map((i) => `<tr><td>${i.row}행</td><td>${esc(i.message)}</td></tr>`).join("")}
      </tbody></table>${total > items.length ? `<p class="meta">앞의 ${items.length}건만 보여 줍니다.</p>` : ""}</div>`;

  byId("preview").innerHTML = `
    <div class="stats">
      ${stat("읽은 행", num(p.rows))}
      ${stat("환자", num(p.patients), `새로 ${num(p.newPatients)}`)}
      ${stat("방문", num(p.visits), `새로 ${num(p.newVisits)}`)}
      ${stat("사진", num(p.photos), "찾음")}
      ${stat("오류 행", num(p.errorRows), p.errorRows ? "가져오지 않음" : "없음")}
    </div>
    ${list("오류", p.errors, Math.max(p.errorRows, p.errors.length), "bad")}
    ${list("주의", p.warnings, p.warnings.length, "")}`;
}

/* ---------- 04 가져오기 ---------- */

function renderRun(): void {
  const box = byId("run");
  const p = lastPreview;
  if (!p || !lastCsv) {
    box.innerHTML = '<p class="meta">03에서 검사를 마치면 여기서 가져올 수 있습니다.</p>';
    return;
  }
  const headerBroken = p.errors.some((e) => e.row === 1);
  const valid = p.rows - p.errorRows;
  if (headerBroken || valid <= 0) {
    box.innerHTML = '<p class="err">가져올 수 있는 행이 없습니다. 03의 오류를 고쳐 다시 검사해 주세요.</p>';
    return;
  }
  box.innerHTML = `<div class="actions">
      <button class="btn primary big" id="go" type="button">${p.errorRows ? `오류 ${num(p.errorRows)}행 빼고 ${num(valid)}행 가져오기` : `${num(valid)}행 가져오기`}</button>
      <span class="meta">환자 ${num(p.newPatients)}명 · 방문 ${num(p.newVisits)}건을 새로 만들고, 사진 ${num(p.photos)}장을 옮깁니다.</span>
    </div>`;
  byId("go").addEventListener("click", () => void start(p.errorRows > 0));
}

async function start(skipInvalid: boolean): Promise<void> {
  if (!lastCsv) return;
  byId<HTMLButtonElement>("go").disabled = true;
  try {
    const res = await postCsv(`/api/import/run?skipInvalid=${skipInvalid}`, lastCsv);
    if (!res.ok) throw await toApiError(res);
    void poll();
  } catch (e) {
    toast(errorMessage(e));
    byId<HTMLButtonElement>("go").disabled = false;
  }
}

async function poll(): Promise<void> {
  let s: Status;
  try {
    s = await api<Status>("GET", "/api/import/status");
  } catch {
    window.setTimeout(() => void poll(), 2000);
    return;
  }
  const box = byId("run");
  if (s.state === "Running") {
    const pct = s.total ? Math.round((s.done / s.total) * 100) : 0;
    box.innerHTML = `<div class="upload"><div class="row-between"><b>가져오는 중 ${pct}%</b><span class="meta">창을 닫아도 서버에서 계속됩니다</span></div>
      <div class="bar"><i style="width:${pct}%"></i></div>
      <p class="meta">환자 ${num(s.done)} / ${num(s.total)} · 사진 ${num(s.photosDone)} / ${num(s.photosTotal)}</p></div>`;
    window.setTimeout(() => void poll(), 1000);
    return;
  }
  if (s.state === "Succeeded" && s.result) {
    const r = s.result;
    box.innerHTML = `<div class="done"><h3>가져오기를 마쳤습니다</h3>
      <div class="stats">
        <div class="stat"><span class="label">새 환자</span><b>${num(r.newPatients)}</b></div>
        <div class="stat"><span class="label">새 방문</span><b>${num(r.newVisits)}</b><small>보완 ${num(r.updatedVisits)}</small></div>
        <div class="stat"><span class="label">사진</span><b>${num(r.photosAdded)}</b><small>이미 있음 ${num(r.photosSkipped)}</small></div>
        <div class="stat"><span class="label">뺀 행</span><b>${num(r.skippedRows)}</b></div>
      </div>
      <p class="meta">원장 화면의 환자 기록과 지난 날짜 목록에서 "이전 기록"으로 보입니다. 가져오기 폴더의 원본은 02에서 비울 수 있습니다.</p></div>`;
    lastCsv = null;
    lastPreview = null;
    void loadFolder();
  } else if (s.state === "Failed") {
    box.innerHTML = `<p class="err">${esc(s.error ?? "가져오기에 실패했습니다.")}</p>`;
    renderRunLater();
  }
}

function renderRunLater(): void {
  if (!lastPreview) return;
  const box = byId("run");
  const again = document.createElement("div");
  box.append(again);
  const p = lastPreview;
  again.innerHTML = `<button class="btn" type="button">다시 시도</button>`;
  again.querySelector("button")!.addEventListener("click", () => void start(p.errorRows > 0));
}

/* ---------- 시작 ---------- */

void loadFolder();
void api<Status>("GET", "/api/import/status").then((s) => {
  if (s.state === "Running") void poll();
});
