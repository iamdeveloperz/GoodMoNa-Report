import "../shared/base.css";
import "../shared/intranet.css";
import { errorMessage, post } from "../shared/api";
import { byId, esc } from "../shared/format";

let bootstrap = false;
const err = byId("err");
const roleSel = byId<HTMLSelectElement>("role");

/** 관리자가 아직 없고 서버 PC에서 열었으면: 첫 관리자(대표원장·관리자)를 바로 만듭니다. */
async function checkBootstrap(): Promise<void> {
  try {
    const res = await fetch("/api/auth/bootstrap");
    bootstrap = res.ok && ((await res.json()) as { available: boolean }).available;
  } catch {
    bootstrap = false;
  }
  if (!bootstrap) return;
  byId("bootstrap").hidden = false;
  byId("title").textContent = "첫 관리자 계정";
  byId("lead").firstChild!.textContent = "첫 관리자 만들기";
  byId("extra").hidden = true;
  byId("submit").textContent = "관리자 계정 만들기";
  roleSel.innerHTML = '<option value="Owner" selected>대표원장</option><option value="Admin">관리자</option>';
}

function fail(message: string): void {
  err.textContent = message;
  err.hidden = false;
}

byId<HTMLFormElement>("form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const v = (id: string) => byId<HTMLInputElement>(id).value.trim();
  const name = v("name");
  const loginId = v("loginId").toLowerCase();
  const password = byId<HTMLInputElement>("password").value;
  err.hidden = true;

  if (!name) return fail("이름을 입력해 주세요.");
  if (!/^[a-z0-9][a-z0-9._-]{2,29}$/.test(loginId)) return fail("아이디는 영문 소문자·숫자 3~30자로 정해 주세요.");
  if (password.length < 8) return fail("비밀번호는 8자 이상이어야 합니다.");
  if (password !== byId<HTMLInputElement>("password2").value) return fail("비밀번호 확인이 맞지 않습니다.");
  if (!roleSel.value) return fail("직무를 골라 주세요.");

  const btn = byId<HTMLButtonElement>("submit");
  btn.disabled = true;
  try {
    if (bootstrap) {
      await post("/api/auth/bootstrap", { loginId, name, password, role: roleSel.value });
      location.replace("/intranet/admin");
      return;
    }
    await post("/api/auth/signup", { loginId, name, password, requestedRole: roleSel.value, department: v("department"), phone: v("phone") });
    byId("form").hidden = true;
    const done = byId("done");
    done.hidden = false;
    done.innerHTML = `<b>${esc(name)}</b>님의 가입 신청을 보냈습니다.<br>대표원장·관리자가 승인하면 아이디 <b>${esc(loginId)}</b>로 <a href="/login">로그인</a>할 수 있습니다.`;
  } catch (ex) {
    fail(errorMessage(ex));
    btn.disabled = false;
  }
});

void checkBootstrap();
