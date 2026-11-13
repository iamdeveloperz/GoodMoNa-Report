# GoodMoNa PMS — 1번 과제: 보고 흐름 + 원장 결과 확인

간호사가 환자를 찾아 오늘 방문을 열고, QR로 폰 카메라를 연결해 사진을 찍은 뒤, 경과(호전·유지·악화)와 메모를 원장에게 보고합니다.
폰에서 올린 사진은 SignalR로 PC 화면에 바로 뜹니다. 원장은 보고를 실시간으로 받아 사진을 비교하고, 소견을 녹음하면 Whisper가 자동으로 스크립트를 만들어 그 녹음에 연결합니다.

| 주소 | 화면 |
| --- | --- |
| `/` | 역할 선택 |
| `/report` | 간호사 보고 (PC) |
| `/director` | 원장 결과 확인 (iPad·PC) |
| `/capture/{token}` | 폰 촬영 (QR로 열림) |

### 간호사 보고

| 단계 | 화면 | 서버 |
| --- | --- | --- |
| 1 환자 검색·등록 | 보고 앱 `/report` | `GET/POST /api/patients` |
| 2 방문 생성 (직전 사진 왼쪽) | 보고 앱 | `POST /api/patients/{id}/visits/today` |
| 3 QR 표시 (10분, 새 QR을 띄우면 이전 QR 무효) | 보고 앱 | `POST /api/visits/{id}/capture-tokens` |
| 4 폰 촬영 (회전 보정·EXIF 제거·축소 후 업로드) | 촬영 페이지 `/capture/{token}` | `GET /api/capture/{token}`, `POST /api/capture/{token}/photo` |
| 5 오늘 사진 실시간 표시 | 보고 앱 | SignalR `/hubs/visits` → `PhotoArrived` |
| 6 보고 제출 | 보고 앱 | `PUT /api/visits/{id}/report` → `ReportSubmitted` (원장 화면으로) |

### 원장 결과 확인

| 단계 | 서버 |
| --- | --- |
| 날짜별 받은 보고 (새 보고는 알림음 + NEW) | `GET /api/director/reports?date=`, SignalR `ReportSubmitted` |
| 전후 사진·간호사 메모·환자 지난 기록 | `GET /api/patients/{id}/history` |
| 소견 녹음 업로드 (앱 녹음 / 기기 녹음 앱 / 파일 선택) | `POST /api/visits/{id}/notes` → 202 |
| 자동 변환 → 스크립트를 녹음에 1:1 연결 | 백그라운드 큐 → Whisper → SignalR `NoteUpdated` |
| 녹음 재생 (구간 이동) | `GET /api/notes/{id}/audio` (Range 지원) |
| 녹음 삭제 → 스크립트·파일 함께 삭제 | `DELETE /api/notes/{id}` → `NoteDeleted` |
| 변환 실패 시 다시 시도 | `POST /api/notes/{id}/retry` |

녹음은 브라우저에서 16kHz 모노 WAV로 바꿔 올립니다(서버에 ffmpeg 불필요). 한 방문에 녹음을 여러 개 둘 수 있고, 녹음을 지우면 DB cascade로 스크립트도 지워집니다. 변환 중에 지우면 작업을 취소합니다. 서버가 재시작되면 대기·변환 중이던 녹음을 다시 처리합니다.

Whisper 모델 `ggml-large-v3-turbo.bin`(약 1.6GB)은 `App:Whisper:ModelPath`(기본 `C:\GoodMoNa\models\`)에 둡니다. 없으면 `install.ps1`이 내려받습니다. GPU(Vulkan)를 먼저 쓰고, 안 되면 CPU로 바꿉니다.

## 구성

```
src/GoodMoNa.Server          ASP.NET Core (.NET 10) Minimal API · SignalR · EF Core SQLite · Whisper.net
tests/GoodMoNa.Server.Tests  통합 테스트 (WebApplicationFactory, 시계·STT 주입으로 QR 만료와 녹음 변환·삭제 경합까지 검증)
web/                         역할 선택·보고·원장·촬영 페이지 (Vite + TypeScript) → 빌드 결과는 서버 wwwroot로
```

- 데이터(SQLite + 사진 + 녹음)는 `%LOCALAPPDATA%\GoodMoNa`에 저장합니다. OneDrive 동기화 폴더에 DB를 두지 않기 위해서입니다. `App:DataRoot`로 바꿀 수 있습니다.
- "오늘"은 서버 시간대가 아니라 `App:TimeZone`(기본 Asia/Seoul) 기준입니다.
- QR 토큰은 256비트 난수이고 DB에는 SHA-256 해시만 저장합니다. 폰 화면에는 가린 이름(김*수)만 보냅니다.
- `App:SeedDemoData`가 켜져 있으면 빈 DB에 예시 환자 6명과 과거 방문 사진(SVG로 만든 가짜 두피 사진)을 넣습니다.

## 실행

```powershell
cd web; npm install; npm run build; cd ..
dotnet run --project src/GoodMoNa.Server      # http://localhost:5011 (개발용, 배포 서비스는 5001)
dotnet test                                   # 서버 테스트
```

화면을 고치면서 볼 때는 서버를 켠 채로 `cd web; npm run dev` → http://localhost:5173/report, /director (API·SignalR은 5011로 프록시).

## 이 PC를 서버로: 배포 (https://goodmona.hsdevs.com)

폰 카메라와 마이크는 HTTPS에서만 동작하므로 Cloudflare Tunnel로 공개합니다. 서버는 `localhost:5001`에만 열려 있고, 공유기 포트포워딩 없이 PC가 Cloudflare로 나가는 연결만 씁니다.

```
폰/PC 브라우저 ──https──▶ Cloudflare ◀──터널(나가는 연결)── cloudflared 서비스 ──http──▶ localhost:5001 (GoodMoNa01 서비스)
```

```powershell
cloudflared tunnel login            # 처음 한 번: 브라우저에서 hsdevs.com 승인
.\deploy\install.ps1                # 관리자 권한으로 다시 뜸 (UAC 창에서 '예')
```

`install.ps1`은 다시 실행해도 됩니다. 코드를 고친 뒤 재배포할 때도 같은 명령입니다.

| 항목 | 값 |
| --- | --- |
| 서버 서비스 | `GoodMoNa01` (자동 시작, 죽으면 5초 뒤 재시작) |
| 터널 서비스 | `GoodMoNa01Tunnel` (cloudflared, 터널 이름 `goodmona`) |
| 프로그램 | `C:\GoodMoNa\01\app` |
| 데이터 (SQLite·사진·녹음) | `C:\GoodMoNa\01\data` |
| Whisper 모델 | `C:\GoodMoNa\models` |
| 터널 설정·로그 | `C:\GoodMoNa\01\cloudflared` |

서비스를 지울 때는 `.\deploy\uninstall.ps1` (데이터와 터널은 남김).

환자 정보가 오가므로, 실제 데이터를 넣기 전에 `/report`·`/director`와 API에 Cloudflare Access(이메일 OTP 등)를 거는 것을 권장합니다. 촬영 페이지 `/capture/*`와 `/api/capture/*`는 QR 토큰이 권한이므로 Access에서 제외합니다.
