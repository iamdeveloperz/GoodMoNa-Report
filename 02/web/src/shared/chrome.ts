import { byId, esc } from "./format";
import { showWho, type Me, type StaffRole } from "./session";

interface NavItem {
  href: string;
  label: string;
  roles?: StaffRole[];
}

/** 인트라넷 메뉴. 01의 보고·결과 확인 화면도 직무에 맞으면 보입니다. */
const NAV: NavItem[] = [
  { href: "/intranet", label: "홈" },
  { href: "/intranet/notices", label: "공지" },
  { href: "/intranet/leave", label: "연차" },
  { href: "/intranet/attendance", label: "출퇴근" },
  { href: "/intranet/parking", label: "주차" },
  { href: "/intranet/people", label: "주소록" },
  { href: "/report", label: "보고", roles: ["Owner", "Doctor", "Nurse"] },
  { href: "/director", label: "결과 확인", roles: ["Owner", "Doctor"] },
  { href: "/intranet/admin", label: "직원 관리", roles: ["Owner", "Admin"] },
  { href: "/intranet/settings", label: "설정", roles: ["Owner", "Admin"] },
];

export const isManager = (me: Me | null): boolean => me?.role === "Owner" || me?.role === "Admin";

/** 머리띠: 로그인한 직원 표시 + 직무에 맞는 메뉴(#nav). 관리자 전용 요소(data-roles="Owner,Admin")도 여기서 보이거나 숨깁니다. */
export async function setupMasthead(): Promise<Me | null> {
  const me = await showWho(byId("who"), true);
  const nav = document.getElementById("nav");
  if (nav && me) {
    const here = location.pathname.replace(/\/$/, "") || "/intranet";
    nav.innerHTML = NAV.filter((n) => !n.roles || n.roles.includes(me.role))
      .map((n) => `<a href="${n.href}"${here === n.href ? ' aria-current="page"' : ""}>${esc(n.label)}</a>`)
      .join("");
    nav.querySelector('[aria-current="page"]')?.scrollIntoView({ inline: "center", block: "nearest" });
  }
  document.querySelectorAll<HTMLElement>("[data-roles]").forEach((el) => {
    el.hidden = !me || !el.dataset.roles!.split(",").includes(me.role);
  });
  return me;
}

/** 로그인 뒤 돌아갈 주소. 같은 사이트 경로이거나, 서버 PC에서 localhost로 연 01 화면만 허용합니다(다른 사이트로 튕기는 것 방지). */
export function safeNext(raw: string | null): string {
  if (!raw) return "/intranet";
  if (raw.startsWith("/") && !raw.startsWith("//")) return raw;
  try {
    const u = new URL(raw);
    if (u.protocol === "http:" && (u.hostname === "localhost" || u.hostname === "127.0.0.1")) return u.href;
  } catch {
    /* 잘못된 주소 */
  }
  return "/intranet";
}
