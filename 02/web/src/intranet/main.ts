import "../shared/base.css";
import "../shared/intranet.css";
import { api, type StaffItem } from "../shared/api";
import { setupMasthead } from "../shared/chrome";
import { byId, esc } from "../shared/format";
import { ROLE_LABEL, type StaffRole } from "../shared/session";

interface Tile {
  href: string;
  title: string;
  desc: string;
  roles: StaffRole[];
}

const TILES: Tile[] = [
  { href: "/report", title: "보고", desc: "환자 검색 → 폰 촬영(QR) → 경과 보고", roles: ["Owner", "Doctor", "Nurse"] },
  { href: "/director", title: "결과 확인", desc: "받은 보고 · 전후 비교 · 소견 녹음", roles: ["Owner", "Doctor"] },
  { href: "/intranet/admin", title: "직원 관리", desc: "가입 승인 · 직무 변경 · 사용 중지", roles: ["Owner", "Admin"] },
  { href: "/import", title: "기록 가져오기", desc: "이전 전산 CSV·사진 (서버 PC에서)", roles: ["Owner", "Admin"] },
];

async function main(): Promise<void> {
  const me = await setupMasthead();
  if (!me) return;
  byId("hello").innerHTML = `${esc(me.name)}<small>${ROLE_LABEL[me.role]}</small>`;

  const mine = TILES.filter((t) => t.roles.includes(me.role));
  const box = byId("tiles");
  box.innerHTML = mine.length
    ? mine.map((t) => `<a class="tile" href="${t.href}"><b>${t.title}</b><span>${t.desc}</span></a>`).join("")
    : '<p class="empty">아직 쓸 수 있는 업무 화면이 없습니다. 인트라넷 기능이 곧 추가됩니다.</p>';

  // 관리자에게는 승인 대기 건수를 표시
  if (me.role === "Owner" || me.role === "Admin") {
    const pending = await api<StaffItem[]>("GET", "/api/admin/staff?status=Pending");
    if (pending.length) {
      const tile = box.querySelector<HTMLElement>('a[href="/intranet/admin"]');
      tile?.insertAdjacentHTML("beforeend", `<em class="count">승인 대기 ${pending.length}</em>`);
    }
  }
}

void main();
