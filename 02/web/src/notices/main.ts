import "../shared/base.css";
import "../shared/intranet.css";
import { api, errorMessage } from "../shared/api";
import { isManager, setupMasthead } from "../shared/chrome";
import { byId, esc, fmtDate, fmtHM, toast } from "../shared/format";
import { ROLE_LABEL, type Me } from "../shared/session";
import type { Notice, NoticeListItem, NoticeReader } from "../shared/types";

/** 공지: 목록 | 본문. 주소 #id로 바로 열립니다. 관리자는 쓰기·고치기·지우기·읽음 현황. */

let me: Me | null = null;
let notices: NoticeListItem[] = [];
let current: Notice | null = null;
let editing: Notice | "new" | null = null;
let confirmDelete = false;

const currentId = (): number => Number(location.hash.slice(1)) || 0;

function renderList(): void {
  byId("list").innerHTML = notices.length === 0
    ? '<p class="empty">아직 공지가 없습니다.</p>'
    : `<ul class="list">${notices
        .map((n) => `<li${n.id === currentId() ? ' class="current"' : ""}>
          <span class="grow"><a href="#${n.id}">${n.read ? "" : '<span class="unread-dot" aria-label="안 읽음"></span>'}${esc(n.title)}</a><br>
          <span class="meta">${esc(n.authorName)} · ${fmtDate(n.createdAt)}${isManager(me) ? ` · 읽음 ${n.readCount}/${n.targetCount}` : ""}</span></span>
          ${n.required ? '<span class="tag accent">필독</span>' : ""}</li>`)
        .join("")}</ul>`;
}

function renderDetail(): void {
  const box = byId("detail");
  const n = current;
  if (!n) {
    box.innerHTML = notices.length ? '<p class="muted">왼쪽에서 공지를 고르세요.</p>' : "";
    return;
  }
  const manage = isManager(me)
    ? `<div class="inline">
        <button class="textbtn" type="button" data-act="edit">고치기</button>
        ${confirmDelete
          ? '<button class="btn compact danger solid" type="button" data-act="delete-yes">지우기 확인</button><button class="textbtn" type="button" data-act="delete-no">취소</button>'
          : '<button class="textbtn danger" type="button" data-act="delete">지우기</button>'}
      </div><div id="reads"></div>`
    : "";
  box.innerHTML = `<div class="inline">${n.required ? '<span class="tag accent">필독</span>' : ""}<span class="meta">${esc(n.authorName)} · ${fmtDate(n.createdAt)} ${fmtHM(n.createdAt)}${n.updatedAt ? ` · 수정 ${fmtDate(n.updatedAt)}` : ""}</span></div>
    <h2>${esc(n.title)}</h2>
    <div class="prose">${esc(n.body)}</div>
    ${n.required ? `<p class="meta">${n.readAt ? `${fmtDate(n.readAt)} ${fmtHM(n.readAt)}에 확인했습니다.` : "확인 처리 중…"}</p>` : ""}
    ${manage}`;
  if (isManager(me)) void loadReads(n.id);
}

async function loadReads(id: number): Promise<void> {
  try {
    const r = await api<{ read: NoticeReader[]; unread: NoticeReader[] }>("GET", `/api/intranet/notices/${id}/reads`);
    const who = (list: NoticeReader[]) => list.map((p) => `${esc(p.name)}<small class="meta"> ${ROLE_LABEL[p.role]}</small>`).join(", ") || "없음";
    const el = document.getElementById("reads");
    if (el) el.innerHTML = `<div class="card"><h2>읽음 현황</h2><p><b>안 읽음 ${r.unread.length}명</b> — ${who(r.unread)}</p><p class="meta">읽음 ${r.read.length}명 — ${who(r.read)}</p></div>`;
  } catch {
    /* 현황은 부가 정보 */
  }
}

function renderEditor(): void {
  const box = byId("editor");
  if (!editing) {
    box.innerHTML = "";
    return;
  }
  const n = editing === "new" ? null : editing;
  box.innerHTML = `<form class="card" id="noticeForm">
    <h2>${n ? "공지 고치기" : "새 공지"}</h2>
    <label class="field"><span class="label">제목</span><input type="text" name="title" maxlength="100" required value="${esc(n?.title ?? "")}"></label>
    <label class="field"><span class="label">내용</span><textarea name="body" rows="8" maxlength="5000" required>${esc(n?.body ?? "")}</textarea></label>
    <label class="check"><input type="checkbox" name="required"${n?.required ? " checked" : ""}> 필독 (홈 위에 고정, 읽음 확인. 필독 공지의 내용을 고치면 다시 확인받습니다)</label>
    <div class="inline"><button class="btn primary" type="submit">${n ? "저장" : "올리기"}</button><button class="textbtn" type="button" data-act="cancel-edit">취소</button></div>
  </form>`;
  box.querySelector<HTMLInputElement>("input[name=title]")?.focus();
}

async function loadList(): Promise<void> {
  notices = await api<NoticeListItem[]>("GET", "/api/intranet/notices");
  renderList();
}

async function open(): Promise<void> {
  confirmDelete = false;
  const id = currentId();
  if (!id) {
    current = null;
    renderDetail();
    return;
  }
  try {
    current = await api<Notice>("GET", `/api/intranet/notices/${id}`);
    renderDetail();
    if (!current.readAt) {
      await api("POST", `/api/intranet/notices/${id}/read`);
      current.readAt = new Date().toISOString();
      renderDetail();
      await loadList();
    } else renderList();
  } catch (e) {
    current = null;
    renderDetail();
    toast(errorMessage(e));
  }
}

document.addEventListener("submit", async (e) => {
  const form = e.target as HTMLFormElement;
  if (form.id !== "noticeForm") return;
  e.preventDefault();
  const data = new FormData(form);
  const body = { title: String(data.get("title") ?? ""), body: String(data.get("body") ?? ""), required: data.get("required") === "on" };
  try {
    const saved = editing === "new"
      ? await api<Notice>("POST", "/api/intranet/notices", body)
      : await api<Notice>("PUT", `/api/intranet/notices/${(editing as Notice).id}`, body);
    editing = null;
    renderEditor();
    toast("저장했습니다");
    await loadList();
    if (location.hash === `#${saved.id}`) await open();
    else location.hash = String(saved.id);
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

document.addEventListener("click", async (e) => {
  const act = (e.target as HTMLElement).closest<HTMLElement>("[data-act]")?.dataset.act;
  if (!act) return;
  switch (act) {
    case "edit":
      editing = current;
      renderEditor();
      break;
    case "cancel-edit":
      editing = null;
      renderEditor();
      break;
    case "delete":
      confirmDelete = true;
      renderDetail();
      break;
    case "delete-no":
      confirmDelete = false;
      renderDetail();
      break;
    case "delete-yes":
      try {
        await api("DELETE", `/api/intranet/notices/${current!.id}`);
        toast("지웠습니다");
        location.hash = "";
        await loadList();
      } catch (ex) {
        toast(errorMessage(ex));
      }
      break;
  }
});

byId("newBtn").addEventListener("click", () => {
  editing = "new";
  renderEditor();
});
window.addEventListener("hashchange", () => void open());

void (async () => {
  me = await setupMasthead();
  if (!me) return;
  try {
    await loadList();
    if (!currentId() && notices[0]) history.replaceState(null, "", `#${notices[0].id}`);
    await open();
  } catch (e) {
    toast(errorMessage(e));
  }
})();
