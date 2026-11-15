/** 인트라넷 업무 API의 응답 형식 (서버 Contracts/IntranetDtos.cs와 같은 모양) */
import type { StaffRole } from "./session";

export type LeaveType = "Annual" | "Sick" | "Family" | "Official";
export type LeavePortion = "Full" | "AmHalf" | "PmHalf";
export type LeaveStatus = "Pending" | "Approved" | "Rejected" | "Cancelled";
export type AttendanceMethod = "Network" | "Qr" | "Unverified" | "Manual";
export type DayStatus = "Off" | "Leave" | "Working" | "Done" | "Absent" | "None";
export type ParkingRequestStatus = "Pending" | "Assigned" | "Rejected" | "Cancelled";

export interface LeaveBalance {
  periodStart: string;
  periodLastDay: string;
  serviceYears: number;
  granted: number;
  adjusted: number;
  used: number;
  pending: number;
  remaining: number;
  available: number;
  nextGrantDate: string;
  nextGrantDays: number;
}

export interface LeaveRequest {
  id: number;
  staffId: number;
  staffName: string;
  role: StaffRole;
  department: string | null;
  type: LeaveType;
  portion: LeavePortion;
  startDate: string;
  endDate: string;
  days: number;
  reason: string | null;
  status: LeaveStatus;
  createdAt: string;
  decidedAt: string | null;
  decidedByName: string | null;
  decisionNote: string | null;
}

export interface LeaveAdjustment {
  id: number;
  staffId: number;
  days: number;
  effectiveDate: string;
  reason: string;
  createdAt: string;
  createdByName: string;
}

export interface MyLeave {
  hireDate: string | null;
  balance: LeaveBalance | null;
  requests: LeaveRequest[];
  adjustments: LeaveAdjustment[];
}

export interface PendingLeave {
  request: LeaveRequest;
  balance: LeaveBalance | null;
}

export interface CalendarLeave {
  id: number;
  staffId: number;
  name: string;
  role: StaffRole;
  type: LeaveType;
  portion: LeavePortion;
  startDate: string;
  endDate: string;
  status: LeaveStatus;
}

export interface Holiday {
  date: string;
  name: string;
  builtIn: boolean;
}

export interface LeaveCalendar {
  month: string;
  workDays: number[];
  holidays: Holiday[];
  items: CalendarLeave[];
  warnSameRole: number;
}

export interface StaffBalance {
  staffId: number;
  name: string;
  role: StaffRole;
  department: string | null;
  hireDate: string | null;
  balance: LeaveBalance | null;
}

export interface NoticeListItem {
  id: number;
  title: string;
  required: boolean;
  authorName: string;
  createdAt: string;
  updatedAt: string | null;
  read: boolean;
  readCount: number;
  targetCount: number;
}

export interface Notice {
  id: number;
  title: string;
  body: string;
  required: boolean;
  authorId: number;
  authorName: string;
  createdAt: string;
  updatedAt: string | null;
  readAt: string | null;
}

export interface NoticeReader {
  staffId: number;
  name: string;
  role: StaffRole;
  department: string | null;
  readAt: string | null;
}

export interface AttendanceDay {
  date: string;
  workday: boolean;
  holidayName: string | null;
  checkInAt: string | null;
  checkOutAt: string | null;
  checkInMethod: AttendanceMethod | null;
  checkOutMethod: AttendanceMethod | null;
  late: boolean;
  earlyLeave: boolean;
  leaveType: LeaveType | null;
  leavePortion: LeavePortion | null;
  status: DayStatus;
  editReason: string | null;
  editedByName: string | null;
}

export interface MyAttendance {
  month: string;
  workStart: string;
  workEnd: string;
  verify: { required: boolean; onClinicNetwork: boolean; ip: string | null };
  today: AttendanceDay;
  days: AttendanceDay[];
}

export interface DailyAttendance {
  date: string;
  workday: boolean;
  holidayName: string | null;
  rows: { staffId: number; name: string; role: StaffRole; department: string | null; day: AttendanceDay }[];
}

export interface Person {
  id: number;
  name: string;
  role: StaffRole;
  department: string | null;
  phone: string | null;
  hireDate: string | null;
}

export interface Home {
  today: string;
  workday: boolean;
  holidayName: string | null;
  attendance: AttendanceDay | null;
  leave: LeaveBalance | null;
  requiredUnread: NoticeListItem[];
  recentNotices: NoticeListItem[];
  offToday: CalendarLeave[];
  manage: { staffPending: number; leavePending: number; parkingPending: number; missingHireDate: number; kbPending: number } | null;
}

export interface Settings {
  workDays: number[];
  workStart: string;
  workEnd: string;
  attendanceVerify: boolean;
  attendanceNetworks: string[];
  leaveWarnSameRole: number;
  yourIp: string | null;
}

export interface Vehicle {
  id: number;
  staffId: number;
  plate: string;
  model: string | null;
}

export interface ParkingAssignment {
  id: number;
  spotId: number;
  spotName: string;
  spotNote: string | null;
  staffId: number;
  staffName: string;
  plate: string | null;
  startDate: string;
  endDate: string | null;
  assignedByName: string;
}

export interface ParkingRequest {
  id: number;
  staffId: number;
  staffName: string;
  role: StaffRole;
  department: string | null;
  vehicleId: number | null;
  plate: string | null;
  note: string | null;
  status: ParkingRequestStatus;
  createdAt: string;
}

export interface MyParking {
  vehicles: Vehicle[];
  assignment: ParkingAssignment | null;
  request: ParkingRequest | null;
}

export interface Spot {
  id: number;
  name: string;
  note: string | null;
  active: boolean;
  current: ParkingAssignment | null;
  next: ParkingAssignment | null;
}

export interface PlateHit {
  plate: string;
  model: string | null;
  ownerName: string;
  role: StaffRole;
  department: string | null;
  phone: string | null;
  spotName: string | null;
}

export const LEAVE_TYPE: Record<LeaveType, string> = { Annual: "연차", Sick: "병가", Family: "경조", Official: "공가" };
export const PORTION: Record<LeavePortion, string> = { Full: "종일", AmHalf: "오전 반차", PmHalf: "오후 반차" };
export const LEAVE_STATUS: Record<LeaveStatus, string> = { Pending: "승인 대기", Approved: "승인", Rejected: "반려", Cancelled: "취소" };
export const METHOD: Record<AttendanceMethod, string> = { Network: "병원 네트워크", Qr: "입구 QR", Unverified: "확인 없음", Manual: "관리자 수정" };
export const DAY_STATUS: Record<DayStatus, string> = { Off: "휴무", Leave: "휴가", Working: "근무 중", Done: "퇴근", Absent: "기록 없음", None: "" };
export const WEEKDAYS = ["일", "월", "화", "수", "목", "금", "토"];

/* ---------- CS 안내 도우미 (서버 Contracts/KnowledgeDtos.cs) ---------- */

export type KbKind = "Faq" | "Doc";
export type KbStatus = "Draft" | "Published" | "Rejected" | "Archived";
export type KbSource = "Manual" | "Import" | "Question";
export type AskFollowUp = "None" | "Open" | "Resolved" | "Dismissed";

export interface KbItem {
  id: number;
  kind: KbKind;
  status: KbStatus;
  title: string;
  body: string;
  category: string;
  keywords: string;
  source: KbSource;
  sourceRef: string | null;
  importId: number | null;
  reviewBy: string | null;
  stale: boolean;
  createdAt: string;
  createdByName: string;
  updatedAt: string | null;
  decidedAt: string | null;
  decidedByName: string | null;
  rejectReason: string | null;
}

export interface KbDraft {
  kind: KbKind;
  title: string;
  body: string;
  category: string;
  keywords: string;
}

export interface KbImportPreview {
  items: KbDraft[];
  warnings: string[];
}

export interface KbImport {
  id: number;
  name: string;
  origin: string;
  itemCount: number;
  createdAt: string;
  createdByName: string;
}

export interface AssistantStatus {
  enabled: boolean;
  mode: string;
  model: string | null;
}

export interface KbOverview {
  drafts: number;
  published: number;
  stale: number;
  openQuestions: number;
  assistant: AssistantStatus;
  suggestions: number;
  watchPages: number;
  lastRun: KbCurateResult | null;
}

export interface KbCurateResult {
  at: string;
  full: boolean;
  drafts: number;
  keywordSuggestions: number;
  pagesChecked: number;
  siteChanges: number;
  skipped: number;
  errors: string[];
}

export type KbSuggestionKind = "Keywords" | "SiteChange";

export interface KbSuggestion {
  id: number;
  kind: KbSuggestionKind;
  status: "Pending" | "Accepted" | "Dismissed";
  itemId: number | null;
  itemTitle: string | null;
  itemBody: string | null;
  title: string;
  detail: string;
  phrases: string[];
  proposedBody: string | null;
  sourceUrl: string | null;
  createdAt: string;
}

export interface KbWatchPage {
  id: number;
  url: string;
  lastCheckedAt: string | null;
  lastChangedAt: string | null;
  lastError: string | null;
  lines: number;
}

export interface AskHit {
  id: number;
  kind: KbKind;
  title: string;
  category: string;
  snippet: string;
  body: string;
  coverage: number;
  reviewBy: string | null;
  stale: boolean;
}

export interface AskResult {
  logId: number;
  question: string;
  found: boolean;
  faqs: AskHit[];
  docs: AskHit[];
  related: AskHit[];
  answer: { text: string; sources: { n: number; itemId: number; title: string }[] } | null;
  note: string | null;
  aiDeclined: boolean;
}

export interface AskLog {
  id: number;
  question: string;
  staffId: number;
  staffName: string;
  createdAt: string;
  found: boolean;
  answered: boolean;
  helpful: boolean | null;
  followUp: AskFollowUp;
  topItemId: number | null;
  topItemTitle: string | null;
  topCoverage: number;
  resolvedItemId: number | null;
  aiDeclined: boolean;
  correction: string | null;
  draftItemId: number | null;
}

export const KB_KIND: Record<KbKind, string> = { Faq: "FAQ", Doc: "문서" };
export const KB_STATUS: Record<KbStatus, string> = { Draft: "초안", Published: "승인", Rejected: "반려", Archived: "내림" };
export const KB_SOURCE: Record<KbSource, string> = { Manual: "직접 작성", Import: "가져오기", Question: "직원 질문" };
