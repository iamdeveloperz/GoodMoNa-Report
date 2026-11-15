import "../shared/base.css";
import "../shared/intranet.css";
import { api, errorMessage } from "../shared/api";
import { setupMasthead } from "../shared/chrome";
import { byId, esc, fmtDate, fmtHM, formValues, toast } from "../shared/format";
import {
  KB_KIND, KB_SOURCE, KB_STATUS,
  type AskLog, type KbImport, type KbImportPreview, type KbItem, type KbKind, type KbOverview,
} from "../shared/types";

/**
 * 자료 관리 (대표원장·관리자): 처리할 질문 → FAQ, 승인 대기 → 승인·반려·고치기, 자료 가져오기(미리 보기 → 초안),
 * 승인된 자료(재검토일·내리기), 내린·반려 자료(다시 승인·지우기), 가져오기 기록.
 */

let items: KbItem[] = [];
let questions: AskLog[] = [];
/** 편집기: 기존 자료 고치기 / 질문에서 FAQ 만들기 / 새 자료 */
let editing: { item?: KbItem; question?: AskLog } | null = null;
let rejecting: number | null = null;
const selected = new Set<number>();

const FORMAT_HELP = `<div class="card">
  <h2>가져오기 형식</h2>
  <pre class="code"># 큰 제목              → 아래 소제목 문서들의 앞 이름
## 소제목              → 문서 한 건 (다음 제목 전까지가 본문)
Q: 질문                → FAQ 한 건 (질문: / Q1. / **Q.** 도 됨)
A: 답                  → 여러 줄 가능
분류: 예약              → 이후 자료의 분류
키워드: 예약 변경, 노쇼  → 바로 앞 자료의 검색 보조어
&lt;!-- 메모 --&gt;           → 무시</pre>
  <p class="meta">제목도 Q/A도 없는 글은 통째로 문서 한 건(제목 = 원본 이름)입니다. 가져온 자료는 모두 <b>초안</b>이고, 승인해야 검색됩니다.</p>
  <h2>초기 수집: Claude · Codex에게 줄 지시문</h2>
  <p class="meta">원장님 영상 대본·기존 안내문을 AI에게 주고 아래 지시문으로 정리하게 한 뒤, 결과를 붙여넣고 "정리한 방법"을 고르세요. 환자 정보가 든 자료는 넣지 마세요.</p>
  <pre class="code" id="promptText">아래 자료는 굿모나의원 CS 직원이 고객 문의에 답할 때 참고할 원문입니다.
다음 형식으로만 정리해 주세요.
- 고객이 실제로 물을 법한 질문은 "Q: 질문" 다음 줄에 "A: 답"으로 씁니다.
- 질문으로 바꾸기 어려운 안내는 "## 제목" 아래 본문으로 씁니다.
- 주제가 바뀌면 "분류: 예약"처럼 분류 줄을 넣습니다(예약·진료시간·주차·결제·서류·시술 안내·사후관리 등).
- 고객이 다르게 말할 만한 표현은 답 아래 "키워드: …" 줄에 쉼표로 적습니다.
- 원문에 없는 내용(가격·기간·효과 등)은 절대 지어내지 말고, 원문이 애매하면 "확인 필요"라고 씁니다.
- 진단·처방처럼 의학적 판단이 필요한 내용은 "원장님 확인이 필요합니다"로 답합니다.
- 환자 이름·연락처는 쓰지 않습니다.

[원문]
(여기에 붙여넣기)</pre>
  <div><button class="btn compact" type="button" id="copyPrompt">지시문 복사</button></div>
</div>`;

function itemMeta(i: KbItem): string {
  return `${esc(i.category)} · ${KB_SOURCE[i.source]}${i.sourceRef ? ` (${esc(i.sourceRef)})` : ""} · ${esc(i.createdByName)} ${fmtDate(i.createdAt)}`;
}

function itemRow(i: KbItem, actions: string, check = false): string {
  return `<li class="kb-row">
    ${check ? `<input type="checkbox" class="pick" data-pick="${i.id}" aria-label="선택"${selected.has(i.id) ? " checked" : ""}>` : ""}
    <details class="hit grow"><summary><span class="tag${i.kind === "Faq" ? " solid" : ""}">${KB_KIND[i.kind]}</span> <b>${esc(i.title)}</b>
      ${i.stale ? '<span class="tag accent">재검토일 지남</span>' : ""}${i.reviewBy && !i.stale ? `<span class="meta">재검토 ${esc(i.reviewBy)}</span>` : ""}
      <br><span class="meta">${itemMeta(i)}</span></summary>
      <div class="prose">${esc(i.body)}</div>${i.keywords ? `<p class="meta">검색 보조어: ${esc(i.keywords)}</p>` : ""}
      ${i.rejectReason ? `<p class="meta">반려 사유: ${esc(i.rejectReason)}</p>` : ""}${i.decidedByName ? `<p class="meta">${KB_STATUS[i.status]}: ${esc(i.decidedByName)} ${fmtDate(i.decidedAt!)}</p>` : ""}
    </details>
    <div class="acts">${actions}</div></li>`;
}

function renderDrafts(): void {
  const drafts = items.filter((i) => i.status === "Draft");
  for (const id of [...selected]) if (!drafts.some((d) => d.id === id)) selected.delete(id);
  byId("drafts").innerHTML = drafts.length === 0
    ? '<p class="empty">승인을 기다리는 자료가 없습니다.</p>'
    : `<div class="inline"><label class="check"><input type="checkbox" id="pickAll"${selected.size === drafts.length ? " checked" : ""}> 모두 선택</label>
        <button class="btn primary compact" type="button" id="approveSelected"${selected.size ? "" : " disabled"}>선택한 ${selected.size}건 승인</button>
        <span class="meta">가져온 자료는 내용이 맞는지 펼쳐 확인한 뒤 승인하세요.</span></div>
      <ul class="list">${drafts.map((i) => itemRow(i, rejecting === i.id
        ? `<input type="text" id="rejectReason" maxlength="200" placeholder="반려 사유"><button class="btn compact" type="button" data-act="reject-yes" data-id="${i.id}">반려</button><button class="textbtn" type="button" data-act="reject-no">취소</button>`
        : `<button class="btn compact" type="button" data-act="approve" data-id="${i.id}">승인</button><button class="textbtn" type="button" data-act="edit" data-id="${i.id}">고치기</button><button class="textbtn danger" type="button" data-act="reject" data-id="${i.id}">반려</button>`, true)).join("")}</ul>`;
}

function renderPublished(): void {
  const f = (document.querySelector<HTMLInputElement>("#filter")?.value ?? "").trim();
  const list = items.filter((i) => i.status === "Published" && (!f || `${i.title} ${i.body} ${i.keywords} ${i.category}`.includes(f)))
    .sort((a, b) => Number(b.stale) - Number(a.stale));
  byId("published").innerHTML = `<div class="inline"><button class="btn compact" type="button" data-act="new">새 자료 쓰기</button></div>` + (list.length === 0
    ? '<p class="empty">승인된 자료가 없습니다.</p>'
    : `<ul class="list">${list.map((i) => itemRow(i, `<button class="textbtn" type="button" data-act="edit" data-id="${i.id}">고치기</button><button class="textbtn danger" type="button" data-act="archive" data-id="${i.id}">내리기</button>`)).join("")}</ul>`);
}

function renderOthers(): void {
  const list = items.filter((i) => i.status === "Archived" || i.status === "Rejected");
  byId("others").innerHTML = list.length === 0
    ? '<p class="empty">없습니다.</p>'
    : `<ul class="list">${list.map((i) => itemRow(i, `<span class="tag muted">${KB_STATUS[i.status]}</span><button class="textbtn" type="button" data-act="approve" data-id="${i.id}">다시 승인</button><button class="textbtn danger" type="button" data-act="delete" data-id="${i.id}">지우기</button>`)).join("")}</ul>`;
}

/** "표현 추가"는 가장 가까운 자료가 어느 정도(30%) 겹칠 때만 권함. 그 아래는 엉뚱한 자료일 가능성이 큼 */
function renderQuestions(): void {
  byId("questions").innerHTML = questions.length === 0
    ? '<p class="empty">처리할 질문이 없습니다.</p>'
    : `<ul class="list">${questions.map((q) => `<li><span class="grow"><b>${esc(q.question)}</b><br>
        <span class="meta">${esc(q.staffName)} · ${fmtDate(q.createdAt)} ${fmtHM(q.createdAt)} · ${q.found ? `찾았지만 "부족함"${q.topItemTitle ? ` (가장 가까운 자료: ${esc(q.topItemTitle)})` : ""}` : `못 찾음${q.topItemTitle ? ` (조금 겹친 자료: ${esc(q.topItemTitle)})` : ""}`}</span></span>
        <div class="acts">${q.topItemId && q.topItemTitle && q.topCoverage >= 0.3 && q.question.length <= 60 ? `<button class="btn compact" type="button" data-act="attach" data-id="${q.id}" data-item="${q.topItemId}" title="이 질문 표현을 그 자료의 검색 보조어로 붙여 다음엔 찾게 합니다">「${esc(q.topItemTitle.length > 18 ? `${q.topItemTitle.slice(0, 18)}…` : q.topItemTitle)}」에 표현 추가</button>` : ""}<button class="btn compact" type="button" data-act="from-question" data-id="${q.id}">FAQ로 만들기</button><button class="textbtn" type="button" data-act="dismiss" data-id="${q.id}">닫기</button></div></li>`).join("")}</ul>`;
}

function renderEditor(): void {
  const box = byId("editor");
  if (!editing) {
    box.innerHTML = "";
    return;
  }
  const i = editing.item;
  const q = editing.question;
  const kind: KbKind = i?.kind ?? "Faq";
  const heading = i ? "자료 고치기" : q ? "질문으로 FAQ 만들기" : "새 자료";
  box.innerHTML = `<form class="card" id="itemForm">
    <h2>${heading}</h2>${q ? `<p class="meta">${esc(q.staffName)}님의 질문: "${esc(q.question)}" — 다듬어서 질문으로 쓰세요. 저장하면 이 질문은 처리됨으로 바뀝니다.</p>` : ""}
    <div class="form-grid">
      <label class="field"><span class="label">종류</span><select name="kind"><option value="Faq"${kind === "Faq" ? " selected" : ""}>FAQ</option><option value="Doc"${kind === "Doc" ? " selected" : ""}>문서</option></select></label>
      <label class="field"><span class="label">분류</span><input type="text" name="category" maxlength="20" value="${esc(i?.category ?? "")}" placeholder="일반"></label>
      <label class="field"><span class="label">재검토일 (가격·이벤트 등)</span><input type="date" name="reviewBy" value="${esc(i?.reviewBy ?? "")}"></label>
      <label class="field wide"><span class="label">질문 · 제목</span><input type="text" name="title" maxlength="200" required value="${esc(i?.title ?? q?.question ?? "")}"></label>
      <label class="field wide"><span class="label">답 · 본문</span><textarea name="body" rows="6" maxlength="4000" required>${esc(i?.body ?? "")}</textarea></label>
      <label class="field wide"><span class="label">검색 보조어 (고객이 다르게 말하는 표현, 쉼표로)</span><input type="text" name="keywords" maxlength="200" value="${esc(i?.keywords ?? "")}"></label>
    </div>
    ${i?.status === "Published" ? '<p class="meta">승인된 자료를 고치면 고친 사람이 다시 승인한 것으로 기록됩니다.</p>' : `<label class="check"><input type="checkbox" name="publish" checked> 저장하면서 바로 승인</label>`}
    <div class="inline"><button class="btn primary" type="submit">저장</button><button class="textbtn" type="button" data-act="cancel-edit">취소</button></div>
  </form>`;
  box.scrollIntoView({ block: "start" });
  box.querySelector<HTMLTextAreaElement>(q ? "textarea" : "input[name=title]")?.focus();
}

async function load(): Promise<void> {
  const [overview, list, open, imports] = await Promise.all([
    api<KbOverview>("GET", "/api/intranet/kb/overview"),
    api<KbItem[]>("GET", "/api/intranet/kb/items"),
    api<AskLog[]>("GET", "/api/intranet/kb/questions"),
    api<KbImport[]>("GET", "/api/intranet/kb/imports"),
  ]);
  items = list;
  questions = open;
  const stat = (n: number, label: string, warn = false) => `<div class="stat"><b${warn && n ? ' class="warn-text"' : ""}>${n}<small>건</small></b><span>${label}</span></div>`;
  byId("stats").innerHTML = stat(overview.openQuestions, "처리할 질문", true) + stat(overview.drafts, "승인 대기", true) + stat(overview.published, "승인된 자료")
    + stat(overview.stale, "재검토일 지남", true)
    + `<div class="stat"><b class="small-b">${overview.assistant.enabled ? "켜짐" : "꺼짐"}</b><span>AI 답변${overview.assistant.model ? ` · ${esc(overview.assistant.model)}` : " · 검색만"}</span></div>`;
  byId("imports").innerHTML = imports.length === 0
    ? '<p class="empty">아직 가져온 자료가 없습니다.</p>'
    : `<ul class="list">${imports.map((b) => `<li><span class="grow"><b>${esc(b.name)}</b> <span class="meta">${b.itemCount}건 · 정리: ${esc(b.origin)}</span></span><span class="meta">${esc(b.createdByName)} ${fmtDate(b.createdAt)} ${fmtHM(b.createdAt)}</span></li>`).join("")}</ul>`;
  renderQuestions();
  renderDrafts();
  renderPublished();
  renderOthers();
}

function importBody(): Record<string, string> {
  const v = formValues(byId<HTMLFormElement>("importForm"));
  return { text: v.text ?? "", name: v.name ?? "", origin: v.origin ?? "직접", category: v.category ?? "" };
}

async function preview(): Promise<void> {
  const p = await api<KbImportPreview>("POST", "/api/intranet/kb/import/preview", importBody());
  byId("preview").innerHTML = `<div class="card"><h2>미리 보기 · ${p.items.length}건 (아직 저장 안 됨)</h2>
    ${p.warnings.map((w) => `<p class="notice">${esc(w)}</p>`).join("")}
    <table class="people"><thead><tr><th>종류</th><th>분류</th><th>질문 · 제목</th><th>답 · 본문</th></tr></thead><tbody>
    ${p.items.map((d) => `<tr><td>${KB_KIND[d.kind]}</td><td>${esc(d.category)}</td><td>${esc(d.title)}${d.keywords ? `<br><small class="meta">${esc(d.keywords)}</small>` : ""}</td><td class="clip">${esc(d.body.length > 160 ? `${d.body.slice(0, 160)}…` : d.body)}</td></tr>`).join("")}
    </tbody></table></div>`;
}

document.addEventListener("submit", async (e) => {
  const form = e.target as HTMLFormElement;
  e.preventDefault();
  try {
    if (form.id === "importForm") {
      const r = await api<{ items: KbItem[]; warnings: string[] }>("POST", "/api/intranet/kb/import", importBody());
      form.reset();
      byId("preview").innerHTML = "";
      r.items.forEach((i) => selected.add(i.id));
      toast(`초안 ${r.items.length}건을 만들었습니다. 승인 대기에서 확인해 주세요.`);
      await load();
      byId("drafts").scrollIntoView({ block: "start" });
    } else if (form.id === "itemForm" && editing) {
      const v = formValues(form);
      const body = { kind: v.kind, title: v.title, body: v.body, category: v.category, keywords: v.keywords, reviewBy: v.reviewBy || null, publish: v.publish === "on" };
      if (editing.item) await api("PUT", `/api/intranet/kb/items/${editing.item.id}`, body);
      else await api("POST", "/api/intranet/kb/items", { ...body, fromQuestionId: editing.question?.id ?? null });
      editing = null;
      renderEditor();
      toast("저장했습니다");
      await load();
    }
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

document.addEventListener("change", async (e) => {
  const el = e.target as HTMLInputElement;
  if (el.dataset.pick) {
    if (el.checked) selected.add(Number(el.dataset.pick));
    else selected.delete(Number(el.dataset.pick));
    renderDrafts();
  } else if (el.id === "pickAll") {
    items.filter((i) => i.status === "Draft").forEach((i) => (el.checked ? selected.add(i.id) : selected.delete(i.id)));
    renderDrafts();
  } else if (el.id === "file" && el.files?.[0]) {
    const file = el.files[0];
    const form = byId<HTMLFormElement>("importForm");
    form.querySelector<HTMLTextAreaElement>("textarea[name=text]")!.value = await file.text();
    form.querySelector<HTMLInputElement>("input[name=name]")!.value = file.name;
    await preview().catch((ex) => toast(errorMessage(ex)));
  }
});

document.addEventListener("input", (e) => {
  if ((e.target as HTMLElement).id === "filter") renderPublished();
});

document.addEventListener("click", async (e) => {
  const el = (e.target as HTMLElement).closest<HTMLElement>("button");
  if (!el) return;
  const id = Number(el.dataset.id);
  try {
    if (el.id === "previewBtn") await preview();
    else if (el.id === "formatBtn") {
      const box = byId("format");
      box.innerHTML = FORMAT_HELP;
      box.hidden = !box.hidden;
    } else if (el.id === "copyPrompt") {
      await navigator.clipboard.writeText(byId("promptText").textContent ?? "").then(() => toast("복사했습니다"), () => toast("직접 선택해 복사해 주세요."));
    } else if (el.id === "approveSelected") {
      const r = await api<{ approved: number }>("POST", "/api/intranet/kb/approve", { ids: [...selected] });
      selected.clear();
      toast(`${r.approved}건을 승인했습니다`);
      await load();
    }
    switch (el.dataset.act) {
      case "approve":
        await api("POST", `/api/intranet/kb/items/${id}/approve`);
        toast("승인했습니다. 이제 검색됩니다.");
        await load();
        break;
      case "reject":
        rejecting = id;
        renderDrafts();
        document.getElementById("rejectReason")?.focus();
        break;
      case "reject-no":
        rejecting = null;
        renderDrafts();
        break;
      case "reject-yes":
        await api("POST", `/api/intranet/kb/items/${id}/reject`, { reason: (document.getElementById("rejectReason") as HTMLInputElement).value });
        rejecting = null;
        toast("반려했습니다");
        await load();
        break;
      case "archive":
        await api("POST", `/api/intranet/kb/items/${id}/archive`);
        toast("내렸습니다. 더 이상 검색되지 않습니다.");
        await load();
        break;
      case "delete":
        await api("DELETE", `/api/intranet/kb/items/${id}`);
        toast("지웠습니다");
        await load();
        break;
      case "edit":
        editing = { item: items.find((i) => i.id === id) };
        renderEditor();
        break;
      case "new":
        editing = {};
        renderEditor();
        break;
      case "from-question":
        editing = { question: questions.find((q) => q.id === id) };
        renderEditor();
        break;
      case "attach":
        await api("POST", `/api/intranet/kb/questions/${id}/attach`, { itemId: Number(el.dataset.item) });
        toast("검색 보조어로 붙였습니다. 다음엔 이 표현으로도 찾습니다.");
        await load();
        break;
      case "dismiss":
        await api("POST", `/api/intranet/kb/questions/${id}/dismiss`);
        await load();
        break;
      case "cancel-edit":
        editing = null;
        renderEditor();
        break;
    }
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

void (async () => {
  if (!(await setupMasthead())) return;
  await load().catch((e) => toast(errorMessage(e)));
})();
