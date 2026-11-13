# GoodMoNa PMS — 1번 과제: 보고 흐름

간호사가 환자를 찾아 오늘 방문을 열고, QR로 폰 카메라를 연결해 사진을 찍은 뒤, 경과(호전·유지·악화)와 메모를 원장에게 보고합니다.
폰에서 올린 사진은 SignalR로 PC 화면에 바로 뜹니다.

| 단계 | 화면 | 서버 |
| --- | --- | --- |
| 1 환자 검색·등록 | 보고 앱 `/` | `GET/POST /api/patients` |
| 2 방문 생성 (직전 사진 왼쪽) | 보고 앱 | `POST /api/patients/{id}/visits/today` |
| 3 QR 표시 (10분, 새 QR을 띄우면 이전 QR 무효) | 보고 앱 | `POST /api/visits/{id}/capture-tokens` |
| 4 폰 촬영 (회전 보정·EXIF 제거·축소 후 업로드) | 촬영 페이지 `/capture/{token}` | `GET /api/capture/{token}`, `POST /api/capture/{token}/photo` |
| 5 오늘 사진 실시간 표시 | 보고 앱 | SignalR `/hubs/visits` → `PhotoArrived` |
| 6 보고 제출 | 보고 앱 | `PUT /api/visits/{id}/report` → `ReportSubmitted` (원장 앱용) |

## 구성

```
src/GoodMoNa.Server        ASP.NET Core (.NET 10) Minimal API · SignalR · EF Core SQLite
tests/GoodMoNa.Server.Tests  통합 테스트 (WebApplicationFactory, 시계 주입으로 QR 만료까지 검증)
web/                         보고 앱·촬영 페이지 (Vite + TypeScript) → 빌드 결과는 서버 wwwroot로
```

- 데이터(SQLite + 사진)는 `%LOCALAPPDATA%\GoodMoNa`에 저장합니다. OneDrive 동기화 폴더에 DB를 두지 않기 위해서입니다. `App:DataRoot`로 바꿀 수 있습니다.
- "오늘"은 서버 시간대가 아니라 `App:TimeZone`(기본 Asia/Seoul) 기준입니다.
- QR 토큰은 256비트 난수이고 DB에는 SHA-256 해시만 저장합니다. 폰 화면에는 가린 이름(김*수)만 보냅니다.
- `App:SeedDemoData`가 켜져 있으면 빈 DB에 예시 환자 6명과 과거 방문 사진(SVG로 만든 가짜 두피 사진)을 넣습니다.

## 실행

```powershell
cd web; npm install; npm run build; cd ..
dotnet run --project src/GoodMoNa.Server      # http://localhost:5001
dotnet test                                   # 서버 테스트
```

화면을 고치면서 볼 때는 서버를 켠 채로 `cd web; npm run dev` → http://localhost:5173 (API·SignalR은 5001로 프록시).

## 이 PC를 서버로: 배포 (https://goodmona.hsdevs.com)

폰 카메라 페이지는 HTTPS에서만 동작하므로 Cloudflare Tunnel로 공개합니다. 서버는 `localhost:5001`에만 열려 있고, 공유기 포트포워딩 없이 PC가 Cloudflare로 나가는 연결만 씁니다.

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
| 데이터 (SQLite·사진) | `C:\GoodMoNa\01\data` |
| 터널 설정·로그 | `C:\GoodMoNa\01\cloudflared` |

서비스를 지울 때는 `.\deploy\uninstall.ps1` (데이터와 터널은 남김).

환자 정보가 오가므로, 실제 데이터를 넣기 전에 보고 앱 주소에 Cloudflare Access(이메일 OTP 등)를 거는 것을 권장합니다. 촬영 페이지 `/capture/*`와 `/api/capture/*`는 QR 토큰이 권한이므로 Access에서 제외합니다.
