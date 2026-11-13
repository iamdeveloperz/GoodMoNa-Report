import { errorMessage, serverNow, syncClock, toApiError, type CaptureInfo } from "../shared/api";
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
  // 미리보기 URL은 다음 단계에서 안 쓰면 해제합니다.
  if ("url" in stage && (!("url" in next) || next.url !== stage.url)) URL.revokeObjectURL(stage.url);
  stage = next;
  render();
}

const remaining = (): number => (info ? Date.parse(info.expiresAt) - serverNow() : 0);

function header(): string {
  const head = "<h1>굿모나 촬영</h1>";
  if (!info) return head;
  return `${head}<div class="cap-info"><span><b>${esc(info.maskedName)}</b> · ${fmtBirthShort(info.birthDate)} · 오늘</span>
    <span>남은 <span class="timer" id="capTimer">${fmtRemain(remaining())}</span></span></div>`;
}

function render(): void {
  let body = "";
  switch (stage.kind) {
    case "loading":
      body = '<p class="muted">불러오는 중…</p>';
      break;
    case "error":
      body = `<div class="done-box"><p>${esc(stage.message)}</p></div>`;
      break;
    case "expired":
      body = '<div class="done-box"><p>촬영 시간이 지났습니다.<br>PC에서 QR을 다시 띄워 주세요.</p></div>';
      break;
    case "reported":
      body = '<div class="done-box"><p>이미 보고된 방문입니다.</p></div>';
      break;
    case "idle":
      body = `<div class="ph empty">아래 버튼을 누르면<br>카메라가 열립니다</div>
        <label class="btn primary block filebtn">카메라로 촬영<input type="file" accept="image/*" capture="environment" id="camInput" aria-label="카메라로 촬영"></label>
        <button class="linkbtn" type="button" id="useSample">PC에서 시연: 샘플 사진 사용</button>`;
      break;
    case "preview":
      body = `<div class="ph"><img src="${stage.url}" alt="촬영한 사진 미리보기"></div>
        <div class="grid2"><button class="btn" type="button" id="retake">다시 찍기</button><button class="btn primary" type="button" id="send">보내기</button></div>`;
      break;
    case "uploading":
      body = `<div class="ph"><img src="${stage.url}" alt="촬영한 사진 미리보기"></div>
        <div class="progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${stage.percent}"><i style="width:${stage.percent}%"></i></div>
        <p class="muted" style="text-align:center">보내는 중 ${stage.percent}%</p>`;
      break;
    case "done":
      body = `<div class="done-box"><div class="check">✓</div><b>전송 완료</b><span class="muted">PC 화면을 확인하세요</span></div>
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
    if (stage.kind === "preview") void upload(stage.blob, stage.url);
  });
}

/** fetch는 업로드 진행률을 주지 않아서 XHR을 씁니다. */
function upload(blob: Blob, url: string): Promise<void> {
  setStage({ kind: "uploading", blob, url, percent: 0 });
  return new Promise((resolve) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", `/api/capture/${encodeURIComponent(token)}/photo`);
    xhr.upload.onprogress = (e) => {
      if (!e.lengthComputable || stage.kind !== "uploading") return;
      stage.percent = Math.min(99, Math.round((e.loaded / e.total) * 100));
      const bar = phone.querySelector<HTMLElement>(".progress i");
      if (bar) bar.style.width = `${stage.percent}%`;
      const label = phone.querySelector<HTMLElement>(".progress + p");
      if (label) label.textContent = `보내는 중 ${stage.percent}%`;
    };
    xhr.onload = async () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        setStage({ kind: "done" });
      } else {
        const err = await toApiError(new Response(xhr.responseText, { status: xhr.status }));
        if (xhr.status === 410) setStage({ kind: "expired" });
        else if (xhr.status === 409) setStage({ kind: "reported" });
        else {
          toast(err.message);
          setStage({ kind: "preview", blob, url });
        }
      }
      resolve();
    };
    xhr.onerror = () => {
      toast("전송에 실패했습니다. 연결을 확인하고 다시 보내 주세요.");
      setStage({ kind: "preview", blob, url });
      resolve();
    };
    const form = new FormData();
    form.append("photo", blob, "photo.jpg");
    xhr.send(form);
  });
}

function tick(): void {
  const el = document.getElementById("capTimer");
  if (el) el.textContent = fmtRemain(remaining());
  // 업로드 중이거나 이미 보낸 뒤에는 화면을 바꾸지 않습니다.
  if (info && remaining() <= 0 && (stage.kind === "idle" || stage.kind === "preview")) {
    setStage({ kind: "expired" });
  }
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
