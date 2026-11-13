import { byId } from "./format";
import { showWho, type Me } from "./session";

/** 머리띠: 로그인한 직원 표시 + 직무에 맞는 메뉴만 보이기 (data-roles="Owner,Doctor") */
export async function setupMasthead(): Promise<Me | null> {
  const me = await showWho(byId("who"), true);
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
