import "../shared/base.css";
import "../shared/intranet.css";
import { errorMessage, post } from "../shared/api";
import { safeNext } from "../shared/chrome";
import { byId } from "../shared/format";

const params = new URLSearchParams(location.search);
const next = safeNext(params.get("next"));
const err = byId("err");

// 이미 로그인돼 있으면(출입증만 만료) 바로 갱신해서 돌려보냅니다. "다른 계정으로 로그인"으로 왔으면 그대로 둡니다.
async function trySilent(): Promise<void> {
  if (params.has("switch")) return;
  try {
    const res = await fetch("/api/auth/refresh", { method: "POST" });
    if (res.ok) location.replace(next);
  } catch {
    /* 무시 */
  }
}

async function checkBootstrap(): Promise<void> {
  try {
    const res = await fetch("/api/auth/bootstrap");
    if (res.ok && ((await res.json()) as { available: boolean }).available) byId("bootstrap").hidden = false;
  } catch {
    /* 무시 */
  }
}

byId<HTMLFormElement>("form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const loginId = byId<HTMLInputElement>("loginId").value.trim();
  const password = byId<HTMLInputElement>("password").value;
  if (!loginId || !password) {
    err.textContent = "아이디와 비밀번호를 입력해 주세요.";
    err.hidden = false;
    return;
  }
  const btn = byId<HTMLButtonElement>("submit");
  btn.disabled = true;
  err.hidden = true;
  try {
    await post("/api/auth/login", { loginId, password });
    location.replace(next);
  } catch (ex) {
    err.textContent = errorMessage(ex);
    err.hidden = false;
    btn.disabled = false;
    byId<HTMLInputElement>("password").select();
  }
});

void trySilent();
void checkBootstrap();
