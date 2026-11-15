import "../shared/base.css";
import "../shared/intranet.css";
import { api, errorMessage } from "../shared/api";
import { isManager, setupMasthead } from "../shared/chrome";
import { byId, esc, fmtDate, fmtMDW, formValues, toast, todayIso } from "../shared/format";
import { ROLE_LABEL, type Me } from "../shared/session";
import type { MyParking, ParkingAssignment, ParkingRequest, Person, PlateHit, Spot } from "../shared/types";

/**
 * 직원 주차: 번호 뒷자리로 차주 찾기(이중 주차 연락) · 내 주차 배정·신청 · 내 차량.
 * 대표원장·관리자: 신청 대기 → 주차면 배정, 주차면 관리(지금·다음 사용자, 배정 끝내기).
 */

let me: Me | null = null;
let spots: Spot[] = [];
let people: Person[] = [];

const period = (a: ParkingAssignment) => `${fmtMDW(a.startDate)} ~ ${a.endDate ? fmtMDW(a.endDate) : "계속"}`;

function renderLookup(hits: PlateHit[] | null): void {
  byId("lookup").innerHTML = `<form id="lookupForm" class="inline">
      <input type="search" name="q" inputmode="numeric" placeholder="번호 뒤 4자리 (예: 3456)" aria-label="차량 번호" required>
      <button class="btn" type="submit">찾기</button></form>
    ${hits === null ? "" : hits.length === 0
      ? '<p class="empty">등록된 직원 차량이 없습니다.</p>'
      : `<ul class="list">${hits.map((h) => `<li><span class="grow"><b>${esc(h.plate)}</b> ${h.model ? `<span class="meta">${esc(h.model)}</span>` : ""}<br>
          ${esc(h.ownerName)} <span class="meta">${ROLE_LABEL[h.role]}${h.department ? ` · ${esc(h.department)}` : ""}${h.spotName ? ` · 주차면 ${esc(h.spotName)}` : ""}</span></span>
          ${h.phone ? `<a class="btn compact" href="tel:${esc(h.phone.replace(/[^0-9+]/g, ""))}">${esc(h.phone)}</a>` : '<span class="meta">연락처 없음</span>'}</li>`).join("")}</ul>`}`;
}

async function loadMine(): Promise<void> {
  const m = await api<MyParking>("GET", "/api/intranet/parking/me");
  const a = m.assignment;
  byId("mine").innerHTML = a
    ? `<div class="card"><p class="big-number">${esc(a.spotName)}</p><p>${period(a)}${a.plate ? ` · ${esc(a.plate)}` : ""}</p>${a.spotNote ? `<p class="meta">${esc(a.spotNote)}</p>` : ""}<p class="meta">배정: ${esc(a.assignedByName)}</p></div>`
    : m.request
      ? `<p class="notice">${fmtDate(m.request.createdAt)}에 배정을 신청했습니다${m.request.plate ? ` (${esc(m.request.plate)})` : ""}. 대표원장·관리자가 주차면을 정하면 여기에 보입니다.
          <button class="textbtn" type="button" data-cancel-request="${m.request.id}">신청 취소</button></p>`
      : m.vehicles.length === 0
        ? '<p class="muted">배정된 주차면이 없습니다. 아래에서 차량을 먼저 등록한 뒤 신청하세요.</p>'
        : `<form id="applyForm" class="inline"><select name="vehicleId" aria-label="차량">${m.vehicles.map((v) => `<option value="${v.id}">${esc(v.plate)}</option>`).join("")}</select>
            <input type="text" name="note" maxlength="200" placeholder="요청 사항 (선택)"><button class="btn primary" type="submit">주차 배정 신청</button></form>`;

  byId("vehicles").innerHTML = `${m.vehicles.length === 0 ? '<p class="empty">등록한 차량이 없습니다.</p>' : `<ul class="list">${m.vehicles
      .map((v) => `<li><span class="grow"><b>${esc(v.plate)}</b> ${v.model ? `<span class="meta">${esc(v.model)}</span>` : ""}</span><button class="textbtn danger" type="button" data-remove-vehicle="${v.id}">지우기</button></li>`)
      .join("")}</ul>`}
    <form id="vehicleForm" class="form-grid">
      <label class="field"><span class="label">차량 번호</span><input type="text" name="plate" required placeholder="12가 3456"></label>
      <label class="field"><span class="label">차종 · 색 (선택)</span><input type="text" name="model" maxlength="40" placeholder="흰색 아반떼"></label>
      <div><button class="btn" type="submit">차량 등록</button></div>
    </form>
    <p class="meta">이중 주차 때 다른 직원이 번호로 연락할 수 있도록 차량 번호와 연락처(주소록)가 직원에게 보입니다.</p>`;
}

function spotOptions(): string {
  return spots.filter((s) => s.active).map((s) => `<option value="${s.id}">${esc(s.name)}${s.current ? ` (사용 중: ${esc(s.current.staffName)})` : ""}</option>`).join("");
}

async function loadRequests(): Promise<void> {
  const rows = await api<ParkingRequest[]>("GET", "/api/intranet/parking/requests");
  byId("requestCount").textContent = `${rows.length}건`;
  byId("requests").innerHTML = rows.length === 0
    ? '<p class="empty">배정을 기다리는 신청이 없습니다.</p>'
    : `<table class="people"><thead><tr><th>신청자</th><th>차량</th><th>신청일</th><th>배정</th></tr></thead><tbody>${rows
        .map((r) => `<tr><td class="nm"><b>${esc(r.staffName)}</b><small>${ROLE_LABEL[r.role]}${r.note ? ` · ${esc(r.note)}` : ""}</small></td>
          <td>${esc(r.plate ?? "—")}</td><td>${fmtDate(r.createdAt)}</td>
          <td><div class="acts"><select data-spot-for="${r.staffId}" aria-label="주차면">${spotOptions()}</select><input type="date" data-start-for="${r.staffId}" value="${todayIso()}" aria-label="시작일">
            <button class="btn compact primary" type="button" data-assign="${r.staffId}">배정</button>
            <button class="textbtn" type="button" data-reject-request="${r.id}">반려</button></div></td></tr>`)
        .join("")}</tbody></table>`;
}

async function loadSpots(): Promise<void> {
  spots = await api<Spot[]>("GET", "/api/intranet/parking/spots");
  const who = (a: ParkingAssignment | null) => (a ? `${esc(a.staffName)}${a.plate ? ` <span class="meta">${esc(a.plate)}</span>` : ""}<br><small class="meta">${period(a)}</small>` : '<span class="meta">비어 있음</span>');
  byId("spots").innerHTML = `${spots.length === 0 ? '<p class="empty">주차면을 먼저 등록하세요.</p>' : `<table class="people"><thead><tr><th>주차면</th><th>지금</th><th>다음</th><th></th></tr></thead><tbody>${spots
      .map((s) => `<tr><td class="nm"><b>${esc(s.name)}</b><small>${esc(s.note ?? "")}${s.active ? "" : " · 사용 안 함"}</small></td><td>${who(s.current)}</td><td>${who(s.next)}</td>
        <td><div class="acts">${s.current ? `<button class="textbtn" type="button" data-end="${s.current.id}">오늘로 배정 끝내기</button>` : ""}
          <button class="textbtn" type="button" data-toggle-spot="${s.id}" data-active="${s.active}">${s.active ? "사용 안 함" : "다시 사용"}</button></div></td></tr>`)
      .join("")}</tbody></table>`}
    <div class="cols">
      <form id="spotForm" class="form-grid card"><h2 class="wide">주차면 추가</h2>
        <label class="field"><span class="label">이름</span><input type="text" name="name" maxlength="20" required placeholder="B1-03"></label>
        <label class="field"><span class="label">메모 (선택)</span><input type="text" name="note" maxlength="100" placeholder="엘리베이터 앞"></label>
        <div><button class="btn" type="submit">추가</button></div></form>
      <form id="assignForm" class="form-grid card"><h2 class="wide">바로 배정</h2>
        <label class="field"><span class="label">주차면</span><select name="spotId">${spotOptions()}</select></label>
        <label class="field"><span class="label">직원</span><select name="staffId">${people.map((p) => `<option value="${p.id}">${esc(p.name)}</option>`).join("")}</select></label>
        <label class="field"><span class="label">시작일</span><input type="date" name="startDate" value="${todayIso()}"></label>
        <label class="field"><span class="label">끝 날짜 (비우면 계속)</span><input type="date" name="endDate"></label>
        <div><button class="btn primary" type="submit">배정</button></div></form>
    </div>`;
}

async function reload(): Promise<void> {
  try {
    await loadMine();
    if (isManager(me)) {
      people = await api<Person[]>("GET", "/api/intranet/people");
      await loadSpots();
      await loadRequests();
    }
  } catch (e) {
    toast(errorMessage(e));
  }
}

document.addEventListener("submit", async (e) => {
  const form = e.target as HTMLFormElement;
  e.preventDefault();
  const v = formValues(form);
  try {
    switch (form.id) {
      case "lookupForm":
        renderLookup(await api<PlateHit[]>("GET", `/api/intranet/parking/lookup?q=${encodeURIComponent(v.q ?? "")}`));
        byId<HTMLInputElement>("lookup").querySelector<HTMLInputElement>("input[name=q]")!.value = v.q ?? "";
        return;
      case "vehicleForm":
        await api("POST", "/api/intranet/parking/vehicles", { plate: v.plate, model: v.model });
        toast("차량을 등록했습니다");
        break;
      case "applyForm":
        await api("POST", "/api/intranet/parking/requests", { vehicleId: Number(v.vehicleId), note: v.note });
        toast("배정을 신청했습니다");
        break;
      case "spotForm":
        await api("POST", "/api/intranet/parking/spots", { name: v.name, note: v.note });
        toast("주차면을 추가했습니다");
        break;
      case "assignForm":
        await api("POST", "/api/intranet/parking/assignments", { spotId: Number(v.spotId), staffId: Number(v.staffId), startDate: v.startDate || null, endDate: v.endDate || null });
        toast("배정했습니다");
        break;
      default:
        return;
    }
    await reload();
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

document.addEventListener("click", async (e) => {
  const el = (e.target as HTMLElement).closest<HTMLElement>("button");
  if (!el) return;
  const d = el.dataset;
  try {
    if (d.removeVehicle) {
      await api("DELETE", `/api/intranet/parking/vehicles/${d.removeVehicle}`);
      toast("차량을 지웠습니다");
    } else if (d.cancelRequest) {
      await api("POST", `/api/intranet/parking/requests/${d.cancelRequest}/cancel`);
      toast("신청을 취소했습니다");
    } else if (d.rejectRequest) {
      await api("POST", `/api/intranet/parking/requests/${d.rejectRequest}/reject`, {});
      toast("반려했습니다");
    } else if (d.assign) {
      const spotId = Number(document.querySelector<HTMLSelectElement>(`select[data-spot-for="${d.assign}"]`)?.value);
      const startDate = document.querySelector<HTMLInputElement>(`input[data-start-for="${d.assign}"]`)?.value || null;
      await api("POST", "/api/intranet/parking/assignments", { spotId, staffId: Number(d.assign), startDate });
      toast("배정했습니다");
    } else if (d.end) {
      await api("POST", `/api/intranet/parking/assignments/${d.end}/end`, { endDate: todayIso() });
      toast("배정을 끝냈습니다");
    } else if (d.toggleSpot) {
      const s = spots.find((x) => x.id === Number(d.toggleSpot));
      await api("PUT", `/api/intranet/parking/spots/${d.toggleSpot}`, { name: s?.name, note: s?.note, active: d.active !== "true" });
    } else return;
    await reload();
  } catch (ex) {
    toast(errorMessage(ex));
  }
});

void (async () => {
  me = await setupMasthead();
  if (!me) return;
  renderLookup(null);
  await reload();
  if (location.hash) document.getElementById(location.hash.slice(1))?.scrollIntoView();
})();
