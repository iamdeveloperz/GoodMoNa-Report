# GoodMoNa 시스템 설계

GoodMoNa 병원 경과 보고 시스템(PMS)의 서버·언어·프레임워크·구조를 한곳에 모은 문서입니다.
이 리포에서 작업을 이어받는 사람(또는 에이전트)이 먼저 읽도록 썼습니다. 과제별 상세 사용법은 각 폴더의 README에 있습니다.

> 이 문서는 **지금(과제 시연) 구조**입니다. 실제 병원 운영으로 옮길 때의 구성(NAS · 맥미니 · 클라우드, 망 분리, 백업, 배포)은 [운영 구현 설계안](implementation-plan.md)에, 그 구성을 고른 이유와 대안 비교(클라우드 · 맥미니 · NAS · EMR · CRM · 그룹웨어)는 [서버·인프라 대안 비교](infrastructure-options.md)에 있습니다.

## 1. 한눈에

```
폰·PC·iPad 브라우저
   │ https://goodmona.hsdevs.com/…
   ▼
Cloudflare (TLS · DNS)
   ▲ 터널(이 PC에서 바깥으로 나가는 연결만, 포트포워딩 없음)
cloudflared  ── Windows 서비스 GoodMoNa01Tunnel
   │ http://localhost:5001
   ▼
ASP.NET Core 서버 ── Windows 서비스 GoodMoNa01
   ├─ 정적 화면 (Vite 빌드 결과 wwwroot)
   ├─ Minimal API  /api/*
   ├─ SignalR      /hubs/visits   (사진 도착 · 새 보고 · 녹음 상태)
   ├─ 백그라운드   Whisper 변환 큐 · 이전 기록 가져오기
   └─ 저장소       SQLite 1개 + 사진·녹음 파일 (C:\GoodMoNa\01\data)
```

서버는 클라우드가 아니라 **개인 PC(Windows 11)를 24시간 켜 두고** 씁니다. 처음엔 Oracle Cloud VM + Caddy를 검토했으나, 같은 일을 Cloudflare Tunnel로 공유기 설정 없이 할 수 있어 바꿨습니다.

## 2. 기술 스택과 고른 이유

| 영역 | 선택 | 이유 |
| --- | --- | --- |
| 언어 · 런타임 | C# · .NET 10 (SDK 10.0.400, `global.json`) | Windows 서비스로 바로 돌릴 수 있고, 한 프로세스에 API·실시간·백그라운드 작업을 다 담을 수 있음 |
| 웹 서버 | ASP.NET Core Minimal API (Kestrel) | 컨트롤러 없이 기능별 `Endpoints/*.cs` 파일 하나씩. 오류는 모두 ProblemDetails + 한국어 `detail` |
| 실시간 | SignalR (JSON, enum은 문자열) | 폰 촬영 → PC 화면, 보고 → 원장 iPad, 녹음 변환 상태를 새로고침 없이 전달 |
| DB | EF Core 10 + SQLite (파일 1개) | 병원 한 곳·PC 한 대 규모에 충분, 백업은 파일 복사. 마이그레이션은 서버 시작 시 자동 적용 |
| 음성 → 글 | Whisper.net 1.9.1 + `ggml-large-v3-turbo.bin` (약 1.6GB) | 이 PC에서 무료로, 외부로 음성을 보내지 않음. Vulkan GPU 우선, 안 되면 CPU. 24초 녹음 → 약 1.7초 |
| 화면 | Vite 8 + TypeScript 7 (프레임워크 없음, 페이지별 진입점) | 화면이 4~5개로 작아 React 등이 필요 없음. 빌드 결과를 서버 `wwwroot`로 바로 출력 |
| 디자인 | 자체 CSS (`web/src/shared/base.css`), Pretendard 글꼴 | 모더니스트: 무채색 + 시그널 레드 한 색, 그림자·둥근 모서리 없음, 괘선과 여백으로 구분 |
| 테스트 | xUnit + `WebApplicationFactory` (실제 HTTP·SQLite로 통합 테스트) | 시계(`TimeProvider`)와 STT(`ISpeechToText`)를 바꿔 끼워 QR 만료·변환 경합까지 재현 |
| 공개 · HTTPS | Cloudflare Tunnel (`cloudflared`, 터널 이름 `goodmona`) | 폰 카메라·마이크는 HTTPS에서만 동작. 인증서·포트포워딩 불필요 |
| 배포 | PowerShell `deploy/install-all.ps1` (02 → 01 순서, 관리자 권한, 다시 실행해도 안전) | 빌드 → publish → 서비스 등록 → 터널 → 외부 확인을 한 번에 |
| 로그인 | 02가 JWT(RS256) 출입증 발급, 01은 JwtBearer로 공개키 확인 | 직원 DB는 02 한 곳에만. 01은 매 요청 02에 묻지 않음 |
| 소스 관리 | GitHub `iamdeveloperz/GoodMoNa-Report`, 과제별 폴더(`01/`, `02/` …) | |

## 3. 폴더 구조 (01 기준, 02도 같은 모양)

```
01/
  GoodMoNa.slnx · global.json · dotnet-tools.json(dotnet-ef)
  src/GoodMoNa.Server/
    Program.cs              DI 등록 · 미들웨어 · 라우트 · 화면 주소(/report /director /import /capture/*)
    Endpoints/              기능별 API (Patient, Visit, Capture, Photo, Note, Director, Import) + Problems(오류 형식)
    Data/                   Entities · AppDbContext · DemoSeeder · Migrations/
    Contracts/              DTO(record) · 입력 정규화(PatientInput)
    Hubs/VisitHub.cs        SignalR 그룹: visit:{id}, director
    Transcription/          Whisper 변환기 · 큐(Channel) · BackgroundService · WAV 검사
    Import/                 CSV 읽기 · 이전 기록 가져오기 계획/저장 · 사진 색인 · 백그라운드 작업
    Infrastructure/         AppOptions(설정) · DataPaths · ClinicClock(병원 시간대) · MediaStorage · CaptureTokens
  tests/GoodMoNa.Server.Tests/   통합 테스트 (TestApp = 임시 데이터 폴더 + 가짜 시계·STT)
  web/                      index · report · director · import · capture .html + src/<페이지>/main.ts, src/shared/*
  deploy/install.ps1 · uninstall.ps1
  docs/design-report.md · implementation-report.md · result-report.md · crm-integration.md
```

## 4. 데이터 모델

```
Patient (이름+생년월일 유일)
  └─ Visit (환자당 날짜별 1개, Draft → Reported, 경과 Improved/Unchanged/Worsened, 메모 500자)
       ├─ Photo* (다시 찍어도 모두 보관, 화면은 최신 1장. ContentHash = 가져오기 중복 방지)
       ├─ VoiceNote* (원장 소견 녹음, 방문 안 순번 Seq, Queued → Transcribing → Transcribed/Failed)
       │    └─ Transcript (녹음과 1:1, 문장별 시작·끝 ms. 녹음 삭제 시 DB cascade로 함께 삭제)
       └─ CaptureToken* (QR 일회용 권한, SHA-256 해시만 저장, 10분, 새 QR 발급 시 이전 것 무효)
```

- 모든 시각은 UTC로 저장(`DateTime` 변환기), "오늘"은 `App:TimeZone`(Asia/Seoul) 기준 `ClinicClock.Today`.
- 사진·녹음 파일은 DB가 아니라 데이터 폴더 `photos/yyyy/MM/`, `notes/…`에 두고 DB에는 상대 경로만. 경로는 `DataPaths.Resolve`가 데이터 폴더 밖을 거부.
- enum은 DB·JSON 모두 문자열.

## 5. 주요 흐름

1. **보고(간호사, `/report`)**: 환자 검색·등록 → 오늘 방문 열기(직전 사진 함께) → QR 발급 → 폰이 `/capture/{token}`에서 촬영(회전 보정·EXIF 제거·긴 변 2560px JPEG) → 서버 저장 후 SignalR `PhotoArrived` → 경과·메모로 보고 → `ReportSubmitted`가 원장 그룹으로.
2. **결과 확인(원장, `/director`)**: 날짜별 받은 보고(새 보고는 알림음) → 전후 사진(확대 모드 · 5:5 비교 모드) → 소견 녹음(브라우저에서 16kHz 모노 WAV로 바꿔 업로드, 202) → `TranscriptionQueue` → `TranscriptionWorker`가 Whisper 변환 → `NoteUpdated` 푸시. 서버 재시작 시 대기 중 녹음 자동 재처리, 시작 시 모델 예열.
3. **이전 기록 가져오기(`/import`, 서버 PC에서만)**: 사진을 가져오기 폴더에 올림/복사 → CSV 검사(엑셀 행 번호로 오류) → 백그라운드에서 트랜잭션 하나로 저장(실패 시 DB·복사 사진 모두 되돌림). 중복은 이름+생년월일 / 환자+날짜 / SHA-256으로 막음.
4. **인트라넷(02, `/intranet/*`)**: 홈(필독 공지·오늘 출퇴근·내 연차·오늘 휴가자·처리할 일) · 공지 · 연차(신청 → 승인, 근로기준법 §60 자동 계산, 팀 달력) · 출퇴근(병원 공인 IP 또는 30초마다 바뀌는 입구 QR) · 직원 주차(차량 등록 → 신청 → 주차면 배정, 번호 뒷자리 조회) · 주소록 · 설정. 설계는 `02/docs/intranet-design.md`.
5. **CS 안내 도우미(02, `/intranet/ask` · `/intranet/knowledge`)**: 자료 가져오기(규칙 추출: `#` 제목 → 문서, `Q:`/`A:` → FAQ) → 초안 → 관리자 승인 → 승인된 자료만 2-gram BM25 검색(서버 메모리 색인) → (켜져 있으면) Claude API 또는 서버 PC의 Ollama가 찾은 자료만 근거로 답하고 서버가 근거 번호를 검사. 못 찾은 질문·"부족함"은 관리자에게 모여 표현 추가·FAQ로 만들기(학습 고리). 설계는 `02/docs/cs-assistant-design.md`.
6. **CRM 연동**: 설계만. CRM 정보가 없어 구현 가능안(CRM 무관 공통 기반 / 유형별 어댑터)과 대안으로 나눔 (`01/docs/crm-integration.md`). 1번 과제의 설계·구현·결과 보고서는 `01/docs/`.

## 6. API 요약

| 기능 | 경로 |
| --- | --- |
| 환자 | `GET/POST /api/patients`, `POST /api/patients/{id}/visits/today`, `GET /api/patients/{id}/history` |
| 방문 · 보고 | `GET /api/visits/{id}`, `POST /api/visits/{id}/capture-tokens`, `PUT /api/visits/{id}/report` |
| 폰 촬영 | `GET /api/capture/{token}`, `POST /api/capture/{token}/photo` |
| 사진 | `GET /api/photos/{id}` (private 캐시) |
| 원장 | `GET /api/director/reports?date=` |
| 녹음 | `GET/POST /api/visits/{id}/notes`, `GET /api/notes/{id}/audio`(Range), `DELETE /api/notes/{id}`, `POST /api/notes/{id}/retry` |
| 가져오기 | `/api/import/template.csv · files · files/{경로} · preview · run · status` (외부 요청 403) |
| 실시간 | `/hubs/visits`: `JoinVisit`, `LeaveVisit`, `JoinDirector` → `PhotoArrived`, `VisitReported`, `ReportSubmitted`, `NoteUpdated`, `NoteDeleted` |
| 상태 | `GET /healthz` |

## 7. 설정 (`App` 섹션, `appsettings*.json`)

| 키 | 기본 | 설명 |
| --- | --- | --- |
| `DataRoot` | `%LOCALAPPDATA%\GoodMoNa` (배포: `C:\GoodMoNa\01\data`) | SQLite·사진·녹음. OneDrive 폴더는 피함 |
| `PublicBaseUrl` | 요청 주소 | QR에 넣을 외부 주소 |
| `CaptureTokenMinutes` | 10 | QR 유효 시간 |
| `MaxPhotoBytes` | 15MB | 폰 사진 한 장 |
| `SeedDemoData` | false (배포 설정은 true) | 빈 DB에 예시 환자 6명. 실데이터 전에는 끔 |
| `TimeZone` | Asia/Seoul | "오늘" 기준 |
| `MaxNoteMinutes` | 12 | 녹음 한 개 최대 길이 |
| `AllowRemoteImport` | false | 가져오기를 외부(Cloudflare 경유)에도 열지. Access를 건 뒤에만 |
| `Whisper:ModelPath · Language · UseGpu · Threads · Prompt` | `C:\GoodMoNa\models\ggml-large-v3-turbo.bin`, ko, true, 0, 두피 진료 용어 | |

## 8. 포트 · 경로 · 서비스

| 항목 | 01 | 02 | 03 (쇼츠 제작) |
| --- | --- | --- | --- |
| 개발 서버 | http://localhost:5011 | http://localhost:5012 | http://localhost:5013 |
| Vite 개발 화면 | http://localhost:5173 (→ 5011, 로그인 경로는 5012로 프록시) | http://localhost:5174 (→ 5012 프록시) | http://localhost:5175 (→ 5013, 로그인 경로는 5012) |
| 운영 서버 | localhost:5001, 서비스 `GoodMoNa01` | localhost:5002, 서비스 `GoodMoNa02` | localhost:5003 예정(서비스·터널 경로는 아직 없음) |
| 설치 폴더 | `C:\GoodMoNa\01\{app,data,cloudflared}` | `C:\GoodMoNa\02\{app,data}` (data\keys에 서명 개인키) | 개발: `%LOCALAPPDATA%\GoodMoNa03` (DB·영상) |
| 외부 주소 | https://goodmona.hsdevs.com | 같은 주소, 로그인·인트라넷 경로만 (터널 경로 규칙) | 아직 없음 (터널 본문 100MB 제한 → 내부망 업로드 검토) |
| Whisper 모델 | `C:\GoodMoNa\models` (공용) | 같은 파일을 쓸 수 있음 | 같은 파일 (단어 단위 시간으로 자막) |

## 9. 보안 · 운영 현황

- **로그인 (02가 발급, 01은 확인만)**: 직원 DB는 02 인트라넷에만 있습니다. 가입 신청 → 대표원장·관리자 승인 → 로그인. 02가 RS256 JWT 출입증(15분, sub·name·role)을 gm_access 쿠키로 주고, 01은 02의 `/.well-known/jwks.json` 공개키로 서명만 확인해 직무별로 막습니다. 갱신 토큰(14일)은 회전하고 재사용되면 그 직원의 세션을 모두 끊습니다. 퇴사·직무 변경은 늦어도 15분 안에 01·02 모두 반영. 자세한 표는 `02/README.md`.
- 01 권한: 보고 = 대표원장·원장·간호사, 결과 확인·녹음 = 대표원장·원장, 가져오기 = 대표원장·관리자. 폰 촬영 `/capture/*`는 QR 토큰이 권한.
- 같은 주소(goodmona.hsdevs.com)를 Cloudflare 터널이 경로로 나눕니다: `/login`·`/signup`·`/intranet*`·`/api/auth/*`·`/api/admin/*`·`/api/intranet/*`·`/.well-known/*` → 02(5002), 나머지 → 01(5001). 그래서 쿠키 하나로 양쪽에 로그인됩니다.
- 가져오기 API는 `Cf-Connecting-IP` 헤더(Cloudflare 경유 표시)가 있으면 기본 403.
- 폰 화면에는 가린 이름(김\*수)만 보냄. 사진 응답은 `Cache-Control: private`.
- 백업 없음: `C:\GoodMoNa\01\data`·`C:\GoodMoNa\02\data`를 주기적으로 다른 곳에 복사해야 함.
- 02 CS 안내 도우미: 질문은 저장 전에 전화번호·주민번호를 가림. AI 답변은 설정으로 Claude API(가린 질문 + 찾은 자료 4건이 Anthropic으로 전송) 또는 서버 PC의 localhost Ollama(외부 전송 없음). API 키는 `C:\GoodMoNa\02\data\appsettings.local.json`(SYSTEM·관리자만 읽기)에만.
- 02 인트라넷 권한: 공지 쓰기·휴가 승인·출퇴근 수정·주차 배정·설정·CS 자료 가져오기·승인 = 대표원장·관리자, 나머지(공지 읽기·연차 신청·출퇴근·주차 신청·주소록)는 로그인한 직원 모두. 업무 API는 모두 `/api/intranet/*`.
- 비밀값(`cert.pem`, 터널 자격 증명 JSON)은 `C:\GoodMoNa\…\cloudflared`와 `%USERPROFILE%\.cloudflared`에만 두고 **절대 커밋하지 않음**(리포 공개).

## 10. 알려진 함정

- Windows git은 대소문자를 무시하므로 `.gitignore`에 `data/`를 쓰면 소스 폴더 `Data/`가 빠집니다. 파일 패턴(`*.db` 등)으로만 제외.
- Windows PowerShell 5.1: 네이티브 명령에 따옴표가 든 인자를 넘기면 깨짐(`sc.exe config`가 1639) → 서비스 경로는 레지스트리 `ImagePath`로 직접 씀. cloudflared는 삭제 안 된 터널에도 `deleted_at: 0001-01-01…`을 돌려줌.
- 이 PC에는 Python이 없습니다(스토어 바로가기만). 스크립트는 node 또는 PowerShell.
- Cloudflare: 요청 본문 100MB, 응답 대기 100초 제한 → 큰 업로드는 나눠서, 오래 걸리는 작업은 202 + 상태 조회.
- 02의 새 업무 API를 `/api/intranet/*` 밖에 만들면 운영에서는 터널이 01로 보내 404가 납니다. 새 경로 접두어를 쓰려면 `01/deploy/install.ps1`의 `$intranetPaths`와 `01/web/vite.config.ts` 프록시에 함께 추가.
- `dotnet run`을 `ASPNETCORE_ENVIRONMENT` 없이(`--no-launch-profile`) 돌리면 운영 포트(5001/5002)에 붙습니다. 개발은 `dotnet run --project …`(launchSettings 사용)으로.
- Minimal API에서 multipart 폼의 문자열 필드는 `[FromForm]`을 붙여야 함(안 붙이면 쿼리스트링에서 찾음).
- 배포는 관리자 권한(UAC 승인)이 필요해 사람이 "예"를 눌러야 합니다.
- 02 배포 스크립트는 설치 폴더의 `appsettings.Production.json`을 매번 새로 씁니다. 운영에서 바꿔 둘 설정(CS 도우미 AI 켜기 등)은 데이터 폴더의 `appsettings.local.json`에.
- 테스트의 `WebApplicationFactory`에 `Services`라는 속성을 새로 만들면 기본 `Services`(IServiceProvider)를 가립니다. 가짜 서비스 주입은 `TestApp.TestServices`로.
