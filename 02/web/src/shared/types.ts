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
  manage: { staffPending: number; leavePending: number; parkingPending: number; missingHireDate: number } | null;
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
