/** 서버 DTO(Contracts/Dtos.cs)와 1:1 */

export type DraftStatus = "Processing" | "Rendering" | "Review" | "Approved" | "Ready" | "Published" | "Rejected" | "Failed";
export type FindingSource = "Rule" | "Ai" | "Similar";

export const STATUS_LABEL: Record<DraftStatus, string> = {
  Processing: "분석 중",
  Rendering: "미리보기 만드는 중",
  Review: "검토 대기",
  Approved: "승인 · 완성본 만드는 중",
  Ready: "완성본 준비",
  Published: "게시 완료",
  Rejected: "반려",
  Failed: "실패",
};

/** 서버가 일하는 중(화면이 주기적으로 다시 읽어야 하는) 상태 */
export const isBusy = (s: DraftStatus): boolean => s === "Processing" || s === "Rendering" || s === "Approved";

export interface DraftListItem {
  id: number;
  code: string;
  status: DraftStatus;
  stage: string | null;
  error: string | null;
  title: string;
  originalName: string;
  durationMs: number;
  outputDurationMs: number;
  openFindings: number;
  previewVersion: number;
  uploadedByName: string;
  createdAt: string;
}

export interface Cue {
  id: number;
  startMs: number;
  endMs: number;
  text: string;
}

export interface Cut {
  index: number;
  startMs: number;
  endMs: number;
  removed: boolean;
}

export interface Finding {
  id: number;
  source: FindingSource;
  where: string;
  rule: string;
  excerpt: string;
  reason: string;
  blocking: boolean;
  resolution: string | null;
  resolvedByName: string | null;
  resolvedAt: string | null;
}

export interface Draft {
  id: number;
  code: string;
  status: DraftStatus;
  stage: string | null;
  error: string | null;
  version: number;
  previewVersion: number;
  source: {
    originalName: string;
    sizeBytes: number;
    durationMs: number;
    width: number;
    height: number;
    uploadedByName: string;
    createdAt: string;
    sttModel: string | null;
    sttRuntime: string | null;
    sttElapsedMs: number;
  };
  outputDurationMs: number;
  hook: string;
  hookCandidates: string[];
  title: string;
  titleCandidates: string[];
  summary: string;
  description: string;
  hashtags: string;
  emphasis: string[];
  aiNote: string | null;
  hasPatient: boolean;
  consentConfirmed: boolean;
  cues: Cue[];
  cuts: Cut[];
  findings: Finding[];
  approvalBlockers: string[];
  updatedByName: string | null;
  updatedAt: string;
  approvedByName: string | null;
  approvedAt: string | null;
  rejectReason: string | null;
  rejectedByName: string | null;
  youTubeUrl: string | null;
  relatedVideoLinked: boolean;
  publishedAt: string | null;
  publishedByName: string | null;
}

export interface SystemStatus {
  ffmpeg: string | null;
  whisperModel: boolean;
  ai: string;
  aiEnabled: boolean;
  maxUploadMegabytes: number;
  titleHistoryCount: number;
  canApprove: boolean;
}

export interface TitleImportResult {
  added: number;
  updated: number;
  total: number;
}
