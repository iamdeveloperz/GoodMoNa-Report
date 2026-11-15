# GoodMoNa — 2번 과제: 사내 인트라넷

굿모나의원 직원이 매일 쓰는 인트라넷입니다. 직원 계정(가입 → 승인)과 로그인 위에 **공지 · 연차 · 출퇴근 · 직원 주차 · 주소록 · CS 안내 도우미**를 올렸고,
이 로그인으로 1번 과제(보고·결과 확인)의 간호사·원장 권한도 함께 나눕니다.
과제 원문: 기본 인트라넷 형태 + 연차 관리 + 차 관리 + LLM 위키로 CS 응대. 전체 설계는 [../docs/architecture.md](../docs/architecture.md), 인트라넷 기능 설계(법 근거·대안)는 [docs/intranet-design.md](docs/intranet-design.md), CS 안내 도우미(LLM 위키) 설계는 [docs/cs-assistant-design.md](docs/cs-assistant-design.md).

| 주소 | 화면 | 누가 |
| --- | --- | --- |
| `/login` | 로그인 | 누구나 |
| `/signup` | 가입 신청 (관리자가 없고 서버 PC에서 열면 첫 관리자 만들기) | 누구나 |
| `/intranet` | 홈: 확인 안 한 필독 공지 · 오늘 출퇴근(바로 출근/퇴근) · 쓸 수 있는 연차 · 오늘 휴가자 · 최근 공지 · (관리자) 처리할 일 · 바로가기 | 로그인한 직원 |
| `/intranet/notices` | 공지: 목록 \| 본문, 필독 · 읽음 확인 (관리자: 쓰기·고치기·지우기·안 읽은 사람) | 로그인한 직원 |
| `/intranet/leave` | 연차: 내 잔여 · 휴가 신청 · 내 신청(취소) · 팀 달력 (관리자: 승인 대기 · 직원별 잔여 · 수동 조정) | 로그인한 직원 |
| `/intranet/attendance` | 출퇴근: 출근 · 퇴근 · 이번 달 기록 (관리자: 하루 현황 · 사유 남기고 수정) | 로그인한 직원 |
| `/intranet/attendance/kiosk` | 입구 QR 화면: 30초마다 바뀌는 출퇴근 QR (입구 태블릿용) | 대표원장·관리자 |
| `/intranet/parking` | 주차: 번호 뒷자리로 차주 찾기 · 내 주차 · 내 차량 (관리자: 배정 신청 대기 · 주차면 · 배정) | 로그인한 직원 |
| `/intranet/people` | 주소록 · 내 부서·연락처 수정 (입사일은 관리자에게만) | 로그인한 직원 |
| `/intranet/ask` | CS 도우미: 고객 문의 → (켜져 있으면) AI 답변 · FAQ · 안내 문서, 답 복사, 도움됨/부족함, 내 최근 질문, 자료 제안, 전체 자료 | 로그인한 직원 |
| `/intranet/knowledge` | 자료 관리: 처리할 질문(표현 추가 · FAQ로 만들기 · 닫기) · 승인 대기 · 자료 가져오기(미리 보기 → 초안, AI 정리 지시문) · 승인된 자료(재검토일 · 내리기) · 가져오기 기록 | 대표원장·관리자 |
| `/intranet/admin` | 직원 관리: 가입 승인·반려, 직무 변경, **입사일**, 사용 중지 | 대표원장·관리자 |
| `/intranet/settings` | 설정: 근무 요일·시간 · 같은 직무 휴가 경고 인원 · 출퇴근 확인(병원 IP) · 쉬는 날 | 대표원장·관리자 |

## 요구사항과 진행

- [x] 로그인 · 회원가입 (**승인해야만 로그인 가능**) · 직무 권한
- [x] 1번 과제와 로그인 공유 (간호사·원장 구분)
- [x] 공지사항 (필독·읽음 확인)
- [x] 연차 (신청 → 승인, 근로기준법 기준 잔여 자동 계산, 팀 달력)
- [x] 출퇴근 (병원 와이파이 공인 IP 또는 입구 QR)
- [x] 차 관리 = **직원 주차** (차량 등록 · 배정 신청 → 주차면 배정 · 이중 주차 연락)
- [x] 주소록
- [ ] 품의·결재 (연차 승인 흐름을 일반화)
- [ ] 사내 메신저 (1:1·부서방, SignalR)
- [x] LLM 위키로 CS 응대 = **CS 안내 도우미** (프로토타입): 자료 추출 → 초안 → 승인 → 검색, 못 찾은 질문 → FAQ(학습 고리), 출처 검사 AI 답변 연결부
- [x] CS 도우미 AI 답변: 서버 PC 로컬 모델(Ollama `qwen3:4b-instruct`) — `deploy/set-assistant.ps1`로 로컬·Claude API·끄기 전환
- [ ] 평가 묶음으로 답 품질·비용 측정, 로컬 모델(Ollama)로 옮길지 결정, 영상 대본 바로 가져오기(Whisper)
- [ ] 홈 화면 설치(PWA)·웹 푸시 알림

## 업무 규칙

### 연차 (근로기준법 제60조, 입사일 기준)

| 항목 | 규칙 |
| --- | --- |
| 발생 | 입사 1년 미만: 1개월 개근마다 1일(최대 11일, 입사 1년 되는 날 전까지 사용) · 1년 이상: 15일 · 3년 이상: 최초 1년을 넘는 근속 2년마다 1일 가산, 최대 25일 |
| 연차 기간 | 입사 기념일 ~ 다음 기념일 전날. 남은 연차는 기간이 끝나면 사라짐(이월은 관리자 수동 조정) |
| 출근율 80% | 출퇴근 기록과 별개로 "충족"으로 봄. 미달 등 다른 경우는 관리자가 조정 |
| 입사일 | 관리자가 직원 관리에서 입력. 없으면 연차(차감)는 신청 불가, 병가·경조·공가는 가능 |
| 종류 | 연차(차감) · 병가 · 경조 · 공가(차감 안 함). 단위: 종일 · 오전 반차 · 오후 반차(0.5일, 하루만). 오전·오후 반차는 같은 날 함께 가능 |
| 일수 | 근무 요일만 셈. 공휴일·휴진일 제외. 휴무일을 나중에 추가해도 다시 계산됨(일수를 저장하지 않음) |
| 잔여 | 발생 + 조정 − 사용(승인) = 남은 연차, 여기서 승인 대기분을 뺀 것이 "쓸 수 있는 연차" |
| 신청 제한 | 쓸 수 있는 연차 이내 · 1년 미만은 휴가 마지막 날까지 생기는 만큼만(당겨 쓰기 없음) · 연차 기간을 넘는 신청은 나눠서 · 지난 날짜는 30일 전까지 · 한 번에 60일까지 |
| 승인 | 대표원장·관리자. 관리자는 자기 신청을 승인할 수 없음(대표원장은 가능). 승인 때 남은 연차를 다시 확인 |
| 취소 | 본인: 승인 대기 중이거나 아직 시작 안 한 승인 건 · 대표원장·관리자: 언제든(사유 기록) |
| 팀 달력 | 대기·승인 휴가, 공휴일·휴진일. 같은 직무가 경고 인원(기본 2명) 이상 쉬는 근무일은 빨간 테두리 |

### 출퇴근

| 항목 | 규칙 |
| --- | --- |
| 병원에 있는지 확인 | ① 병원 네트워크: 접속 IP(Cloudflare가 주는 `Cf-Connecting-IP`)가 설정의 공인 IP·대역에 있으면 통과 ② 입구 QR: 키오스크 화면의 30초마다 바뀌는 코드(HMAC-SHA256, 직전 코드까지 허용)로 연 경우 통과 |
| 확인 끄기 | 기본은 꺼짐: 어디서든 기록, "확인 없음"으로 표시. 켜면 ①·② 중 하나가 필요(아니면 403) |
| 하루 기록 | 직원·날짜(병원 시간대)마다 하나. 출근은 한 번, 퇴근은 다시 누르면 마지막 시각. 자정을 넘긴 퇴근은 16시간 안의 전날 기록에 붙음 |
| 지각 · 조퇴 | 근무 시작 1분 이후 출근 = 지각, 근무 끝 전 퇴근 = 조퇴. 오전 반차면 지각, 오후 반차면 조퇴를 따지지 않음 |
| 상태 | 근무 중 · 퇴근 · 휴가(승인된 종일) · 휴무(근무일 아님) · 기록 없음(지난 근무일, 계정을 만든 날·입사일 이후만) |
| 수정 | 대표원장·관리자, 사유 필수, 누가·언제·왜를 남김. 시각을 모두 비우면 그날 기록 삭제. 앞날은 불가 |

### 직원 주차

| 항목 | 규칙 |
| --- | --- |
| 차량 | 본인이 등록(최대 3대). 번호는 `12가3456` · `123가4567` · `서울12가3456` 형식, 공백·하이픈은 빼고 저장, 번호 중복 불가 |
| 배정 | 직원이 신청 → 관리자가 주차면·시작일(끝 날짜는 선택)을 정해 배정. 주차면 하나에 같은 기간 한 명, 직원 한 명에 같은 기간 한 면 |
| 차주 찾기 | 번호 2자리 이상(보통 뒤 4자리)으로 검색 → 차주 이름·부서·연락처·주차면. 로그인한 직원만 |

### CS 안내 도우미

| 항목 | 규칙 |
| --- | --- |
| 근거 | **승인된 자료만** 검색·AI 답변에 씀. 초안·반려·내린 자료는 빠짐 |
| 자료 상태 | 초안 → 승인 / 반려(사유 필수, 초안만) · 승인 → 내림(검색에서 빠짐) → 다시 승인 가능 · 승인된 자료는 내린 뒤에만 지움 |
| 누가 | 직원: 질문, 승인된 자료 보기, 자료 제안(초안). 대표원장·관리자: 가져오기·승인·반려·고치기·내리기·지우기, 처리할 질문 |
| 가져오기 | `# 큰 제목`/`## 소제목` → 문서, `Q:`/`A:` → FAQ, `분류:`·`키워드:` 줄, `<!-- -->` 무시. 구조 없는 글은 문서 1건. 본문 4,000자 넘으면 나눔. 한 번에 200,000자·300건까지. 모두 초안으로 들어옴 |
| 검색 | 글자 2개 단위 + BM25, 제목·보조어 두 배. 질문 조각의 40% 이상이 든 자료 = 찾음. FAQ 3 · 문서 5 · 못 찾으면 관련 자료 3 |
| 학습 고리 | 못 찾은 질문·"부족함" → 처리할 질문 → 기존 자료에 표현 추가(검색 보조어, 60자 이하) · FAQ로 만들기 · 닫기 |
| 재검토일 | 지난 승인 자료는 "재검토일 지남" 표시 |
| 개인정보 | 질문의 전화번호·주민번호는 저장 전에 가림. 환자 기록(01)은 보지 않음 |
| AI 답변 | 기본 꺼짐. `Mode`: `Claude`(Claude API, 가린 질문과 찾은 자료 4건이 전송됨) 또는 `Ollama`(서버 PC 로컬). 찾은 자료 상위 4건만 근거로 답하고, 근거 번호 검사를 통과해야 보여 줌. 못 찾으면 AI를 부르지 않음, 실패하면 검색 결과만 |

## 로그인 구조

직원 DB는 이 서버(02)에만 있습니다. 01 진료 서버는 직원 DB를 보지 않고, 02가 발급한 **출입증(JWT)** 의 서명과 직무만 확인합니다.

```
① /login (02) — 아이디·비밀번호 확인 → 출입증 발급
     gm_access  : JWT RS256, 15분, { sub: 직원번호, name, role }  · 경로 /  (01도 받음)
     gm_refresh : 무작위 토큰(DB엔 SHA-256만), 14일 · 경로 /api/auth (02만 받음)
② /report (01) — 쿠키의 출입증 서명을 02의 공개키(/.well-known/jwks.json)로 확인 → 직무로 허용/거절
③ 15분 뒤 — 화면이 /api/auth/refresh를 불러 새 출입증. 이때 DB에서 재직·직무를 다시 확인
```

| 항목 | 규칙 |
| --- | --- |
| 가입 | 신청하면 `Pending`. 대표원장·관리자가 직무를 확인(바꿀 수 있음)해 승인해야 로그인 가능. 반려 사유는 로그인 시 안내 |
| 첫 관리자 | 관리자가 한 명도 없을 때, **서버 PC(localhost)에서만** `/signup`으로 바로 대표원장·관리자 생성 |
| 직무 | `Owner` 대표원장 · `Doctor` 원장 · `Nurse` 간호사 · `Staff` 직원 · `Admin` 관리자 |
| 01 권한 | 보고: 대표원장·원장·간호사 / 결과 확인·녹음: 대표원장·원장 / 기록 가져오기: 대표원장·관리자 / 폰 촬영: QR 토큰(로그인 없음) |
| 퇴사·직무 변경 | 사용 중지하면 세션을 모두 끊어 늦어도 15분 안에 01·02 모두 막힘. 직무 변경도 다음 갱신 때 반영 |
| 비밀번호 | PBKDF2(ASP.NET Identity PasswordHasher), 8자 이상, 아이디 포함 불가. 5번 틀리면 10분 잠금. 없는 아이디도 같은 시간이 걸리게 함 |
| 갱신 토큰 | 쓸 때마다 새로 바꿈. 이미 바뀐 토큰이 30초 뒤에 다시 쓰이면 도난으로 보고 그 직원의 세션을 모두 끊음 |
| 그 밖 | 로그인·가입은 IP당 1분 30회. 쿠키는 HttpOnly·SameSite(Lax/Strict)·HTTPS에서 Secure. 마지막 관리자는 중지·강등 불가 |

서명 개인키는 데이터 폴더 `keys/signing-key.pem`(운영: `C:\GoodMoNa\02\data\keys`)에만 있고 리포에 넣지 않습니다. 지우면 새 키가 생기고 모두 다시 로그인합니다.

### API

| 기능 | 경로 |
| --- | --- |
| 가입 · 첫 관리자 | `POST /api/auth/signup`, `GET/POST /api/auth/bootstrap` |
| 로그인 · 갱신 · 로그아웃 · 내 정보 | `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout`, `GET /api/auth/me` |
| 직원 관리 (대표원장·관리자) | `GET /api/admin/staff?status=`, `POST /api/admin/staff/{id}/approve · reject · disable · enable`, `PUT /api/admin/staff/{id}/role · profile`(부서·연락처·입사일) |
| 공개키 (01이 사용) | `GET /.well-known/openid-configuration`, `GET /.well-known/jwks.json` |

인트라넷 업무 API는 모두 `/api/intranet` 아래입니다(터널이 이 경로를 02로 보냄). 로그인한 직원이면 쓸 수 있고, **★는 대표원장·관리자만**.

| 기능 | 경로 |
| --- | --- |
| 홈 · 주소록 · 내 연락처 | `GET /home`, `GET /people`, `PUT /me/profile` |
| 공지 | `GET /notices`, `GET /notices/{id}`, `POST /notices/{id}/read`, ★`POST /notices`, ★`PUT · DELETE /notices/{id}`, ★`GET /notices/{id}/reads` |
| 연차 | `GET /leave/me`, `POST /leave/requests`, `POST /leave/requests/{id}/cancel`, `GET /leave/calendar?month=`, ★`GET /leave/requests?status=`, ★`POST /leave/requests/{id}/approve · reject`, ★`GET /leave/balances`, ★`GET · POST /leave/adjustments` |
| 출퇴근 | `GET /attendance/me?month=`, `POST /attendance/check-in · check-out` (본문 `{code}`: 입구 QR), ★`GET /attendance/daily?date=`, ★`GET /attendance/staff/{id}?month=`, ★`PUT /attendance/staff/{id}/{date}`, ★`GET /attendance/kiosk` |
| 주차 | `GET /parking/me`, `POST /parking/vehicles`, `DELETE /parking/vehicles/{id}`, `POST /parking/requests`, `POST /parking/requests/{id}/cancel`, `GET /parking/lookup?q=`, ★`GET · POST /parking/spots`, ★`PUT /parking/spots/{id}`, ★`GET /parking/requests`, ★`POST /parking/requests/{id}/reject`, ★`POST /parking/assignments`, ★`POST /parking/assignments/{id}/end`, ★`GET /parking/vehicles` |
| 설정 · 쉬는 날 | ★`GET · PUT /settings`, `GET /holidays?year=`, ★`POST /holidays`, ★`DELETE /holidays/{date}` |
| CS 도우미 | `POST /ask`, `POST /ask/{id}/feedback`, `GET /ask/mine`, `GET /ask/status` |
| CS 자료 | `GET /kb/items?status=&mine=&q=`, `GET /kb/items/{id}`, `POST /kb/items`(직원은 초안 제안), ★`PUT /kb/items/{id}`, ★`POST /kb/items/{id}/approve · reject · archive`, ★`DELETE /kb/items/{id}`, ★`POST /kb/approve`(여러 건), ★`POST /kb/import/preview`, ★`POST /kb/import`, ★`GET /kb/imports`, ★`GET /kb/questions?followUp=`, ★`POST /kb/questions/{id}/dismiss · attach`, ★`GET /kb/overview` |

## 구성

```
src/GoodMoNa02.Server
  Program.cs              DI · 인증 · 요청 제한 · 화면 주소
  Auth/                   SigningKeys(RSA 키) · TokenService(출입증·쿠키) · AuthSetup(JWT 확인·정책) · PageGate(화면 보호)
  Endpoints/              Auth · Admin · Home(홈·주소록) · Notice · Leave · Attendance · Parking · Settings · Knowledge(CS 자료) · Ask(CS 도우미) · Problems
  Intranet/               LeavePolicy(근로기준법 §60, 순수 계산) · LeaveLedger(잔여) · WorkCalendar(근무일) · KoreanHolidays(내장 공휴일)
                          ClinicSetup(병원 설정·휴무일·IP 확인) · AttendanceCodes(입구 QR, HMAC 30초) · Plates(차량 번호)
  Knowledge/              KbExtractor(자료 추출 규칙) · KbText(2-gram·개인정보 가림) · KbIndex(BM25 메모리 색인) · Assistant(AI 답변: 꺼짐/Ollama, 지시문, 근거 검사) · ClaudeAnswerWriter(Claude API)
  Data/                   Staff · StaffSession · IntranetEntities(공지·연차·휴무일·설정·출퇴근·주차) · KnowledgeEntities(CS 자료·가져오기·질문 기록) · AppDbContext · Migrations/
  Contracts/              DTO · StaffInput(입력 검사)
tests/GoodMoNa02.Server.Tests   통합 테스트 72개: 로그인(16) · 연차 규칙(24, 순수 계산) · 연차 흐름(10) · 출퇴근(4) · 공지·주소록·홈·주차·휴무일·보호(7) · CS 도우미(11)
web/                      login · signup · intranet · notices · leave · attendance · kiosk · parking · people · ask · knowledge · intranet-admin · settings
                          (정적 파일은 /intranet-assets/ — 01의 /assets/와 겹치지 않게. 메뉴는 src/shared/chrome.ts 한 곳)
deploy/install.ps1        서비스 GoodMoNa02 (localhost:5002)
deploy/set-assistant.ps1  CS 도우미 AI 답변 전환: -Mode Ollama -Model … / Claude(키는 관리자 창에서 입력) / Off
docs/                     intranet-design(인트라넷 기능) · cs-assistant-design(CS 안내 도우미)
samples/cs-knowledge/     CS 도우미 가상 예시 자료 (원문 대본 → AI 정리본, 안내 문서, FAQ)
```

## 실행

```powershell
dotnet tool restore
cd web; npm install; npm run build; cd ..
dotnet run --project src/GoodMoNa02.Server     # http://localhost:5012
dotnet test
```

01과 함께 개발할 때: 02 서버(5012)와 01 서버(5011)를 켜고 `01/web`에서 `npm run dev` → http://localhost:5173. 01의 Vite가 로그인 관련 경로를 02로 넘겨, 운영과 같은 한 주소로 동작합니다.

## 배포

```powershell
.\deploy\install-all.ps1     # 리포 맨 위: 02 → 01(+터널) 순서, UAC 한 번
```

설치 후 처음 한 번, **서버 PC의 브라우저에서** http://localhost:5002/signup 으로 첫 관리자(대표원장)를 만듭니다. 그 뒤 직원은 https://goodmona.hsdevs.com/signup 에서 가입 신청 → 관리자가 `/intranet/admin`에서 승인하고 **입사일**을 넣습니다(연차 계산). `/intranet/settings`에서 근무 요일과 공휴일 목록을 확인하세요(내장 공휴일은 2026~2027년). 출퇴근 확인을 쓰려면 병원 와이파이에 연결한 기기로 설정을 열어 "지금 이 기기의 IP 넣기", 입구 태블릿에는 `/intranet/attendance/kiosk`를 관리자 계정으로 열어 둡니다.

| 항목 | 값 |
| --- | --- |
| 서비스 | `GoodMoNa02` (localhost:5002) |
| 설치 · 데이터 | `C:\GoodMoNa\02\app` · `C:\GoodMoNa\02\data` (SQLite + 서명 키) |
| 운영 설정 | 데이터 폴더의 `appsettings.local.json` (배포가 덮어쓰지 않음). CS 도우미 AI 전환: `deploy\set-assistant.ps1 -Mode Ollama -Model qwen3:4b-instruct` (또는 `-Mode Claude` / `-Mode Off`, UAC 필요) — [설계 §7](docs/cs-assistant-design.md#7-ai-답변-3단계-운영-로컬-모델) |
| 외부 경로 | Cloudflare 터널(01 설치 스크립트가 설정)이 `/login`, `/signup`, `/intranet*`, `/api/auth/*`, `/api/admin/*`, `/api/intranet/*`, `/.well-known/*`를 02로 보냄 |
