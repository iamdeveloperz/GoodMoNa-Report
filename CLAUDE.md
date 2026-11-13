# GoodMoNa Report — 작업 안내 (에이전트용)

GoodMoNa 병원 경과 보고 시스템(PMS) 과제 리포입니다. 시스템 구성은 [docs/architecture.md](docs/architecture.md)를 먼저 읽으세요.

## 리포 구성

| 폴더 | 내용 | 상태 |
| --- | --- | --- |
| `01/` | 보고 흐름 + 원장 결과 확인 + 사진 확대·비교 + 이전 기록 CSV 가져오기 + CRM 연동 방안 | 완료, https://goodmona.hsdevs.com 운영 중 |
| `02/` | 2번 과제 사내 인트라넷 — 직원 가입(승인 필수)·로그인·직원 관리 완료. 01의 간호사·원장 권한도 이 로그인으로 나눔. 공지·연차·출퇴근·품의·주차·메신저·LLM 위키는 예정 | 진행 중 |
| `deploy/install-all.ps1` | 02 → 01(+터널) 순서로 한 번에 배포 | |
| `docs/` | 리포 전체 설계 문서 | |

각 과제 폴더는 독립된 .NET 솔루션 + Vite 웹입니다. 과제끼리 코드를 공유하지 않습니다(필요해지면 그때 공통 라이브러리로 뺌).

## 자주 쓰는 명령 (과제 폴더에서)

```powershell
cd web; npm install; npm run build; cd ..     # 화면 빌드 → 서버 wwwroot
dotnet test                                   # 통합 테스트 (01: 54개, 02: 16개)
dotnet run --project src/<서버 프로젝트>        # 개발 서버 (01: 5011, 02: 5012)
cd web; npm run dev                           # 화면 개발 (01: 5173, 02: 5174)
dotnet ef migrations add <이름> --project src/<서버 프로젝트>   # dotnet tool restore 먼저
```

## 지켜야 할 것

- **사용자와는 한국어로** 이야기합니다. 화면 문구·오류 메시지·주석도 한국어.
- **비밀값을 커밋하지 않습니다**: `cert.pem`, 터널 자격 증명 `*.json`(cloudflared), API 키. 리포는 공개입니다.
- **배포(`deploy/install-all.ps1` = 02 → 01)는 관리자 권한이 필요**합니다. 실행하면 UAC 창이 뜨므로 사용자에게 "예"를 눌러 달라고 알립니다. 운영 중인 서비스를 멈추므로 사용자가 요청할 때만.
- 사용자가 직접 셸 명령을 돌릴 때는 `! <명령>` 형식(bash 문법)으로 안내합니다.
- 커밋 메시지는 `01: …` / `02: …`처럼 과제 번호로 시작하고, 끝에 Co-Authored-By와 Claude-Session 줄을 붙입니다.
- 기능을 바꾸면 테스트를 함께 고치고, 해당 과제 README의 표(주소·API·설정)를 갱신합니다.
- 디자인은 `web/src/shared/base.css`의 토큰만 씁니다(무채색 + `--accent` 한 색, 그림자·둥근 모서리·그라데이션 없음).
- 실제 환자 데이터는 리포·테스트에 넣지 않습니다. 예시 사진은 SVG/캔버스로 만든 가짜.
- 직무 이름(`Owner`·`Doctor`·`Nurse`·`Staff`·`Admin`)은 02 `Data/Entities.cs`의 StaffRole과 01 `Auth/StaffAuth.cs`의 StaffRoles가 같아야 합니다. 출입증 Issuer·Audience도 두 서버 설정이 같아야 합니다.
- 02의 서명 개인키(`keys/signing-key.pem`)는 데이터 폴더에만 둡니다. 커밋 금지.
- 개발: 02(5012)·01(5011) 서버를 켜고 `01/web`에서 `npm run dev`(5173) — 로그인 경로가 02로 프록시돼 운영과 같은 한 주소로 동작.

## 환경 메모

- Windows 11, PowerShell 5.1 + Git Bash. Python 없음(파일 편집 스크립트는 node).
- .NET SDK 10.0.400, Node 22+, cloudflared 설치됨, Whisper 모델은 `C:\GoodMoNa\models`.
- 리포가 OneDrive 폴더 안에 있으므로 DB·데이터는 리포 밖(`%LOCALAPPDATA%\GoodMoNa` 또는 `C:\GoodMoNa\…`)에 둡니다.
