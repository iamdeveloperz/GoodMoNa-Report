import { goLogin, refreshSession } from "./api";
import { esc } from "./format";

export type StaffRole = "Owner" | "Doctor" | "Nurse" | "Staff" | "Admin";
export const ROLE_LABEL: Record<StaffRole, string> = { Owner: "대표원장", Doctor: "원장", Nurse: "간호사", Staff: "직원", Admin: "관리자" };

export interface Me {
  id: number;
  loginId: string;
  name: string;
  role: StaffRole;
}

/**
 * 로그인한 직원 (02 인트라넷의 /api/auth/me).
 * 로그인 안 했으면 "signed-out", 서버에 닿지 않으면 "offline" (네트워크 문제로 로그인 화면에 보내지 않기 위해 구분).
 */
export async function getMe(): Promise<Me | "signed-out" | "offline"> {
  for (let i = 0; i < 2; i++) {
    let res: Response;
    try {
      res = await fetch("/api/auth/me");
    } catch {
      return "offline";
    }
    if (res.ok) return (await res.json()) as Me;
    if (res.status !== 401) return "offline";
    if (!(await refreshSession())) return "signed-out";
  }
  return "signed-out";
}

export async function logout(): Promise<void> {
  try {
    await fetch("/api/auth/logout", { method: "POST" });
  } finally {
    location.href = "/login";
  }
}

/**
 * 머리띠 오른쪽에 "김간호 간호사 · 로그아웃" 표시.
 * required면 로그인이 안 된 경우 바로 로그인 화면으로 보냅니다(서버도 화면 주소를 막지만, 개발 서버·캐시된 화면 대비).
 */
export async function showWho(el: HTMLElement, required = false): Promise<Me | null> {
  const me = await getMe();
  if (me === "signed-out" && required) {
    goLogin();
    return null;
  }
  if (typeof me === "string") {
    el.innerHTML = '<a class="who-link" href="/login">로그인</a>';
    return null;
  }
  el.innerHTML = `<a class="who-link" href="/intranet" title="인트라넷 홈">${esc(me.name)}<small>${ROLE_LABEL[me.role]}</small></a><button class="who-out" type="button">로그아웃</button>`;
  el.querySelector("button")!.addEventListener("click", () => void logout());
  return me;
}
