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
  if (!Number.isNaN(d)) clockOffsetMs = d - Date.now();
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
  return (await res.json()) as T;
}

export const errorMessage = (e: unknown): string =>
  e instanceof ApiError ? e.message : e instanceof Error ? e.message : "알 수 없는 오류가 발생했습니다.";
