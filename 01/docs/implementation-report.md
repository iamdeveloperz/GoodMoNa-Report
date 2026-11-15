# 1번 과제 구현 보고서

> 문서 묶음: [설계 보고서](design-report.md) · **구현 보고서**(이 문서) · [결과 보고서](result-report.md) · [CRM 연동 방안](crm-integration.md)
> 작성 기준일: 2026-11-15 · 대상 코드: `01/` (커밋 `a1b2799` 기준)

설계 보고서의 결정을 코드로 어떻게 옮겼는지, 구현 중 어떤 문제를 만나 어떻게 풀었는지를 기록합니다. 파일 경로는 `01/` 기준입니다.

## 1. 개발 경과

| 시각 (KST) | 커밋 | 내용 |
| --- | --- | --- |
| 11-13 16:58 | `a122c30` | 보고 흐름: 환자 검색 → 방문 → QR 촬영 → 실시간 표시 → 보고. 통합 테스트 11개, Windows 서비스 + Cloudflare Tunnel 배포 스크립트 |
| 11-13 18:02 | `eb332ce` | 원장 결과 확인 화면, Whisper 녹음 변환(큐·워커·예열·복구), 디자인 전면 교체. 테스트 29개 |
| 11-13 19:00 | `c29f8ae` | README에 원장 화면·녹음 흐름·개발 포트 반영 |
| 11-13 20:06 | `67a33dd` | 재배포 시 터널이 꺼진 채 멈추던 문제 수정 |
| 11-13 20:56 | `d128cb7` | 재배포 시 터널 서비스 경로 변경 실패(1639) 수정 |
| 11-13 20:59 | `a1a9bb3` | 녹음 출처가 항상 '앱 녹음'으로 저장되던 문제 수정 |
| 11-13 22:17 | `822a657` | 사진 확대·전후 비교 모드, 이전 기록 CSV 일괄 가져오기 |
| 11-13 22:18 | `68ebc05` | CRM 연동 방안 문서 (1차) |
| 11-13 22:52 | `51cccf7` | 가져오기를 기본적으로 서버 PC(localhost)에서만 허용 |
| 11-14 01:07 | `a1b2799` | 02 인트라넷 로그인 연동: 01의 간호사·원장·관리자 권한 분리, 보고자 = 로그인한 직원. 테스트 54개 |
| 11-15 | (이 커밋) | 설계·구현·결과 보고서, CRM 연동 방안 개편(구현 가능안 / 대안 구분) |

## 2. 코드 구성과 규모

```
01/
  src/GoodMoNa.Server/            서버 (C#, 마이그레이션 제외 약 2,800줄)
    Program.cs                    DI 등록 · 미들웨어 순서 · 권한별 라우트 그룹 · 화면 주소
    Auth/                         StaffAuth(JWT 확인·직무 정책) · PageGate(화면 접근 → /login)
    Endpoints/                    Patient · Visit · Capture · Photo · Note · Director · Import + Problems
    Data/                         Entities · AppDbContext · DemoSeeder · Migrations(4개)
    Contracts/                    DTO(record) · PatientInput(이름 정규화·마스킹)
    Hubs/VisitHub.cs              SignalR 그룹 visit:{id}, director
    Transcription/                ISpeechToText · WhisperSpeechToText · TranscriptionQueue · TranscriptionWorker · WavInfo
    Import/                       CsvTable · LegacyImporter · PhotoIndex · ImportJobs
    Infrastructure/               AppOptions · DataPaths · ClinicClock · MediaStorage · CaptureTokens
  tests/GoodMoNa.Server.Tests/    통합 테스트 54개 (약 980줄)
  web/                            화면 (TypeScript·CSS 약 3,300줄)
    src/report · director · capture · import · landing     페이지별 진입점
    src/shared/                   api · live(SignalR) · session(출입증 갱신) · audio · image · viewer · format · base.css
  deploy/install.ps1 · uninstall.ps1
```

## 3. 기능별 구현

### 3.1 환자·방문 (`Endpoints/PatientEndpoints.cs`, `Contracts/PatientInput.cs`)

- 이름은 공백 정리 후 저장, 검색은 이름 일부 일치 + 동명이인은 생년월일순.
- 같은 이름+생년월일 등록 시 409와 함께 **기존 환자를 돌려줘** 화면이 바로 그 환자를 엽니다(중복 등록 대신 이어서 진행).
- `POST /visits/today`는 멱등: 오늘 방문이 있으면 그것을, 없으면 새로 만들고, 직전 방문의 최신 사진을 같이 돌려줍니다.
- "오늘"은 `ClinicClock.Today`(설정 시간대 기준) — 서버가 UTC로 돌아도 자정 경계가 병원 기준으로 맞습니다.

### 3.2 QR 촬영 (`Endpoints/VisitEndpoints.cs`, `Endpoints/CaptureEndpoints.cs`, `Infrastructure/CaptureTokens.cs`, `web/src/capture`)

```
IssueCaptureToken:  방문이 오늘·Draft인지 확인
                    → 같은 방문의 살아 있는 토큰 일괄 무효 (ExecuteUpdate, RevokedAt = now)
                    → 32바이트 난수 → Base64Url 43자 → DB엔 SHA-256 hex만
                    → { token, url: PublicBaseUrl/capture/{token}, expiresAt }
UploadPhoto:        토큰 형식 검사(43자, URL-safe) → 해시로 조회 → 상태(Ready/Expired/Reported)
                    → 크기(15MB)·JPEG 시그니처 검사 → photos/yyyy/MM/에 저장 → DB → SignalR PhotoArrived
```

- 폰 페이지는 `capture=environment` 파일 입력으로 후면 카메라를 바로 엽니다(브라우저 카메라 API보다 기기 호환성이 높음).
- 업로드 전 `prepareJpeg`(`web/src/shared/image.ts`): `createImageBitmap(..., {imageOrientation: "from-image"})`로 EXIF 회전을 픽셀에 반영 → 캔버스에 다시 그려 EXIF(위치 포함) 제거 → 긴 변 2560px, JPEG 0.88 → 보통 1~2MB.
- 남은 시간은 서버 시계 기준으로 셉니다(`syncClock`: 응답 `Date` 헤더로 폰 시계 오차 보정). 만료 시 화면이 "QR 다시 띄우기" 안내로 바뀌고, 업로드 중에는 화면을 바꾸지 않습니다.
- 오류는 상태 코드로 구분해 화면을 바꿉니다: 410 → 만료 화면, 409 → 이미 보고됨, 그 외 → 토스트 후 미리보기 유지(재전송 가능).

### 3.3 실시간 (`Hubs/VisitHub.cs`, `web/src/shared/live.ts`)

| 이벤트 | 보내는 곳 | 받는 그룹 |
| --- | --- | --- |
| `PhotoArrived(visitId, photo)` | 폰 업로드 | `visit:{id}` (그 방문을 보고 있는 PC) |
| `VisitReported(visitId)` | 보고 제출 | `visit:{id}` |
| `ReportSubmitted(event)` | 보고 제출 | `director` |
| `NoteUpdated(note)` / `NoteDeleted(visitId, noteId)` | 녹음 업로드·변환 워커·삭제 | `director`, `visit:{id}` |

- 원장 그룹 가입(`JoinDirector`)은 허브 안에서도 직무를 다시 확인합니다(허브 자체는 Clinical 권한이라 간호사도 연결 가능하기 때문).
- 재연결되면 화면의 준비 함수(`onReady`)를 다시 실행해 그룹에 재가입합니다. 완전히 끊기면 5초 뒤 다시 연결하고, 폰·iPad가 잠들었다 깨어나 화면이 다시 보일 때도 연결을 확인합니다.
- enum은 문자열로 직렬화(`JsonStringEnumConverter`) — API 응답과 허브 메시지 형식이 같습니다.

### 3.4 보고 (`VisitEndpoints.SubmitReport`)

검증 순서: 경과 값 → 메모 500자 → 방문 존재 → 이미 보고됨(409) → 오늘 방문인지(409) → 사진 1장 이상(409). 저장 후 남은 QR을 모두 무효로 하고, 방문 그룹에 `VisitReported`, 원장 그룹에 `ReportSubmitted`를 보냅니다. 보고자는 출입증의 `sub`(직원번호)·`name`으로 남깁니다(`StaffAuth.Who`).

### 3.5 원장 화면 (`web/src/director`, `web/src/shared/viewer.ts`, `Endpoints/DirectorEndpoints.cs`)

- 날짜별 받은 보고 목록 + 새 보고 알림음. iPad Safari는 사용자 조작 전 소리를 막으므로 첫 터치 때 `AudioContext`를 깨워 둡니다(`Chime.unlock`).
- 환자 기록(`GET /api/patients/{id}/history`)을 받아 방문·사진·소견을 시간순으로 재구성.
- **확대 모드**: 포인터 이벤트로 핀치·휠·더블탭 확대(최대 8배), 날짜를 바꿔도 확대 위치 유지(같은 부위 비교).
- **비교 모드**: 전·후 두 장을 5:5 전체 화면, 한쪽을 확대하면 양쪽이 같이 확대, 조작 막대는 3초 뒤 숨김(환자에게 보여 줄 때).

### 3.6 소견 녹음과 자동 변환 (`web/src/shared/audio.ts`, `Endpoints/NoteEndpoints.cs`, `Transcription/*`)

```
[브라우저]  MediaRecorder (크롬·갤럭시 webm/opus, iPad mp4/aac) 또는 파일 선택 / 기기 녹음 앱
            → decodeAudioData → OfflineAudioContext(1ch, 16kHz)로 리샘플·다운믹스 → 16bit PCM WAV
[업로드]    POST /api/visits/{id}/notes  (보고된 방문만, 12분 상한, WAV 헤더 검사 → 아니면 415)
            → audio/yyyy/MM/ 저장 → VoiceNote(Seq, Queued) → 대기열 → 202 Accepted + NoteUpdated
[워커]      TranscriptionWorker (BackgroundService, 한 번에 하나)
            → Transcribing 푸시 → Whisper 변환(문장별 시작·끝 ms) → 환각 문구 제거
            → Transcript 1:1 저장 → Transcribed 푸시   /  예외 → Failed(오류 500자) 푸시
```

구현에서 신경 쓴 점:

| 상황 | 처리 |
| --- | --- |
| 서버 시작이 모델 로딩(1.6GB)으로 늦어짐 | `Lazy<WhisperFactory>`로 지연 로딩 + 워커 시작 직후 1초 무음으로 예열(`WarmUpAsync`) → 요청은 바로 받고 첫 변환 13초 → 약 1초 |
| 모델 파일이 없음 | `LazyThreadSafetyMode.PublicationOnly`로 예외를 캐시하지 않음 → 파일을 넣으면 재시작 없이 "다시 시도"로 동작 |
| 서버가 변환 중 꺼짐 | 시작 시 `Queued`·`Transcribing` 녹음을 대기열에 다시 넣음(`RecoverPendingAsync`) |
| 변환 도중 원장이 녹음 삭제 | `TranscriptionQueue.Cancel`이 그 작업의 `CancellationTokenSource`를 취소 → 워커는 스크립트를 붙이지 않고 파일만 정리. 변환 후 저장 직전 삭제는 재조회·FK 위반(`DbUpdateException`)으로 감지 |
| 파일이 열려 있어 삭제 실패 | `MediaStorage.TryDelete`가 실패해도 워커 종료 시 다시 정리 |
| 같은 방문에 녹음 두 개가 동시에 올라옴 | (VisitId, Seq) 유일 인덱스 충돌 시 번호를 다시 매겨 최대 3회 저장 |
| Whisper 환각("시청해 주셔서 감사합니다" 등) | 무음 임계값 0.6 + 대표 문구 정규식으로 문장 단위 제거(`IsHallucination`) |
| 의료 용어 인식 | `Whisper:Prompt`에 두피 진료 용어를 넣어 어휘 유도 |
| iPad에서 녹음 재생·탐색 | `Results.File(..., enableRangeProcessing: true)` — Safari는 Range 응답이 있어야 `<audio>` 탐색 가능 |
| 녹음 삭제 시 스크립트 | DB cascade로 함께 삭제, 파일도 삭제, `NoteDeleted` 푸시. 같은 삭제를 두 번 보내도 204(멱등) |

### 3.7 이전 기록 가져오기 (`Import/*`, `Endpoints/ImportEndpoints.cs`, `web/src/import`)

- CSV 인코딩 자동 판별(UTF-8 BOM / UTF-16 / UTF-8 검증 실패 시 CP949), 따옴표 안 쉼표·줄바꿈 지원(`CsvTable`).
- 검사(`preview`)는 아무것도 저장하지 않고 **엑셀 행 번호**로 오류를 돌려줍니다.
- 실행은 202 + 백그라운드(`ImportJobs`), 트랜잭션 하나. 실패하면 DB를 롤백하고 이미 복사한 사진 파일도 지웁니다.
- 중복 방지: 환자 = 이름+생년월일, 방문 = 환자+날짜(이미 있으면 빈 칸만 채움), 사진 = SHA-256.
- 사진 업로드는 한 장씩(Cloudflare 요청 100MB 제한), 파일 시그니처로 JPG·PNG·WebP만, 가져오기 폴더 밖 경로 거부.
- 기본은 서버 PC에서만: Cloudflare를 거친 요청(`Cf-Connecting-IP` 헤더)은 엔드포인트 필터에서 403.

### 3.8 로그인·권한 (`Auth/StaffAuth.cs`, `Auth/PageGate.cs`, `web/src/shared/session.ts`)

- `JwtBearer`가 `gm_access` 쿠키에서 토큰을 읽고(`OnMessageReceived`), 02의 `/.well-known/jwks.json`에서 받은 공개키로 서명·Issuer·Audience·만료(±30초)를 확인합니다. 키는 캐시되고 02가 늦게 켜져도 30초마다 다시 시도.
- 정책 3개: Clinical(대표원장·원장·간호사), Director(대표원장·원장), ImportAdmin(대표원장·관리자). `Program.cs`에서 라우트 그룹 단위로 겁니다.
- 화면 경로(`/report`·`/director`·`/import`)는 `PageGate` 미들웨어가 로그인 안 됨 → `/login?next=…`, 직무 불일치 → "권한 없음" 화면.
- 401/403 응답도 ProblemDetails + 한국어 메시지. 화면의 API 호출이 401을 받으면 갱신 토큰으로 새 출입증을 받아 한 번 다시 시도하고(`api.ts`), 그래도 안 되면 `/login?next=…`로 보냅니다. 서버에 닿지 않는 경우는 로그아웃으로 취급하지 않습니다(`session.ts`).

### 3.9 배포 (`deploy/install.ps1`, 리포 루트 `deploy/install-all.ps1`)

```
관리자 권한으로 다시 실행(UAC) → 준비물 확인(dotnet·npm·cloudflared·Cloudflare 로그인 cert.pem) → Whisper 모델 없으면 내려받기
→ 화면 빌드 → dotnet publish → 서비스 GoodMoNa01 등록/갱신(자동 시작, 실패 시 5초 뒤 재시작)
→ 운영 설정(데이터 폴더·외부 주소) → 터널 확인/생성 → 경로 규칙(02/01) 설정 → 터널 서비스 재시작
→ https://goodmona.hsdevs.com/healthz 외부 확인
```

다시 실행해도 안전(멱등)하게 만들었고, 재배포 중에는 터널을 멈추지 않아 외부 접속이 끊긴 채 남지 않습니다(서버만 잠깐 502).

## 4. 구현 중 만난 문제와 해결

| # | 증상 | 원인 | 해결 | 커밋 |
| --- | --- | --- | --- | --- |
| 1 | 클론하면 엔티티·마이그레이션이 없음 | Windows git은 대소문자를 무시해 `.gitignore`의 `data/`가 소스 폴더 `Data/`까지 제외 | 폴더 규칙 대신 파일 패턴(`*.db` 등)으로만 제외 | `eb332ce` |
| 2 | 녹음 출처가 항상 '앱 녹음' | Minimal API에서 `string` 매개변수는 쿼리스트링으로 바인딩 → multipart 폼의 `source`를 못 읽음 | `[FromForm]` 추가 + 테스트 | `a1a9bb3` |
| 3 | 재배포 시 터널 서비스 경로 변경 실패(종료 코드 1639) | Windows PowerShell 5.1이 네이티브 명령 인자 속 따옴표를 깨뜨림(`sc.exe config binPath=`) | 레지스트리 `ImagePath`를 직접 수정 | `d128cb7` |
| 4 | 재배포 후 터널이 꺼진 채 멈춤 | cloudflared가 살아 있는 터널에도 `deleted_at: 0001-01-01…`을 줘서 삭제된 터널로 오인 → 같은 이름으로 생성 시도 실패 | zero time을 "삭제 안 됨"으로 처리, 재배포 중 터널은 멈추지 않고 마지막에 재시작 | `67a33dd` |
| 5 | 첫 녹음 변환이 13초 | 모델 로딩 + GPU 셰이더 컴파일 | 서버 시작 직후 1초 무음 예열 | `eb332ce` |
| 6 | 조용한 녹음에 엉뚱한 문장 | Whisper가 무음 구간에 학습 데이터 문구를 생성 | 무음 임계값 + 환각 문구 필터 | `eb332ce` |
| 7 | iPad에서 녹음 재생 위치 이동 불가 | Safari는 Range 미지원 응답을 탐색하지 못함 | Range 처리 켜기 + 테스트 | `eb332ce` |
| 8 | iPad에서 새 보고 알림음이 안 남 | 사용자 조작 전 오디오 재생 차단 | 첫 터치 때 `AudioContext` 깨우기, 준비 안 됐으면 화면에 안내 | `eb332ce` |
| 9 | 대량 사진 가져오기 실패 | Cloudflare 요청 본문 100MB·응답 대기 100초 제한 | 사진 한 장씩 업로드(동시 4개, 이미 있는 건 건너뜀) + 저장은 202 백그라운드 | `822a657` |
| 10 | 가져오기가 외부에 열려 있음 | 대량 쓰기 기능이 인터넷에 노출 | 기본 localhost 전용, `AllowRemoteImport`는 Cloudflare Access 적용 뒤에만 | `51cccf7` |
| 11 | 폰 사진이 PC에서 옆으로 누움 / 위치 정보 포함 | EXIF 회전·GPS 태그 | 브라우저에서 회전 반영 후 다시 그려 EXIF 제거 | `a122c30` |

## 5. 테스트

`tests/GoodMoNa.Server.Tests` — `TestApp`이 임시 데이터 폴더·SQLite로 실제 서버를 띄우고, `TimeProvider`(가짜 시계)와 `ISpeechToText`(가짜 변환기: `Gate`를 열 때까지 변환이 끝나지 않고, `Fail`로 실패를 흉내)를 바꿔 끼웁니다. 테스트용 출입증은 테스트 안에서 만든 RSA 키로 서명합니다.

| 파일 | 개수 | 검증하는 것 |
| --- | --- | --- |
| `ReportFlowTests` | 11 | 환자 검색·동명이인 정렬, 중복 등록 409, 잘못된 입력 400, 오늘 방문 멱등, **QR → 촬영 → 보고 전체 흐름**, QR 10분 만료, 새 QR 시 이전 QR 무효, JPEG 아닌 파일·가짜 토큰 거절 |
| `NoteFlowTests` | 18 | 녹음 → 자동 변환 → 스크립트 연결, Range 재생, 삭제 시 스크립트·파일 삭제, **변환 도중 삭제 시 작업 중단**, 실패 → 재시도, 16kHz 모노가 아니면 415, 보고 전 녹음 금지, 원장 목록·환자 기록 집계, 화면 경로 |
| `ImportTests` | 18 | 양식 UTF-8 BOM, 미리보기 무저장·엑셀 행 번호, CP949 CSV, **다시 돌려도 중복 없음**, 기존 방문은 빈칸만 채움, 오류 행 처리 선택, 필수 머리글, 폴더 밖 경로·비사진 거절, 같은 이름 사진 구분, Cloudflare 경유 403 |
| `AuthTests` | 7 | 미로그인 API 401·화면 로그인 이동, 간호사는 원장 기능 403, 보고자 = 로그인 직원, 비진료 직무 차단, 다른 키·만료 출입증 거절, 쿠키로 출입증 전달, 서버 PC 직접 접속 시 02 로그인으로 |

실행: `cd 01; dotnet test` → 2026-11-15 기준 **54개 모두 통과(약 7초)**.

화면 동작(카메라·마이크·터치 확대·알림음)은 자동 테스트 범위 밖입니다. 기기별 수동 확인 항목은 [결과 보고서](result-report.md#4-화면-수동-확인-체크리스트)에 정리했습니다.
