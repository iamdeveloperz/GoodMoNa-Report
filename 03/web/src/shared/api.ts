/** ProblemDetails를 한국어 메시지로 풀어 주는 fetch 래퍼 + 출입증 자동 갱신 (01 web/src/shared/api.ts와 같은 방식). */

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
 * 출입증(gm_access)은 15분짜리입니다. 만료되어 401이 오면 02 인트라넷의 /api/auth/refresh로 새로 받고 한 번 다시 보냅니다.
 * 갱신도 안 되면(로그아웃·퇴사) 로그인 화면으로 보냅니다.
 */
let refreshing: Promise<boolean> | null = null;
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

export async function api<T>(method: string, url: string, body?: unknown, retried = false): Promise<T> {
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
  if (res.status === 401) {
    if (!retried && (await refreshSession())) return api<T>(method, url, body, true);
    goLogin();
    throw new ApiError("로그인이 필요합니다.", 401, null);
  }
  if (!res.ok) throw await toApiError(res);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

/**
 * 영상 파일을 본문 그대로 올립니다(진행률 때문에 XHR). 서버는 멀티파트가 아니라 원본 바이트를 바로 디스크에 씁니다.
 */
export function uploadFile<T>(url: string, file: File, onProgress?: (percent: number) => void, retried = false): Promise<T> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", url);
    xhr.setRequestHeader("Content-Type", file.type.startsWith("video/") ? file.type : "application/octet-stream");
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) onProgress?.(Math.min(99, Math.round((e.loaded / e.total) * 100)));
    };
    xhr.onload = async () => {
      if (xhr.status === 401) {
        if (!retried && (await refreshSession())) {
          uploadFile<T>(url, file, onProgress, true).then(resolve, reject);
          return;
        }
        goLogin();
        reject(new ApiError("로그인이 필요합니다.", 401, null));
        return;
      }
      if (xhr.status >= 200 && xhr.status < 300) {
        onProgress?.(100);
        resolve(JSON.parse(xhr.responseText) as T);
      } else {
        reject(await toApiError(new Response(xhr.responseText, { status: xhr.status })));
      }
    };
    xhr.onerror = () => reject(new ApiError("전송에 실패했습니다. 연결을 확인하고 다시 시도해 주세요.", 0, null));
    xhr.send(file);
  });
}

export const errorMessage = (e: unknown): string =>
  e instanceof ApiError ? e.message : e instanceof Error ? e.message : "알 수 없는 오류가 발생했습니다.";
