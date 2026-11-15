import "../shared/base.css";
import "../shared/intranet.css";
import { ApiError, api, errorMessage } from "../shared/api";
import { isManager, setupMasthead } from "../shared/chrome";
import { addMonths, byId, esc, fmtHM, fmtMDW, fmtMonth, hm, toast, todayIso } from "../shared/format";
import { ROLE_LABEL, type Me } from "../shared/session";
import { DAY_STATUS, LEAVE_TYPE, METHOD, PORTION, type AttendanceDay, type DailyAttendance, type MyAttendance } from "../shared/types";

/**
 * 출퇴근: 오늘 출근·퇴근 버튼 · 이번 달 기록. 입구 QR로 열면(?code=…) 그 코드로 병원에 있음을 확인합니다.
 * 대표원장·관리자: 하루 현황(휴가·기록 없음 포함) · 사유를 남기고 수정 · 입구 QR 화면.
 */

let me: Me | null = null;
let month = todayIso().slice(0, 7);
let code = new URLSearchParams(location.search).get("code");
let editing: number | null = null;

const time = (iso: string | null) => (iso ? fmtHM(iso) : "—");
const statusText = (d: AttendanceDay) => {
  const parts: string[] = [];
  if (d.status !== "None") parts.push(DAY_STATUS[d.status]);
  if (d.leaveType) parts.push(`${LEAVE_TYPE[d.leaveType]}${d.leavePortion !== "Full" ? ` ${PORTION[d.leavePortion!]}` : ""}`);
  if (d.late) parts.push('<span class="late">지각</span>');
  if (d.earlyLeave) parts.push('<span class="late">조퇴</span>');
  return parts.join(" · ");
};

function renderToday(a: MyAttendance): void {
  const t = a.today;
  const v = a.verify;
  const where = code
    ? "입구 QR로 열었습니다. 1분 안에 눌러 주세요."
    : v.onClinicNetwork
      ? "병원 네트워크에 연결되어 있습니다."
      : v.required
        ? `병원 네트워크가 아닙니다${v.ip ? ` (${esc(v.ip)})` : ""}. 병원 와이파이에 연결하거나 입구 화면의 QR을 찍어 주세요.`
        : "위치 확인을 쓰지 않습니다.";
  byId("today").innerHTML = `<div class="today-box">
    <div class="stats">
      <div class="stat"><b>${time(t.checkInAt)}</b><span>출근${t.checkInMethod ? ` · ${METHOD[t.checkInMethod]}` : ""}</span></div>
      <div class="stat"><b>${time(t.checkOutAt)}</b><span>퇴근${t.checkOutMethod ? ` · ${METHOD[t.checkOutMethod]}` : ""}</span></div>
    </div>
    <div class="card">
      <p>${fmtMDW(t.date)} · 근무 ${hm(a.workStart)}~${hm(a.workEnd)}${t.workday ? "" : ` · ${esc(t.holidayName ?? "쉬는 날")}`}${statusText(t) ? ` · ${statusText(t)}` : ""}</p>
      <div class="punch">
        <button class="btn big ${t.checkInAt ? "" : "primary"}" type="button" data-punch="check-in"${t.checkInAt ? " disabled" : ""}>출근</button>
        <button class="btn big ${t.checkInAt ? "primary" : ""}" type="button" data-punch="check-out"${t.checkInAt ? "" : " disabled"}>퇴근${t.checkOutAt ? " (다시)" : ""}</button>
      </div>
      <p class="meta">${where}</p>
    </div></div>`;
}

function renderMonth(a: MyAttendance): void {
  byId("monthNav").innerHTML = `<button class="textbtn" type="button" data-month="-1">이전</button><b>${fmtMonth(a.month)}</b><button class="textbtn" type="button" data-month="1">다음</button>`;
  const rows = a.days.filter((d) => d.workday || d.checkInAt || d.leaveType);
  const worked = a.days.filter((d) => d.checkInAt).length;
  const late = a.days.filter((d) => d.late).length;
  const absent = a.days.filter((d) => d.status === "Absent").length;
  byId("month").innerHTML = `<p class="meta">출근 ${worked}일 · 지각 ${late}회 · 기록 없음 ${absent}일</p>
    <table class="people"><thead><tr><th>날짜</th><th>출근</th><th>퇴근</th><th>상태</th><th>비고</th></tr></thead><tbody>${rows
      .map((d) => `<tr><td>${fmtMDW(d.date)}${d.holidayName ? ` <span class="meta">${esc(d.holidayName)}</span>` : ""}</td><td>${time(d.checkInAt)}</td><td>${time(d.checkOutAt)}</td>
        <td>${statusText(d)}</td><td class="meta">${d.editReason ? `${esc(d.editedByName ?? "")} 수정: ${esc(d.editReason)}` : ""}</td></tr>`)
      .join("")}</tbody></table>`;
}

async function loadMine(): Promise<void> {
  const a = await api<MyAttendance>("GET", `/api/intranet/attendance/me?month=${month}`);
  renderToday(a);
  renderMonth(a);
}

async function loadDaily(): Promise<void> {
  const input = byId<HTMLInputElement>("dailyDate");
  input.value ||= todayIso();
  input.max = todayIso();
  const d = await api<DailyAttendance>("GET", `/api/intranet/attendance/daily?date=${input.value}`);
  const counts = { working: d.rows.filter((r) => r.day.checkInAt).length, leave: d.rows.filter((r) => r.day.status === "Leave").length };
  byId("daily").innerHTML = `<p class="meta">${d.workday ? "근무일" : esc(d.holidayName ?? "쉬는 날")} · 출근 ${counts.working}명 · 휴가 ${counts.leave}명 · 전체 ${d.rows.length}명</p>
    <table class="people"><thead><tr><th>직원</th><th>출근</th><th>퇴근</th><th>상태</th><th></th></tr></thead><tbody>${d.rows
      .map((r) => editing === r.staffId
        ? `<tr><td class="nm"><b>${esc(r.name)}</b><small>${ROLE_LABEL[r.role]}</small></td>
            <td><input type="time" id="editIn" value="${r.day.checkInAt ? fmtHM(r.day.checkInAt) : ""}" aria-label="출근 시각"></td>
            <td><input type="time" id="editOut" value="${r.day.checkOutAt ? fmtHM(r.day.checkOutAt) : ""}" aria-label="퇴근 시각"></td>
            <td><input type="text" id="editReason" placeholder="수정 사유 (필수)" maxlength="200"></td>
            <td><div class="acts"><button class="btn compact primary" type="button" data-act="save" data-id="${r.staffId}">저장</button><button class="textbtn" type="button" data-act="cancel">취소</button></div></td></tr>`
        : `<tr><td class="nm"><b>${esc(r.name)}</b><small>${ROLE_LABEL[r.role]}${r.department ? ` · ${esc(r.department)}` : ""}</small></td>
            <td>${time(r.day.checkInAt)}${r.day.checkInMethod ? `<br><small class="meta">${METHOD[r.day.checkInMethod]}</small>` : ""}</td>
            <td>${time(r.day.checkOutAt)}</td><td>${statusText(r.day)}${r.day.editReason ? `<br><small class="meta">${esc(r.day.editReason)}</small>` : ""}</td>
            <td><div class="acts"><button class="textbtn" type="button" data-act="edit" data-id="${r.staffId}">수정</button></div></td></tr>`)
      .join("")}</tbody></table>
    <p class="meta">시각을 모두 비우고 저장하면 그날 기록을 지웁니다.</p>`;
}

async function punch(kind: string): Promise<void> {
  try {
    await api("POST", `/api/intranet/attendance/${kind}`, { code });
    toast(kind === "check-in" ? "출근을 기록했습니다" : "퇴근을 기록했습니다");
    if (code) {
      code = null; // 한 번 쓴 QR 주소는 지움 (새로고침해도 다시 쓰지 않게)
      history.replaceState(null, "", location.pathname);
    }
  } catch (e) {
    toast(e instanceof ApiError && e.body?.code === "not-at-clinic" ? e.message : errorMessage(e));
  }
  await loadMine();
  if (isManager(me)) await loadDaily();
}

document.addEventListener("click", async (e) => {
  const el = e.target as HTMLElement;
  const p = el.closest<HTMLButtonElement>("[data-punch]");
  if (p) {
    p.disabled = true;
    await punch(p.dataset.punch!);
    return;
  }
  const m = el.closest<HTMLElement>("[data-month]");
  if (m) {
    month = addMonths(month, Number(m.dataset.month));
    await loadMine().catch((ex) => toast(errorMessage(ex)));
    return;
  }
  const b = el.closest<HTMLElement>("[data-act]");
  if (!b) return;
  const id = Number(b.dataset.id);
  try {
    switch (b.dataset.act) {
      case "edit":
        editing = id;
        break;
      case "cancel":
        editing = null;
        break;
      case "save": {
        const date = byId<HTMLInputElement>("dailyDate").value;
        await api("PUT", `/api/intranet/attendance/staff/${id}/${date}`, {
          checkIn: byId<HTMLInputElement>("editIn").value,
          checkOut: byId<HTMLInputElement>("editOut").value,
          reason: byId<HTMLInputElement>("editReason").value,
        });
        editing = null;
        toast("수정했습니다");
        break;
      }
      default:
        return;
    }
    await loadDaily();
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

byId("dailyDate").addEventListener("change", () => {
  editing = null;
  void loadDaily().catch((ex) => toast(errorMessage(ex)));
});

void (async () => {
  me = await setupMasthead();
  if (!me) return;
  try {
    await loadMine();
    if (isManager(me)) await loadDaily();
  } catch (e) {
    toast(errorMessage(e));
  }
})();
