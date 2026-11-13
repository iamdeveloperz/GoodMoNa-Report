/** 서버 DTO 타입과, ProblemDetails를 한국어 메시지로 풀어 주는 fetch 래퍼. (01의 api.ts와 같은 규칙) */
import type { StaffRole } from "./session";

export type StaffStatus = "Pending" | "Active" | "Rejected" | "Disabled";

export interface StaffItem {
  id: number;
  loginId: string;
  name: string;
  role: StaffRole;
  requestedRole: StaffRole;
  status: StaffStatus;
  department: string | null;
  phone: string | null;
  createdAt: string;
  decidedAt: string | null;
  decidedBy: string | null;
  rejectReason: string | null;
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
  401: "로그인이 필요합니다.",
  403: "권한이 없습니다.",
  404: "요청한 항목을 찾을 수 없습니다.",
  429: "요청이 너무 많습니다. 잠시 후 다시 시도해 주세요.",
  500: "서버 오류가 발생했습니다. 잠시 후 다시 시도해 주세요.",
};

export async function toApiError(res: Response): Promise<ApiError> {
  let body: Record<string, unknown> | null = null;
  try {
    body = (await res.json()) as Record<string, unknown>;
  } catch {
    /* 본문이 JSON이 아닌 경우 */
  }
  const detail = typeof body?.detail === "string" ? body.detail : null;
  return new ApiError(detail ?? FALLBACK[res.status] ?? `요청 실패 (${res.status})`, res.status, body);
}

let refreshing: Promise<boolean> | null = null;
/** 출입증(15분) 갱신. 동시에 여러 번 불려도 한 번만 요청합니다. */
export function refreshSession(): Promise<boolean> {
  refreshing ??= fetch("/api/auth/refresh", { method: "POST" })
    .then((r) => r.ok)
    .catch(() => false)
    .finally(() => window.setTimeout(() => (refreshing = null), 0));
  return refreshing;
}

export function goLogin(): void {
  location.href = `/login?next=${encodeURIComponent(location.pathname + location.search)}`;
}

/**
 * 로그인이 필요한 API 호출. 401이면 출입증을 갱신해 한 번 다시 보내고, 그래도 안 되면 로그인 화면으로.
 * 로그인·가입 화면에서는 이 함수 대신 {@link post}를 씁니다(401을 그대로 받아야 하므로).
 */
export async function api<T>(method: string, url: string, body?: unknown, retried = false): Promise<T> {
  const res = await send(method, url, body);
  if (res.status === 401) {
    if (!retried && (await refreshSession())) return api<T>(method, url, body, true);
    goLogin();
    throw new ApiError("로그인이 필요합니다.", 401, null);
  }
  if (!res.ok) throw await toApiError(res);
  return (res.status === 204 ? undefined : await res.json()) as T;
}

/** 로그인 처리 없이 그대로 보냅니다. */
export async function post<T>(url: string, body?: unknown): Promise<T> {
  const res = await send("POST", url, body);
  if (!res.ok) throw await toApiError(res);
  return (res.status === 204 ? undefined : await res.json()) as T;
}

async function send(method: string, url: string, body?: unknown): Promise<Response> {
  try {
    return await fetch(url, {
      method,
      headers: body === undefined ? undefined : { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(FALLBACK[0]!, 0, null);
  }
}

export const errorMessage = (e: unknown): string =>
  e instanceof ApiError ? e.message : e instanceof Error ? e.message : "알 수 없는 오류가 발생했습니다.";
