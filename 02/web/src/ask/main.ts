import "../shared/base.css";
import "../shared/intranet.css";
import { api, errorMessage } from "../shared/api";
import { setupMasthead } from "../shared/chrome";
import { byId, esc, fmtDate, fmtHM, formValues, toast } from "../shared/format";
import { KB_KIND, KB_STATUS, type AskHit, type AskLog, type AskResult, type AssistantStatus, type KbItem } from "../shared/types";

/**
 * CS 도우미 (모든 직원): 질문 → AI 답변(켜져 있으면) · FAQ · 문서. 답 복사, 도움됨/부족함, 내 최근 질문, 자료 제안, 전체 자료.
 * 근거를 못 찾은 질문은 서버가 관리자에게 넘깁니다.
 */

let result: AskResult | null = null;
let all: KbItem[] = [];
const copyTexts = new Map<string, string>();

/** [1] 같은 근거 번호를 그 자료로 가는 링크로 */
function withCitations(text: string, sources: { n: number; itemId: number }[]): string {
  return esc(text).replace(/\[(\d{1,2})\]/g, (m, n) => {
    const s = sources.find((x) => x.n === Number(n));
    return s ? `<a class="cite" href="#hit-${s.itemId}">[${n}]</a>` : m;
  });
}

function hitHtml(h: AskHit, open: boolean): string {
  copyTexts.set(`hit-${h.id}`, h.kind === "Faq" ? h.body : `${h.title}\n${h.body}`);
  return `<details class="hit" id="hit-${h.id}"${open ? " open" : ""}>
    <summary><span class="tag${h.kind === "Faq" ? " solid" : ""}">${KB_KIND[h.kind]}</span> <b>${esc(h.title)}</b>
      <span class="meta">${esc(h.category)}</span>${h.stale ? ' <span class="tag accent">재검토일 지남</span>' : ""}
      ${open ? "" : `<span class="snippet">${esc(h.snippet)}</span>`}</summary>
    <div class="prose">${esc(h.body)}</div>
    <div class="inline"><button class="textbtn" type="button" data-copy="hit-${h.id}">답 복사</button>
      ${h.stale ? `<span class="meta">재검토일 ${esc(h.reviewBy)} — 바뀐 내용이 있는지 데스크에 확인하세요</span>` : ""}</div>
  </details>`;
}

function renderResult(): void {
  const box = byId("result");
  const r = result;
  copyTexts.clear();
  if (!r) {
    box.innerHTML = "";
    return;
  }
  const parts: string[] = [];
  if (r.answer) {
    copyTexts.set("answer", r.answer.text.replace(/\s*\[\d{1,2}\]/g, ""));
    parts.push(`<section class="answer"><div class="label">AI 답변 · 승인된 자료만 근거로 씀</div>
      <p class="prose">${withCitations(r.answer.text, r.answer.sources)}</p>
      <div class="inline"><button class="btn compact" type="button" data-copy="answer">응대 문구 복사</button>
        <span class="meta">근거: ${r.answer.sources.map((s) => `<a href="#hit-${s.itemId}">[${s.n}] ${esc(s.title)}</a>`).join(" · ")}</span></div></section>`);
  }
  if (r.note) parts.push(`<p class="notice">${esc(r.note)}</p>`);
  // 더 잘 맞는 쪽(FAQ·문서)을 위로, 그 첫 자료는 펼쳐 둠 (AI 답변이 없을 때)
  const best = (hs: AskHit[]) => hs[0]?.coverage ?? -1;
  const sections = [{ title: "FAQ", hits: r.faqs }, { title: "안내 문서", hits: r.docs }].filter((s) => s.hits.length).sort((a, b) => best(b.hits) - best(a.hits));
  sections.forEach((s, si) => parts.push(`<div class="hits"><h3>${s.title}</h3>${s.hits.map((h, i) => hitHtml(h, si === 0 && i === 0 && !r.answer)).join("")}</div>`));
  if (r.related.length) parts.push(`<div class="hits"><h3>관련 있을 수도 있는 자료</h3>${r.related.map((h) => hitHtml(h, false)).join("")}</div>`);
  if (r.found)
    parts.push(`<div class="inline feedback" id="feedback"><span class="meta">이 결과로 응대할 수 있나요?</span>
      <button class="btn compact" type="button" data-helpful="true">도움됨</button><button class="btn compact" type="button" data-helpful="false">부족함</button></div>`);
  box.innerHTML = parts.join("");
}

function renderMine(list: AskLog[]): void {
  const state = (l: AskLog): string =>
    l.followUp === "Open" ? '<span class="tag accent">관리자 확인 중</span>'
    : l.followUp === "Resolved" ? '<span class="tag">FAQ로 정리됨</span>'
    : l.found ? '<span class="tag muted">찾음</span>' : '<span class="tag muted">닫힘</span>';
  byId("mine").innerHTML = list.length === 0
    ? '<p class="empty">아직 질문이 없습니다.</p>'
    : `<ul class="list">${list.map((l) => `<li><span class="grow"><button class="linkish" type="button" data-again="${esc(l.question)}">${esc(l.question)}</button><br>
        <span class="meta">${fmtDate(l.createdAt)} ${fmtHM(l.createdAt)}${l.answered ? " · AI 답변" : ""}</span></span>${state(l)}</li>`).join("")}</ul>`;
}

function renderProposals(list: KbItem[]): void {
  byId("proposals").innerHTML = list.length === 0 ? "" : `<h3 class="sub">내 제안</h3><ul class="list">${list
    .map((i) => `<li><span class="grow">${esc(i.title)}${i.rejectReason ? `<br><span class="meta">반려: ${esc(i.rejectReason)}</span>` : ""}</span>
      <span class="tag${i.status === "Published" ? "" : " muted"}">${KB_STATUS[i.status]}</span></li>`)
    .join("")}</ul>`;
}

function renderAll(): void {
  const f = (document.querySelector<HTMLInputElement>("#filter")?.value ?? "").trim();
  const list = f ? all.filter((i) => `${i.title} ${i.body} ${i.keywords} ${i.category}`.includes(f)) : all;
  const groups = new Map<string, KbItem[]>();
  list.forEach((i) => groups.set(i.category, [...(groups.get(i.category) ?? []), i]));
  byId("all").innerHTML = list.length === 0
    ? `<p class="empty">${all.length ? "맞는 자료가 없습니다." : "아직 승인된 자료가 없습니다. 관리자가 자료를 가져와 승인하면 여기에 보입니다."}</p>`
    : [...groups].map(([cat, items]) => `<h3 class="sub">${esc(cat)} <small class="meta">${items.length}</small></h3>
        ${items.map((i) => `<details class="hit"><summary><span class="tag${i.kind === "Faq" ? " solid" : ""}">${KB_KIND[i.kind]}</span> <b>${esc(i.title)}</b>${i.stale ? ' <span class="tag accent">재검토일 지남</span>' : ""}</summary><div class="prose">${esc(i.body)}</div></details>`).join("")}`).join("");
}

async function loadSide(): Promise<void> {
  const [mine, proposals] = await Promise.all([
    api<AskLog[]>("GET", "/api/intranet/ask/mine"),
    api<KbItem[]>("GET", "/api/intranet/kb/items?mine=true"),
  ]);
  renderMine(mine);
  renderProposals(proposals);
}

async function ask(question: string): Promise<void> {
  const btn = document.querySelector<HTMLButtonElement>("#askForm button[type=submit]")!;
  btn.disabled = true;
  btn.textContent = "찾는 중…";
  try {
    result = await api<AskResult>("POST", "/api/intranet/ask", { question });
    renderResult();
    await loadSide();
  } catch (e) {
    toast(errorMessage(e));
  } finally {
    btn.disabled = false;
    btn.textContent = "찾기";
  }
}

async function copy(text: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    toast("복사했습니다");
  } catch {
    toast("복사하지 못했습니다. 글을 직접 선택해 복사해 주세요.");
  }
}

document.addEventListener("submit", async (e) => {
  const form = e.target as HTMLFormElement;
  e.preventDefault();
  const v = formValues(form);
  if (form.id === "askForm") await ask(v.question ?? "");
  else if (form.id === "proposeForm") {
    try {
      await api("POST", "/api/intranet/kb/items", { kind: "Faq", title: v.title, body: v.body });
      form.reset();
      toast("제안했습니다. 관리자가 확인하면 검색됩니다.");
      await loadSide();
    } catch (ex) {
      toast(errorMessage(ex));
    }
  }
});

document.addEventListener("click", async (e) => {
  const el = (e.target as HTMLElement).closest<HTMLElement>("[data-copy],[data-helpful],[data-again]");
  if (!el) return;
  if (el.dataset.copy) await copy(copyTexts.get(el.dataset.copy) ?? "");
  else if (el.dataset.helpful && result) {
    try {
      await api("POST", `/api/intranet/ask/${result.logId}/feedback`, { helpful: el.dataset.helpful === "true" });
      byId("feedback").innerHTML = `<span class="meta">${el.dataset.helpful === "true" ? "고맙습니다." : "관리자에게 전달했습니다. 자료를 보완하겠습니다."}</span>`;
      await loadSide();
    } catch (ex) {
      toast(errorMessage(ex));
    }
  } else if (el.dataset.again) {
    const input = document.querySelector<HTMLInputElement>("#askForm input[name=question]")!;
    input.value = el.dataset.again;
    window.scrollTo({ top: 0 });
    await ask(el.dataset.again);
  }
});

document.addEventListener("input", (e) => {
  if ((e.target as HTMLElement).id === "filter") renderAll();
});

void (async () => {
  if (!(await setupMasthead())) return;
  document.querySelector<HTMLInputElement>("#askForm input[name=question]")?.focus();
  try {
    const [status, items] = await Promise.all([
      api<AssistantStatus>("GET", "/api/intranet/ask/status"),
      api<KbItem[]>("GET", "/api/intranet/kb/items"),
      loadSide(),
    ]);
    byId("mode").textContent = status.enabled ? `AI 답변 켜짐 · 서버 PC 모델 ${status.model ?? ""}` : "AI 답변 꺼짐 · 승인된 자료 검색만";
    all = items;
    renderAll();
  } catch (e) {
    toast(errorMessage(e));
  }
})();
