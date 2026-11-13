/** 서버 DTO와 1:1로 맞춘 타입과, ProblemDetails를 한국어 메시지로 풀어 주는 fetch 래퍼. */

export type VisitStatus = "Draft" | "Reported";
export type Progress = "Improved" | "Unchanged" | "Worsened";
export type CaptureState = "Ready" | "Expired" | "Reported";

export const PROGRESS_LABEL: Record<Progress, string> = { Improved: "호전", Unchanged: "유지", Worsened: "악화" };

export interface PatientSummary {
  id: number;
  name: string;
  birthDate: string; // YYYY-MM-DD
  lastVisitDate: string | null;
  todayStatus: VisitStatus | null;
}

export interface Patient {
  id: number;
  name: string;
  birthDate: string;
}

export interface Photo {
  id: number;
  url: string;
  takenAt: string; // ISO UTC
}

export interface Visit {
  id: number;
  patient: Patient;
  visitDate: string;
  status: VisitStatus;
  progress: Progress | null;
  memo: string;
  photo: Photo | null;
  previous: { visitDate: string; daysAgo: number; photo: Photo } | null;
  reportedAt: string | null;
  reportedBy: string | null;
}

export interface CaptureToken {
  token: string;
  captureUrl: string;
  expiresAt: string;
}

export interface CaptureInfo {
  maskedName: string;
  birthDate: string;
  visitDate: string;
  expiresAt: string;
  state: CaptureState;
}

export type NoteStatus = "Queued" | "Transcribing" | "Transcribed" | "Failed";
export type NoteSource = "InApp" | "FilePick" | "NativeApp";

export interface Segment {
  startMs: number;
  endMs: number;
  text: string;
}

export interface Note {
  id: number;
  visitId: number;
  seq: number;
  status: NoteStatus;
  error: string | null;
  durationMs: number;
  source: NoteSource;
  createdAt: string;
  audioUrl: string;
  transcript: { text: string; segments: Segment[]; model: string; runtime: string; elapsedMs: number; createdAt: string } | null;
}

export interface ReportListItem {
  visitId: number;
  patientId: number;
  patientName: string;
  birthDate: string;
  progress: Progress | null; // 이전 기록을 가져온 방문은 경과가 비어 있을 수 있음
  reportedAt: string;
  reportedBy: string | null;
  noteCount: number;
  pendingNoteCount: number;
}

export interface HistoryVisit {
  visitId: number;
  visitDate: string;
  status: VisitStatus;
  progress: Progress | null;
  memo: string;
  photo: Photo | null;
  notes: Note[];
}

export interface PatientHistory {
  patient: Patient;
  visits: HistoryVisit[];
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly body: Record<string, unknown> | null,
  ) {
    super(message);
  }
}

const FALLBACK: Record<number, string> = {
  0: "서버에 연결할 수 없습니다. 네트워크를 확인해 주세요.",
  404: "요청한 항목을 찾을 수 없습니다.",
  413: "파일이 너무 큽니다.",
  500: "서버 오류가 발생했습니다. 잠시 후 다시 시도해 주세요.",
};

export async function toApiError(res: Response): Promise<ApiError> {
  let body: Record<string, unknown> | null = null;
  try {
    body = (await res.json()) as Record<string, unknown>;
  } catch {
    /* 본문이 JSON이 아닌 경우 (프록시 오류 등) */
  }
  const detail = typeof body?.detail === "string" ? body.detail : null;
  return new ApiError(detail ?? FALLBACK[res.status] ?? `요청 실패 (${res.status})`, res.status, body);
}

/**
 * 폰·PC 시계가 서버와 어긋나도 QR 남은 시간이 맞도록, 응답의 Date 헤더로 서버 시각과의 차이를 기억합니다.
 * (초 단위 정밀도면 충분합니다.)
 */
let clockOffsetMs = 0;
export const serverNow = (): number => Date.now() + clockOffsetMs;
export function syncClock(res: Response): void {
  const d = Date.parse(res.headers.get("Date") ?? "");
  if (Number.isNaN(d)) return;
  // Date 헤더는 초 단위로 잘려 오므로 2초 미만 차이는 무시합니다 (안 그러면 남은 시간이 10:01처럼 보임).
  const offset = d - Date.now();
  clockOffsetMs = Math.abs(offset) < 2000 ? 0 : offset;
}

export async function api<T>(method: string, url: string, body?: unknown): Promise<T> {
  let res: Response;
  try {
    res = await fetch(url, {
      method,
      headers: body === undefined ? undefined : { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(FALLBACK[0]!, 0, null);
  }
  syncClock(res);
  if (!res.ok) throw await toApiError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

/** 파일 업로드 (진행률이 필요해서 XHR). 성공하면 JSON 본문을 돌려줍니다. */
export function upload<T>(url: string, form: FormData, onProgress?: (percent: number) => void): Promise<T> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", url);
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) onProgress?.(Math.min(99, Math.round((e.loaded / e.total) * 100)));
    };
    xhr.onload = async () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        onProgress?.(100);
        resolve(JSON.parse(xhr.responseText) as T);
      } else {
        reject(await toApiError(new Response(xhr.responseText, { status: xhr.status })));
      }
    };
    xhr.onerror = () => reject(new ApiError("전송에 실패했습니다. 연결을 확인하고 다시 시도해 주세요.", 0, null));
    xhr.send(form);
  });
}

export const errorMessage = (e: unknown): string =>
  e instanceof ApiError ? e.message : e instanceof Error ? e.message : "알 수 없는 오류가 발생했습니다.";
