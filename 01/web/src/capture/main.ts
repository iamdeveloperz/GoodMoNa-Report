import "../shared/base.css";
import "./capture.css";
import { ApiError, errorMessage, serverNow, syncClock, toApiError, upload, type CaptureInfo } from "../shared/api";
import { byId, esc, fmtBirthShort, fmtRemain, toast } from "../shared/format";
import { prepareJpeg, sampleScalpJpeg } from "../shared/image";

/**
 * 폰 촬영 페이지 (/capture/{token}).
 * 로그인 없이 QR 속 토큰으로만 동작하며, 화면에는 가린 이름만 보입니다.
 */

type Stage =
  | { kind: "loading" }
  | { kind: "error"; message: string }
  | { kind: "expired" }
  | { kind: "reported" }
  | { kind: "idle" }
  | { kind: "preview"; blob: Blob; url: string }
  | { kind: "uploading"; blob: Blob; url: string; percent: number }
  | { kind: "done" };

const phone = byId("phone");
const token = decodeURIComponent(location.pathname.split("/capture/")[1] ?? "").replace(/\/$/, "");
let info: CaptureInfo | null = null;
let stage: Stage = { kind: "loading" };
let timer: number | undefined;

function setStage(next: Stage): void {
  if ("url" in stage && (!("url" in next) || next.url !== stage.url)) URL.revokeObjectURL(stage.url);
  stage = next;
  render();
}

const remaining = (): number => (info ? Date.parse(info.expiresAt) - serverNow() : 0);

function header(): string {
  const head = '<div class="phone-head"><h1>GoodMoNa 촬영</h1><span class="meta">오늘 방문</span></div>';
  if (!info) return head;
  const left = remaining();
  return `${head}<div class="who"><span><b>${esc(info.maskedName)}</b> <span class="meta">${fmtBirthShort(info.birthDate)}</span></span>
    <span class="clock ${left < 60_000 ? "low" : ""}" id="capTimer">${fmtRemain(left)}</span></div>`;
}

const message = (title: string, body: string) => `<div class="done"><h2>${title}</h2><p class="muted">${body}</p></div>`;

function render(): void {
  let body = "";
  switch (stage.kind) {
    case "loading":
      body = '<p class="muted">불러오는 중…</p>';
      break;
    case "error":
      body = message("열 수 없습니다", esc(stage.message));
      break;
    case "expired":
      body = message("시간이 지났습니다", "PC에서 QR을 다시 띄운 뒤 스캔해 주세요.");
      break;
    case "reported":
      body = message("이미 보고된 방문입니다", "이 QR로는 더 이상 촬영할 수 없습니다.");
      break;
    case "idle":
      body = `<div class="frame empty">아래 버튼을 누르면<br>카메라가 열립니다</div>
        <label class="btn primary block filebtn">카메라로 촬영<input type="file" accept="image/*" capture="environment" id="camInput" aria-label="카메라로 촬영"></label>
        <button class="textbtn" type="button" id="useSample">PC에서 시연할 때: 샘플 사진 사용</button>`;
      break;
    case "preview":
      body = `<div class="frame"><img src="${stage.url}" alt="촬영한 사진 미리보기"></div>
        <div class="grid2"><button class="btn" type="button" id="retake">다시 찍기</button><button class="btn primary" type="button" id="send">보내기</button></div>`;
      break;
    case "uploading":
      body = `<div class="frame"><img src="${stage.url}" alt="촬영한 사진 미리보기"></div>
        <div class="bar" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${stage.percent}"><i style="width:${stage.percent}%"></i></div>
        <p class="meta" id="upLabel">보내는 중 ${stage.percent}%</p>`;
      break;
    case "done":
      body = `${message("보냈습니다", "PC 화면에 사진이 떴는지 확인하세요.")}
        <button class="btn block" type="button" id="again">다시 찍기</button>`;
      break;
  }
  phone.innerHTML = header() + body;
  bind();
}

function bind(): void {
  document.getElementById("camInput")?.addEventListener("change", async (e) => {
    const file = (e.target as HTMLInputElement).files?.[0];
    if (!file) return;
    try {
      const blob = await prepareJpeg(file);
      setStage({ kind: "preview", blob, url: URL.createObjectURL(blob) });
    } catch {
      toast("사진을 읽지 못했습니다. 다시 찍어 주세요.");
    }
  });
  document.getElementById("useSample")?.addEventListener("click", async () => {
    const blob = await sampleScalpJpeg(Date.now() % 100000);
    setStage({ kind: "preview", blob, url: URL.createObjectURL(blob) });
  });
  document.getElementById("retake")?.addEventListener("click", () => setStage({ kind: "idle" }));
  document.getElementById("again")?.addEventListener("click", () => setStage({ kind: "idle" }));
  document.getElementById("send")?.addEventListener("click", () => {
    if (stage.kind === "preview") void send(stage.blob, stage.url);
  });
}

async function send(blob: Blob, url: string): Promise<void> {
  setStage({ kind: "uploading", blob, url, percent: 0 });
  const form = new FormData();
  form.append("photo", blob, "photo.jpg");
  try {
    await upload(`/api/capture/${encodeURIComponent(token)}/photo`, form, (p) => {
      if (stage.kind !== "uploading") return;
      stage.percent = p;
      const bar = phone.querySelector<HTMLElement>(".bar i");
      if (bar) bar.style.width = `${p}%`;
      const label = document.getElementById("upLabel");
      if (label) label.textContent = `보내는 중 ${p}%`;
    });
    setStage({ kind: "done" });
  } catch (e) {
    const status = e instanceof ApiError ? e.status : 0;
    if (status === 410) setStage({ kind: "expired" });
    else if (status === 409) setStage({ kind: "reported" });
    else {
      toast(errorMessage(e));
      setStage({ kind: "preview", blob, url });
    }
  }
}

function tick(): void {
  const el = document.getElementById("capTimer");
  const left = remaining();
  if (el) {
    el.textContent = fmtRemain(left);
    el.classList.toggle("low", left < 60_000);
  }
  // 올리는 중이거나 이미 보낸 뒤에는 화면을 바꾸지 않습니다.
  if (info && left <= 0 && (stage.kind === "idle" || stage.kind === "preview")) setStage({ kind: "expired" });
}

async function load(): Promise<void> {
  if (!token) {
    setStage({ kind: "error", message: "촬영 링크가 올바르지 않습니다. PC의 QR을 다시 스캔해 주세요." });
    return;
  }
  try {
    const res = await fetch(`/api/capture/${encodeURIComponent(token)}`);
    syncClock(res);
    if (!res.ok) throw await toApiError(res);
    info = (await res.json()) as CaptureInfo;
    setStage(info.state === "Ready" ? { kind: "idle" } : info.state === "Reported" ? { kind: "reported" } : { kind: "expired" });
    timer = window.setInterval(tick, 1000);
  } catch (e) {
    setStage({ kind: "error", message: errorMessage(e) });
  }
}

window.addEventListener("pagehide", () => window.clearInterval(timer));
void load();
