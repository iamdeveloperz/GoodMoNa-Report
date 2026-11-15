import "../shared/base.css";
import "../shared/intranet.css";
import { api, errorMessage } from "../shared/api";
import { isManager, setupMasthead } from "../shared/chrome";
import { byId, esc, formValues, toast } from "../shared/format";
import { ROLE_LABEL, type Me } from "../shared/session";
import type { Person } from "../shared/types";

/** 주소록: 사용 중인 직원(부서순). 입사일은 대표원장·관리자에게만. 본인 부서·연락처는 여기서 고칩니다. */

let me: Me | null = null;
let people: Person[] = [];

function render(): void {
  const q = byId<HTMLInputElement>("q").value.trim().toLowerCase();
  const rows = people.filter((p) => !q || [p.name, p.department ?? "", ROLE_LABEL[p.role], p.phone ?? ""].some((s) => s.toLowerCase().includes(q)));
  const manager = isManager(me);
  byId("list").innerHTML = rows.length === 0
    ? '<p class="empty">찾는 직원이 없습니다.</p>'
    : `<table class="people"><thead><tr><th>이름</th><th>부서</th><th>직무</th><th>연락처</th>${manager ? "<th>입사일</th>" : ""}</tr></thead><tbody>${rows
        .map((p) => `<tr><td class="nm"><b>${esc(p.name)}</b></td><td>${esc(p.department ?? "—")}</td><td>${ROLE_LABEL[p.role]}</td>
          <td>${p.phone ? `<a href="tel:${esc(p.phone.replace(/[^0-9+]/g, ""))}">${esc(p.phone)}</a>` : '<span class="meta">—</span>'}</td>${manager ? `<td>${p.hireDate ?? '<span class="late">미등록</span>'}</td>` : ""}</tr>`)
        .join("")}</tbody></table><p class="meta">${rows.length}명</p>`;
}

function renderProfile(): void {
  const mine = people.find((p) => p.id === me?.id);
  byId("profile").innerHTML = `<form id="profileForm" class="form-grid">
    <label class="field"><span class="label">부서</span><input type="text" name="department" maxlength="30" value="${esc(mine?.department ?? "")}" placeholder="예: 진료부"></label>
    <label class="field"><span class="label">연락처</span><input type="tel" name="phone" maxlength="20" value="${esc(mine?.phone ?? "")}" placeholder="010-0000-0000"></label>
    <div><button class="btn" type="submit">저장</button></div>
  </form><p class="meta">이름·직무·입사일은 대표원장·관리자가 직원 관리에서 바꿉니다.</p>`;
}

async function load(): Promise<void> {
  people = await api<Person[]>("GET", "/api/intranet/people");
  render();
  renderProfile();
}

byId("q").addEventListener("input", render);
document.addEventListener("submit", async (e) => {
  const form = e.target as HTMLFormElement;
  if (form.id !== "profileForm") return;
  e.preventDefault();
  try {
    await api("PUT", "/api/intranet/me/profile", formValues(form));
    toast("저장했습니다");
    await load();
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

void (async () => {
  me = await setupMasthead();
  if (!me) return;
  await load().catch((e) => toast(errorMessage(e)));
})();
