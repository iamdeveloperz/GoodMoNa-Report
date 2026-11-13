import "../shared/base.css";
import "../shared/intranet.css";
import { api, errorMessage, type StaffItem } from "../shared/api";
import { setupMasthead } from "../shared/chrome";
import { byId, esc, toast } from "../shared/format";
import { ROLE_LABEL, type Me, type StaffRole } from "../shared/session";

const STATUS_LABEL = { Pending: "승인 대기", Active: "사용 중", Rejected: "반려", Disabled: "중지" } as const;
const ROLES = Object.keys(ROLE_LABEL) as StaffRole[];

let me: Me | null = null;
let staff: StaffItem[] = [];
/** 두 번 눌러야 실행되는 위험 동작(중지)의 대상 */
let confirmDisable: number | null = null;
let rejecting: number | null = null;

const fmtDate = (iso: string) => {
  const d = new Date(iso);
  return `${d.getFullYear()}.${String(d.getMonth() + 1).padStart(2, "0")}.${String(d.getDate()).padStart(2, "0")}`;
};
const roleSelect = (s: StaffItem, selected: StaffRole) =>
  `<select data-role="${s.id}" aria-label="${esc(s.name)} 직무">${ROLES.map((r) => `<option value="${r}"${r === selected ? " selected" : ""}>${ROLE_LABEL[r]}</option>`).join("")}</select>`;
const nameCell = (s: StaffItem) =>
  `<td class="nm"><b>${esc(s.name)}</b><small>${esc(s.loginId)}${s.department ? ` · ${esc(s.department)}` : ""}${s.phone ? ` · ${esc(s.phone)}` : ""}</small></td>`;

async function load(): Promise<void> {
  try {
    staff = await api<StaffItem[]>("GET", "/api/admin/staff");
    render();
  } catch (e) {
    toast(errorMessage(e));
  }
}

function pendingActions(s: StaffItem): string {
  if (rejecting === s.id) {
    return `<input type="text" data-reason="${s.id}" placeholder="반려 사유 (선택)" maxlength="200">
      <button class="btn compact danger" type="button" data-act="reject-yes" data-id="${s.id}">반려</button>
      <button class="textbtn" type="button" data-act="reject-no">취소</button>`;
  }
  return `<button class="btn compact primary" type="button" data-act="approve" data-id="${s.id}">승인</button>
    <button class="textbtn" type="button" data-act="reject" data-id="${s.id}">반려</button>`;
}

function staffActions(s: StaffItem): string {
  if (s.id === me?.id) return '<span class="meta">나</span>';
  switch (s.status) {
    case "Active":
      return confirmDisable === s.id
        ? `<button class="btn compact danger solid" type="button" data-act="disable-yes" data-id="${s.id}">로그인 끊고 중지</button>
           <button class="textbtn" type="button" data-act="disable-no">취소</button>`
        : `<button class="textbtn danger" type="button" data-act="disable" data-id="${s.id}">사용 중지</button>`;
    case "Disabled":
      return `<button class="textbtn" type="button" data-act="enable" data-id="${s.id}">다시 사용</button>`;
    case "Rejected":
      return `<button class="textbtn" type="button" data-act="approve" data-id="${s.id}">승인</button>`;
    default:
      return "";
  }
}

function render(): void {
  const pending = staff.filter((s) => s.status === "Pending");
  const rest = staff.filter((s) => s.status !== "Pending");
  byId("pendingCount").textContent = `${pending.length}명`;
  byId("allCount").textContent = `${rest.length}명`;

  byId("pending").innerHTML = pending.length === 0
    ? '<p class="empty">승인을 기다리는 신청이 없습니다.</p>'
    : `<table class="people"><thead><tr><th>신청자</th><th>신청 직무</th><th>신청일</th><th>승인할 직무</th><th></th></tr></thead><tbody>${pending
        .map((s) => `<tr>${nameCell(s)}<td>${ROLE_LABEL[s.requestedRole]}</td><td>${fmtDate(s.createdAt)}</td><td>${roleSelect(s, s.requestedRole)}</td><td><div class="acts">${pendingActions(s)}</div></td></tr>`)
        .join("")}</tbody></table>`;

  byId("all").innerHTML = rest.length === 0
    ? '<p class="empty">아직 직원이 없습니다.</p>'
    : `<table class="people"><thead><tr><th>직원</th><th>상태</th><th>직무</th><th>결정</th><th></th></tr></thead><tbody>${rest
        .map((s) => {
          const role = s.status === "Active" && s.id !== me?.id ? roleSelect(s, s.role) : ROLE_LABEL[s.role];
          const decided = s.decidedAt
            ? `${fmtDate(s.decidedAt)}${s.decidedBy ? ` · ${esc(s.decidedBy)}` : ""}${s.rejectReason ? `<br><small>${esc(s.rejectReason)}</small>` : ""}`
            : "";
          return `<tr>${nameCell(s)}<td><span class="status ${s.status}">${STATUS_LABEL[s.status]}</span></td><td>${role}</td><td class="meta">${decided}</td><td><div class="acts">${staffActions(s)}</div></td></tr>`;
        })
        .join("")}</tbody></table>`;
}

async function act(action: string, id: number): Promise<void> {
  const person = staff.find((s) => s.id === id);
  try {
    switch (action) {
      case "approve": {
        const sel = document.querySelector<HTMLSelectElement>(`select[data-role="${id}"]`);
        await api("POST", `/api/admin/staff/${id}/approve`, { role: sel?.value ?? person?.requestedRole });
        toast(`${person?.name ?? ""} 승인했습니다`);
        break;
      }
      case "reject":
        rejecting = id;
        render();
        document.querySelector<HTMLInputElement>(`input[data-reason="${id}"]`)?.focus();
        return;
      case "reject-no":
        rejecting = null;
        render();
        return;
      case "reject-yes": {
        const reason = document.querySelector<HTMLInputElement>(`input[data-reason="${id}"]`)?.value ?? "";
        await api("POST", `/api/admin/staff/${id}/reject`, { reason });
        rejecting = null;
        toast(`${person?.name ?? ""} 반려했습니다`);
        break;
      }
      case "disable":
        confirmDisable = id;
        render();
        return;
      case "disable-no":
        confirmDisable = null;
        render();
        return;
      case "disable-yes":
        await api("POST", `/api/admin/staff/${id}/disable`);
        confirmDisable = null;
        toast(`${person?.name ?? ""} 사용을 중지했습니다`);
        break;
      case "enable":
        await api("POST", `/api/admin/staff/${id}/enable`);
        toast(`${person?.name ?? ""} 다시 사용합니다`);
        break;
    }
  } catch (e) {
    toast(errorMessage(e));
  }
  await load();
}

document.addEventListener("click", (e) => {
  const b = (e.target as HTMLElement).closest<HTMLElement>("[data-act]");
  if (b) void act(b.dataset.act!, Number(b.dataset.id));
});

// 사용 중인 직원의 직무 변경 (승인 대기 행의 직무는 승인 버튼을 눌러야 반영)
document.addEventListener("change", async (e) => {
  const sel = e.target as HTMLSelectElement;
  const id = Number(sel.dataset.role);
  const person = staff.find((s) => s.id === id);
  if (!id || person?.status !== "Active") return;
  try {
    await api("PUT", `/api/admin/staff/${id}/role`, { role: sel.value });
    toast(`${person.name} 직무를 ${ROLE_LABEL[sel.value as StaffRole]}(으)로 바꿨습니다 (15분 안에 반영)`);
  } catch (ex) {
    toast(errorMessage(ex));
  }
  await load();
});

void (async () => {
  me = await setupMasthead();
  await load();
})();
