import "../shared/base.css";
import "../shared/intranet.css";
import { api, errorMessage } from "../shared/api";
import { setupMasthead } from "../shared/chrome";
import { byId, esc, fmtMDW, formValues, hm, toast } from "../shared/format";
import { WEEKDAYS, type Holiday, type Settings } from "../shared/types";

/** 설정 (대표원장·관리자): 근무 요일·시간, 같은 직무 휴가 경고 인원, 출퇴근 위치 확인, 쉬는 날(공휴일·휴진일). */

let settings: Settings | null = null;
/** 시각 입력 "09:00" → 서버 형식 "09:00:00" */
const seconds = (t: string | undefined): string => (t && t.length === 5 ? `${t}:00` : t ?? "");
let year = new Date().getFullYear();

function renderWork(s: Settings): void {
  byId("work").innerHTML = `<form id="workForm" class="form-grid">
    <fieldset class="field wide plain"><legend class="label">근무 요일 (연차 일수는 근무 요일만 셉니다)</legend>
      <div class="inline">${[1, 2, 3, 4, 5, 6, 0].map((d) => `<label class="check"><input type="checkbox" name="d${d}"${s.workDays.includes(d) ? " checked" : ""}> ${WEEKDAYS[d]}</label>`).join("")}</div></fieldset>
    <label class="field"><span class="label">근무 시작 (이후 출근은 지각)</span><input type="time" name="workStart" value="${hm(s.workStart)}" required></label>
    <label class="field"><span class="label">근무 끝 (이전 퇴근은 조퇴)</span><input type="time" name="workEnd" value="${hm(s.workEnd)}" required></label>
    <label class="field"><span class="label">같은 직무 동시 휴가 경고 (명)</span><input type="number" name="leaveWarnSameRole" min="1" max="20" value="${s.leaveWarnSameRole}" required></label>
    <div><button class="btn primary" type="submit">저장</button></div>
  </form>`;
}

function renderVerify(s: Settings): void {
  const ipKnown = s.yourIp && !s.yourIp.startsWith("127.") && s.yourIp !== "::1";
  byId("verify").innerHTML = `<form id="verifyForm" class="form-grid">
    <label class="check wide"><input type="checkbox" name="attendanceVerify"${s.attendanceVerify ? " checked" : ""}> 병원에서만 출퇴근 기록 (병원 네트워크 또는 입구 QR)</label>
    <label class="field wide"><span class="label">병원 네트워크 공인 IP · 대역 (줄마다 하나)</span>
      <textarea name="attendanceNetworks" rows="3" placeholder="203.0.113.7&#10;203.0.113.0/24">${esc(s.attendanceNetworks.join("\n"))}</textarea></label>
    <div class="inline wide"><button class="btn primary" type="submit">저장</button>
      ${ipKnown ? `<button class="textbtn" type="button" id="addMyIp">지금 이 기기의 IP 넣기 (${esc(s.yourIp!)})</button>` : ""}
      <a class="textbtn" href="/intranet/attendance/kiosk">입구 QR 화면 열기</a></div>
  </form>
  <p class="meta">병원 와이파이에 연결된 기기에서 이 화면을 열고 "지금 이 기기의 IP 넣기"를 누르면 병원 공인 IP가 들어갑니다. 공인 IP가 바뀌는 회선이면 입구 QR을 함께 쓰세요.
  입구 QR 화면은 입구 태블릿에서 관리자 계정으로 열어 두면 30초마다 코드가 바뀝니다. 확인을 끄면 어디서든 기록되고 "확인 없음"으로 표시됩니다.</p>`;
}

async function loadHolidays(): Promise<void> {
  const list = await api<Holiday[]>("GET", `/api/intranet/holidays?year=${year}`);
  byId("yearNav").innerHTML = `<button class="textbtn" type="button" data-year="-1">이전</button><b>${year}년</b><button class="textbtn" type="button" data-year="1">다음</button>`;
  byId("holidays").innerHTML = `${list.length === 0 ? '<p class="empty">등록된 쉬는 날이 없습니다. 내장 공휴일은 2026~2027년만 들어 있습니다.</p>' : `<ul class="list">${list
      .map((h) => `<li><span class="grow"><b>${fmtMDW(h.date)}</b> ${esc(h.name)} ${h.builtIn ? '<span class="tag muted">공휴일</span>' : '<span class="tag">병원</span>'}</span>
        <button class="textbtn danger" type="button" data-remove="${h.date}">지우기</button></li>`)
      .join("")}</ul>`}
    <form id="holidayForm" class="form-grid">
      <label class="field"><span class="label">날짜</span><input type="date" name="date" required></label>
      <label class="field"><span class="label">이름</span><input type="text" name="name" maxlength="40" required placeholder="병원 휴진 / 임시공휴일"></label>
      <div><button class="btn" type="submit">추가</button></div>
    </form>
    <p class="meta">공휴일은 정부 발표(임시공휴일·대체공휴일)에 맞춰 확인해 주세요. 근로자의 날에 근무하는 병원이면 지우면 됩니다. 쉬는 날은 연차 일수에서 빠집니다.</p>`;
}

async function load(): Promise<void> {
  settings = await api<Settings>("GET", "/api/intranet/settings");
  renderWork(settings);
  renderVerify(settings);
  await loadHolidays();
}

async function save(body: Partial<Settings>): Promise<void> {
  settings = await api<Settings>("PUT", "/api/intranet/settings", { attendanceVerify: settings!.attendanceVerify, ...body });
  toast("저장했습니다");
  renderWork(settings);
  renderVerify(settings);
}

document.addEventListener("submit", async (e) => {
  const form = e.target as HTMLFormElement;
  e.preventDefault();
  const v = formValues(form);
  try {
    switch (form.id) {
      case "workForm":
        await save({
          workDays: [0, 1, 2, 3, 4, 5, 6].filter((d) => v[`d${d}`] === "on"),
          workStart: seconds(v.workStart), workEnd: seconds(v.workEnd), leaveWarnSameRole: Number(v.leaveWarnSameRole),
        });
        break;
      case "verifyForm":
        await save({ attendanceVerify: v.attendanceVerify === "on", attendanceNetworks: (v.attendanceNetworks ?? "").split("\n") });
        break;
      case "holidayForm":
        await api("POST", "/api/intranet/holidays", { date: v.date, name: v.name });
        toast("추가했습니다");
        year = Number(v.date!.slice(0, 4));
        await loadHolidays();
        break;
    }
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

document.addEventListener("click", async (e) => {
  const el = (e.target as HTMLElement).closest<HTMLElement>("button");
  if (!el) return;
  try {
    if (el.dataset.year) {
      year += Number(el.dataset.year);
      await loadHolidays();
    } else if (el.dataset.remove) {
      await api("DELETE", `/api/intranet/holidays/${el.dataset.remove}`);
      toast("지웠습니다");
      await loadHolidays();
    } else if (el.id === "addMyIp" && settings?.yourIp) {
      const area = document.querySelector<HTMLTextAreaElement>("textarea[name=attendanceNetworks]")!;
      if (!area.value.split("\n").includes(settings.yourIp)) area.value = [area.value.trim(), settings.yourIp].filter(Boolean).join("\n");
    }
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

void (async () => {
  if (!(await setupMasthead())) return;
  await load().catch((e) => toast(errorMessage(e)));
})();
