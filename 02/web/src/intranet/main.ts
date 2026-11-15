import "../shared/base.css";
import "../shared/intranet.css";
import { api, errorMessage } from "../shared/api";
import { setupMasthead } from "../shared/chrome";
import { byId, esc, fmtDateLong, fmtDays, fmtHM, fmtMDW, toast } from "../shared/format";
import { ROLE_LABEL, type Me, type StaffRole } from "../shared/session";
import { DAY_STATUS, LEAVE_TYPE, PORTION, type AttendanceDay, type Home } from "../shared/types";

/** 인트라넷 홈: 필독 공지 · 오늘 출퇴근 · 내 연차 · 오늘 휴가자 · 최근 공지 · (관리자) 처리할 일 · 바로가기 */

interface Tile {
  href: string;
  title: string;
  desc: string;
  roles?: StaffRole[];
}

const TILES: Tile[] = [
  { href: "/intranet/notices", title: "공지", desc: "필독 공지 · 읽음 확인" },
  { href: "/intranet/leave", title: "연차", desc: "잔여 · 휴가 신청 · 팀 달력" },
  { href: "/intranet/attendance", title: "출퇴근", desc: "출근 · 퇴근 기록, 이번 달" },
  { href: "/intranet/parking", title: "주차", desc: "내 차량 · 주차 배정 · 차주 찾기" },
  { href: "/intranet/people", title: "주소록", desc: "직원 연락처" },
  { href: "/intranet/ask", title: "CS 도우미", desc: "고객 문의 → 승인된 FAQ·안내 문서에서 답 찾기" },
  { href: "/report", title: "보고", desc: "환자 검색 → 폰 촬영(QR) → 경과 보고", roles: ["Owner", "Doctor", "Nurse"] },
  { href: "/director", title: "결과 확인", desc: "받은 보고 · 전후 비교 · 소견 녹음", roles: ["Owner", "Doctor"] },
  { href: "/intranet/admin", title: "직원 관리", desc: "가입 승인 · 직무 · 입사일 · 사용 중지", roles: ["Owner", "Admin"] },
  { href: "/intranet/settings", title: "설정", desc: "근무 요일 · 출퇴근 확인 · 휴무일", roles: ["Owner", "Admin"] },
  { href: "/intranet/knowledge", title: "자료 관리", desc: "CS 자료 가져오기 · 승인 · 못 찾은 질문", roles: ["Owner", "Admin"] },
  { href: "/import", title: "기록 가져오기", desc: "이전 전산 CSV·사진 (서버 PC에서)", roles: ["Owner", "Admin"] },
];

let me: Me | null = null;

function attendanceCard(day: AttendanceDay | null, home: Home): string {
  if (!day) return "";
  const times = `${day.checkInAt ? `출근 <b>${fmtHM(day.checkInAt)}</b>${day.late ? ' <span class="late">지각</span>' : ""}` : "출근 전"}
    ${day.checkOutAt ? ` · 퇴근 <b>${fmtHM(day.checkOutAt)}</b>` : ""}`;
  const leave = day.leaveType ? `<p>${LEAVE_TYPE[day.leaveType]} · ${PORTION[day.leavePortion!]}</p>` : "";
  const action = !day.checkInAt
    ? '<button class="btn primary" type="button" data-punch="check-in">출근</button>'
    : '<button class="btn" type="button" data-punch="check-out">퇴근</button>';
  return `<div class="card"><h2>오늘 출퇴근</h2>
    <p class="meta">${home.workday ? "근무일" : esc(home.holidayName ?? "쉬는 날")}${day.status !== "None" ? ` · ${DAY_STATUS[day.status]}` : ""}</p>
    <p>${times}</p>${leave}
    <div class="punch">${action}<a class="textbtn" href="/intranet/attendance">기록 보기</a></div></div>`;
}

function leaveCard(home: Home): string {
  const b = home.leave;
  if (!b) {
    return `<div class="card"><h2>내 연차</h2><p class="muted">입사일이 등록되지 않아 연차를 계산할 수 없습니다. 대표원장·관리자에게 입사일 등록을 요청해 주세요.</p>
      <a class="textbtn" href="/intranet/leave">휴가 신청 (병가·경조·공가)</a></div>`;
  }
  return `<div class="card"><h2>내 연차</h2>
    <p class="big-number">${fmtDays(b.available)}<small>일</small></p>
    <p class="meta">쓸 수 있는 연차 · ${fmtMDW(b.periodLastDay)}까지${b.pending ? ` · 승인 대기 ${fmtDays(b.pending)}일` : ""}</p>
    <p class="meta">다음 발생: ${fmtMDW(b.nextGrantDate)} +${b.nextGrantDays}일</p>
    <a class="textbtn" href="/intranet/leave">휴가 신청</a></div>`;
}

function offCard(home: Home): string {
  const items = home.offToday;
  return `<div class="card"><h2>오늘 휴가</h2>${
    items.length === 0
      ? '<p class="muted">오늘 쉬는 직원이 없습니다.</p>'
      : `<ul class="list">${items.map((i) => `<li><span><b>${esc(i.name)}</b> <span class="meta">${ROLE_LABEL[i.role]}</span></span><span class="meta">${LEAVE_TYPE[i.type]} · ${PORTION[i.portion]}</span></li>`).join("")}</ul>`
  }</div>`;
}

function noticeCard(home: Home): string {
  const items = home.recentNotices;
  return `<div class="card"><h2>최근 공지</h2>${
    items.length === 0
      ? '<p class="muted">공지가 없습니다.</p>'
      : `<ul class="list">${items.map((n) => `<li><a href="/intranet/notices#${n.id}">${n.read ? "" : '<span class="unread-dot" aria-label="안 읽음"></span>'}${esc(n.title)}</a>${n.required ? '<span class="tag accent">필독</span>' : ""}</li>`).join("")}</ul>`
  }<a class="textbtn" href="/intranet/notices">공지 전체</a></div>`;
}

async function load(): Promise<void> {
  const home = await api<Home>("GET", "/api/intranet/home");
  byId("hello").innerHTML = `${esc(me!.name)}<small>${ROLE_LABEL[me!.role]} · ${fmtDateLong(home.today)}</small>`;

  byId("required").innerHTML = home.requiredUnread.length
    ? `<div class="banner"><b>확인하지 않은 필독 공지 ${home.requiredUnread.length}건</b>${home.requiredUnread
        .map((n) => `<a href="/intranet/notices#${n.id}">${esc(n.title)}</a>`)
        .join("")}</div>`
    : "";

  byId("cards").innerHTML = attendanceCard(home.attendance, home) + leaveCard(home) + offCard(home) + noticeCard(home);

  if (home.manage) {
    const m = home.manage;
    const todo = [
      { href: "/intranet/admin", title: "가입 승인", n: m.staffPending },
      { href: "/intranet/leave#pending", title: "휴가 승인", n: m.leavePending },
      { href: "/intranet/parking#requests", title: "주차 배정", n: m.parkingPending },
      { href: "/intranet/admin", title: "입사일 미등록", n: m.missingHireDate },
      { href: "/intranet/knowledge", title: "CS 자료 · 질문", n: m.kbPending },
    ];
    byId("todo").innerHTML = todo
      .map((t) => `<a class="tile" href="${t.href}"><b>${t.title}</b><span>${t.n ? `${t.n}건` : "없음"}</span>${t.n ? `<em class="count">${t.n}</em>` : ""}</a>`)
      .join("");
  }
}

document.addEventListener("click", async (e) => {
  const b = (e.target as HTMLElement).closest<HTMLButtonElement>("[data-punch]");
  if (!b) return;
  b.disabled = true;
  try {
    await api("POST", `/api/intranet/attendance/${b.dataset.punch}`, {});
    toast(b.dataset.punch === "check-in" ? "출근을 기록했습니다" : "퇴근을 기록했습니다");
    await load();
  } catch (ex) {
    toast(errorMessage(ex));
    b.disabled = false;
  }
});

void (async () => {
  me = await setupMasthead();
  if (!me) return;
  byId("tiles").innerHTML = `<div class="tiles">${TILES.filter((t) => !t.roles || t.roles.includes(me!.role))
    .map((t) => `<a class="tile" href="${t.href}"><b>${t.title}</b><span>${t.desc}</span></a>`)
    .join("")}</div>`;
  try {
    await load();
  } catch (e) {
    toast(errorMessage(e));
  }
})();
