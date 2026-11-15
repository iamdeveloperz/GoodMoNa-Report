import "../shared/base.css";
import "../shared/intranet.css";
import { api, errorMessage } from "../shared/api";
import { isManager, setupMasthead } from "../shared/chrome";
import { addMonths, byId, esc, fmtDays, fmtMDW, fmtMonth, fmtRange, formValues, pad, toast, todayIso } from "../shared/format";
import { ROLE_LABEL, type Me } from "../shared/session";
import {
  LEAVE_STATUS, LEAVE_TYPE, PORTION, WEEKDAYS,
  type LeaveCalendar, type LeaveRequest, type MyLeave, type PendingLeave, type StaffBalance,
} from "../shared/types";

/**
 * 연차: 내 잔여(근로기준법 §60, 입사일 기준) · 휴가 신청 · 내 신청 · 팀 달력
 * 대표원장·관리자: 승인 대기(신청자의 잔여와 함께) · 직원별 잔여 · 수동 조정
 */

let me: Me | null = null;
let month = todayIso().slice(0, 7);
let rejecting: number | null = null;

const statusTag = (r: LeaveRequest) =>
  `<span class="tag ${r.status === "Pending" ? "accent" : r.status === "Approved" ? "solid" : "muted"}">${LEAVE_STATUS[r.status]}</span>`;
const kind = (r: { type: LeaveRequest["type"]; portion: LeaveRequest["portion"] }) =>
  `${LEAVE_TYPE[r.type]}${r.portion === "Full" ? "" : ` · ${PORTION[r.portion]}`}`;

/* ---------- 내 연차 · 신청 ---------- */

async function loadMine(): Promise<void> {
  const mine = await api<MyLeave>("GET", "/api/intranet/leave/me");
  const b = mine.balance;
  byId("summary").innerHTML = !b
    ? `<p class="notice">입사일이 등록되지 않아 연차를 계산할 수 없습니다. 대표원장·관리자에게 입사일 등록을 요청해 주세요. 병가·경조·공가는 지금도 신청할 수 있습니다.</p>`
    : `<div class="stats">
        <div class="stat"><b>${fmtDays(b.available)}<small>일</small></b><span>쓸 수 있는 연차</span></div>
        <div class="stat"><b>${fmtDays(b.remaining)}<small>일</small></b><span>남은 연차 (승인 기준)</span></div>
        <div class="stat"><b>${fmtDays(b.used)}<small>일</small></b><span>사용</span></div>
        <div class="stat"><b>${fmtDays(b.pending)}<small>일</small></b><span>승인 대기</span></div>
        <div class="stat"><b>${fmtDays(b.granted)}<small>일</small></b><span>발생 (${b.serviceYears === 0 ? "1년 미만 · 매월 1일" : `근속 ${b.serviceYears}년`})</span></div>
        <div class="stat"><b>${b.adjusted > 0 ? "+" : ""}${fmtDays(b.adjusted)}<small>일</small></b><span>조정 (이월 등)</span></div>
      </div>
      <p class="meta">연차 기간 ${fmtMDW(b.periodStart)} ~ ${fmtMDW(b.periodLastDay)} (입사일 ${mine.hireDate}) · 다음 발생 ${fmtMDW(b.nextGrantDate)} +${b.nextGrantDays}일 · 기간이 끝나면 남은 연차는 사라집니다(미사용 수당 대상).</p>`;

  byId("mine").innerHTML = mine.requests.length === 0
    ? '<p class="empty">최근 1년 동안 신청한 휴가가 없습니다.</p>'
    : `<ul class="list">${mine.requests
        .map((r) => {
          const canCancel = r.status === "Pending" || (r.status === "Approved" && r.startDate > todayIso());
          return `<li><span class="grow"><b>${fmtRange(r.startDate, r.endDate)}</b> · ${kind(r)} · ${fmtDays(r.days)}일${r.reason ? `<br><span class="meta">${esc(r.reason)}</span>` : ""}${
            r.decisionNote ? `<br><span class="meta">${esc(r.decidedByName ?? "")}: ${esc(r.decisionNote)}</span>` : ""}</span>
            ${statusTag(r)}${canCancel ? `<button class="textbtn" type="button" data-cancel="${r.id}">취소</button>` : ""}</li>`;
        })
        .join("")}${mine.adjustments.map((a) => `<li><span class="grow">${fmtMDW(a.effectiveDate)} · 조정 ${a.days > 0 ? "+" : ""}${fmtDays(a.days)}일 <span class="meta">${esc(a.reason)} (${esc(a.createdByName)})</span></span></li>`).join("")}</ul>`;
}

function renderApply(): void {
  const t = todayIso();
  byId("apply").innerHTML = `<form id="applyForm" class="form-grid">
    <label class="field"><span class="label">종류</span><select name="type">${Object.entries(LEAVE_TYPE).map(([k, v]) => `<option value="${k}">${v}${k === "Annual" ? " (차감)" : ""}</option>`).join("")}</select></label>
    <label class="field"><span class="label">단위</span><select name="portion">${Object.entries(PORTION).map(([k, v]) => `<option value="${k}">${v}</option>`).join("")}</select></label>
    <label class="field"><span class="label">시작일</span><input type="date" name="startDate" required value="${t}"></label>
    <label class="field"><span class="label">끝나는 날</span><input type="date" name="endDate" value="${t}"></label>
    <label class="field wide"><span class="label">사유 (선택)</span><input type="text" name="reason" maxlength="200" placeholder="예: 가족 행사"></label>
    <div class="inline wide"><button class="btn primary" type="submit">신청</button><span class="meta">주말·공휴일·휴진일은 일수에서 빠집니다. 반차는 하루만. 승인 전에는 언제든, 승인 후에는 시작 전까지 취소할 수 있습니다.</span></div>
  </form>`;
}

/* ---------- 팀 달력 ---------- */

async function loadCalendar(): Promise<void> {
  const cal = await api<LeaveCalendar>("GET", `/api/intranet/leave/calendar?month=${month}`);
  byId("monthNav").innerHTML = `<button class="textbtn" type="button" data-month="-1">이전</button><b>${fmtMonth(cal.month)}</b><button class="textbtn" type="button" data-month="1">다음</button>`;

  const [y, m] = cal.month.split("-").map(Number) as [number, number];
  const days = new Date(y, m, 0).getDate();
  const lead = new Date(y, m - 1, 1).getDay();
  const holidays = new Map(cal.holidays.map((h) => [h.date, h.name]));
  const today = todayIso();
  const cells: string[] = WEEKDAYS.map((w) => `<div class="h">${w}</div>`);
  for (let i = 0; i < lead; i++) cells.push('<div class="d blank"></div>');
  for (let d = 1; d <= days; d++) {
    const iso = `${cal.month}-${pad(d)}`;
    const weekday = new Date(y, m - 1, d).getDay();
    const hol = holidays.get(iso);
    const off = !cal.workDays.includes(weekday) || !!hol;
    const items = cal.items.filter((i) => i.startDate <= iso && i.endDate >= iso);
    // 같은 직무가 경고 인원 이상 쉬는 근무일
    const byRole = new Map<string, number>();
    items.forEach((i) => byRole.set(i.role, (byRole.get(i.role) ?? 0) + (i.portion === "Full" ? 1 : 0.5)));
    const warnRole = off ? null : [...byRole.entries()].find(([, n]) => n >= cal.warnSameRole)?.[0];
    cells.push(`<div class="d${off ? " off" : ""}${iso === today ? " today" : ""}${warnRole ? " warn" : ""}">
      <span class="n">${d}</span>${hol ? `<span class="hol">${esc(hol)}</span>` : ""}
      ${off ? "" : items.map((i) => `<span class="ev ${i.status}" title="${esc(i.name)} ${kind(i)}">${esc(i.name)} ${i.portion === "Full" ? LEAVE_TYPE[i.type] : PORTION[i.portion]}</span>`).join("")}
      ${warnRole ? `<span class="warn-text">${ROLE_LABEL[warnRole as Me["role"]]} ${byRole.get(warnRole)}명</span>` : ""}</div>`);
  }
  byId("calendar").innerHTML = `<div class="cal">${cells.join("")}</div>
    <p class="meta">회색 칸은 쉬는 날. 흐린 글씨는 승인 대기. 같은 직무가 ${cal.warnSameRole}명 이상 쉬는 근무일은 빨간 테두리로 표시합니다.</p>`;
}

/* ---------- 관리 ---------- */

async function loadPending(): Promise<void> {
  const rows = await api<PendingLeave[]>("GET", "/api/intranet/leave/requests");
  byId("pendingCount").textContent = `${rows.length}건`;
  byId("pending").innerHTML = rows.length === 0
    ? '<p class="empty">승인을 기다리는 휴가가 없습니다.</p>'
    : `<table class="people"><thead><tr><th>신청자</th><th>기간</th><th>종류</th><th>일수</th><th>신청자 잔여</th><th></th></tr></thead><tbody>${rows
        .map(({ request: r, balance: b }) => `<tr>
          <td class="nm"><b>${esc(r.staffName)}</b><small>${ROLE_LABEL[r.role]}${r.department ? ` · ${esc(r.department)}` : ""}${r.reason ? ` · ${esc(r.reason)}` : ""}</small></td>
          <td>${fmtRange(r.startDate, r.endDate)}</td><td>${kind(r)}</td><td>${fmtDays(r.days)}일</td>
          <td>${b ? `${fmtDays(b.remaining)}일${b.remaining < r.days ? ' <span class="late">부족</span>' : ""}` : r.type === "Annual" ? '<span class="late">입사일 없음</span>' : "—"}</td>
          <td><div class="acts">${rejecting === r.id
            ? `<input type="text" data-note="${r.id}" placeholder="반려 사유" maxlength="200"><button class="btn compact danger" type="button" data-act="reject-yes" data-id="${r.id}">반려</button><button class="textbtn" type="button" data-act="reject-no">취소</button>`
            : `<button class="btn compact primary" type="button" data-act="approve" data-id="${r.id}">승인</button><button class="textbtn" type="button" data-act="reject" data-id="${r.id}">반려</button>`}</div></td></tr>`)
        .join("")}</tbody></table>`;
}

async function loadBalances(): Promise<void> {
  const rows = await api<StaffBalance[]>("GET", "/api/intranet/leave/balances");
  byId("balances").innerHTML = `<table class="people"><thead><tr><th>직원</th><th>입사일</th><th>연차 기간</th><th>발생</th><th>조정</th><th>사용</th><th>대기</th><th>남음</th></tr></thead><tbody>${rows
    .map((s) => {
      const b = s.balance;
      return `<tr><td class="nm"><b>${esc(s.name)}</b><small>${ROLE_LABEL[s.role]}${s.department ? ` · ${esc(s.department)}` : ""}</small></td>
        <td>${s.hireDate ?? '<a href="/intranet/admin" class="late">등록 필요</a>'}</td>
        ${b ? `<td class="meta">${fmtMDW(b.periodStart)} ~ ${fmtMDW(b.periodLastDay)}</td><td>${fmtDays(b.granted)}</td><td>${fmtDays(b.adjusted)}</td><td>${fmtDays(b.used)}</td><td>${fmtDays(b.pending)}</td><td><b>${fmtDays(b.remaining)}</b></td>` : '<td colspan="6" class="meta">—</td>'}</tr>`;
    })
    .join("")}</tbody></table>
    <form id="adjustForm" class="form-grid card">
      <h2 class="wide">수동 조정</h2>
      <label class="field"><span class="label">직원</span><select name="staffId">${rows.map((s) => `<option value="${s.staffId}">${esc(s.name)}</option>`).join("")}</select></label>
      <label class="field"><span class="label">일수 (+/−, 0.5 단위)</span><input type="number" name="days" step="0.5" min="-30" max="30" required></label>
      <label class="field"><span class="label">적용일</span><input type="date" name="effectiveDate" value="${todayIso()}"></label>
      <label class="field wide"><span class="label">사유</span><input type="text" name="reason" maxlength="200" required placeholder="예: 전년도 이월 3일, 시스템 도입 전 사용 2일(−2)"></label>
      <div class="inline wide"><button class="btn" type="submit">조정 추가</button><span class="meta">적용일이 속한 그 직원의 연차 기간에 더하거나 뺍니다. 출근율 80% 미만 등 법정 발생과 다른 경우에도 씁니다.</span></div>
    </form>`;
}

async function reload(): Promise<void> {
  const jobs: Promise<void>[] = [loadMine(), loadCalendar()];
  if (isManager(me)) jobs.push(loadPending(), loadBalances());
  const results = await Promise.allSettled(jobs);
  const failed = results.find((r): r is PromiseRejectedResult => r.status === "rejected");
  if (failed) toast(errorMessage(failed.reason));
}

/* ---------- 이벤트 ---------- */

document.addEventListener("submit", async (e) => {
  const form = e.target as HTMLFormElement;
  e.preventDefault();
  const v = formValues(form);
  try {
    if (form.id === "applyForm") {
      const r = await api<LeaveRequest>("POST", "/api/intranet/leave/requests", {
        type: v.type, portion: v.portion, startDate: v.startDate, endDate: v.endDate || v.startDate, reason: v.reason,
      });
      toast(`${fmtRange(r.startDate, r.endDate)} ${fmtDays(r.days)}일 신청했습니다`);
      form.reset();
    } else if (form.id === "adjustForm") {
      await api("POST", "/api/intranet/leave/adjustments", { staffId: Number(v.staffId), days: Number(v.days), reason: v.reason, effectiveDate: v.effectiveDate || null });
      toast("조정했습니다");
    }
    await reload();
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

document.addEventListener("change", (e) => {
  // 시작일을 바꾸면 끝나는 날이 앞서지 않게 맞춤
  const t = e.target as HTMLInputElement;
  if (t.name !== "startDate") return;
  const end = t.form?.elements.namedItem("endDate") as HTMLInputElement | null;
  if (end && (!end.value || end.value < t.value)) end.value = t.value;
});

document.addEventListener("click", async (e) => {
  const el = e.target as HTMLElement;
  const monthBtn = el.closest<HTMLElement>("[data-month]");
  if (monthBtn) {
    month = addMonths(month, Number(monthBtn.dataset.month));
    void loadCalendar().catch((ex) => toast(errorMessage(ex)));
    return;
  }
  const cancel = el.closest<HTMLElement>("[data-cancel]");
  const b = el.closest<HTMLElement>("[data-act]");
  try {
    if (cancel) {
      await api("POST", `/api/intranet/leave/requests/${cancel.dataset.cancel}/cancel`, {});
      toast("취소했습니다");
    } else if (b) {
      const id = Number(b.dataset.id);
      switch (b.dataset.act) {
        case "approve":
          await api("POST", `/api/intranet/leave/requests/${id}/approve`, {});
          toast("승인했습니다");
          break;
        case "reject":
          rejecting = id;
          await loadPending();
          document.querySelector<HTMLInputElement>(`input[data-note="${id}"]`)?.focus();
          return;
        case "reject-no":
          rejecting = null;
          await loadPending();
          return;
        case "reject-yes": {
          const note = document.querySelector<HTMLInputElement>(`input[data-note="${id}"]`)?.value ?? "";
          await api("POST", `/api/intranet/leave/requests/${id}/reject`, { note });
          rejecting = null;
          toast("반려했습니다");
          break;
        }
        default:
          return;
      }
    } else return;
    await reload();
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

void (async () => {
  me = await setupMasthead();
  if (!me) return;
  renderApply();
  await reload();
  if (location.hash) document.getElementById(location.hash.slice(1))?.scrollIntoView();
})();
