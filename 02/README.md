# GoodMoNa PMS — 2번 과제 (준비)

> **과제 요구사항이 아직 정리되지 않았습니다.** 요구사항을 받으면 이 문서의 "요구사항" 절부터 채우고 시작하세요.
> 1번 과제 범위(보고 흐름 + 원장 결과 확인 + 확대·비교 + CSV 가져오기 + CRM 방안)는 모두 `01/`에 있습니다.

01과 같은 스택(ASP.NET Core .NET 10 Minimal API · EF Core SQLite · Vite + TypeScript)으로 만든 빈 골격입니다.
서버가 뜨고, 병원 시간대 기준 "오늘"을 돌려주고, 첫 화면이 서버 연결을 확인하는 것까지만 되어 있습니다.
전체 설계 배경은 [../docs/architecture.md](../docs/architecture.md)를 보세요.

## 요구사항

- [ ] (미정)

## 구성

```
src/GoodMoNa02.Server          ASP.NET Core (.NET 10) Minimal API · EF Core SQLite (엔티티 없음)
  Program.cs                   DI · 미들웨어 · /healthz · /api/info
  Infrastructure/              AppOptions · DataPaths · ClinicClock  (01과 같은 규칙)
  Endpoints/Problems.cs        오류 응답 형식 (ProblemDetails + 한국어 detail)
  Data/AppDbContext.cs         DateTime UTC 저장 규칙만 있음
tests/GoodMoNa02.Server.Tests  통합 테스트 (WebApplicationFactory, 임시 데이터 폴더, 가짜 시계)
web/                           Vite + TypeScript, 디자인 토큰은 01의 base.css를 복사해 둠
```

## 실행

```powershell
dotnet tool restore
cd web; npm install; npm run build; cd ..
dotnet run --project src/GoodMoNa02.Server     # http://localhost:5012
dotnet test
```

화면 개발: 서버를 켠 채로 `cd web; npm run dev` → http://localhost:5174 (API는 5012로 프록시). 01(5011·5173)과 동시에 띄울 수 있습니다.

## 예약해 둔 값

| 항목 | 값 |
| --- | --- |
| 개발 서버 / 화면 개발 | 5012 / 5174 |
| 운영 포트 · 서비스 | localhost:5002 · `GoodMoNa02` |
| 데이터 폴더 | 기본 `%LOCALAPPDATA%\GoodMoNa02` (운영 예정 `C:\GoodMoNa\02\data`) |

## 시작할 때 할 일

1. 요구사항을 위 표에 적기. 1번 데이터(환자·방문·사진)를 써야 하면, 01 DB를 공유할지 01 API를 호출할지 먼저 정하기.
2. 엔티티를 `Data/`에 추가하고 `dotnet ef migrations add Initial --project src/GoodMoNa02.Server`.
3. 기능별로 `Endpoints/<기능>Endpoints.cs` + `tests/<기능>Tests.cs`를 한 쌍으로.
4. 페이지를 늘리면 `web/<페이지>.html` + `web/src/<페이지>/main.ts`, `vite.config.ts`의 input, `Program.cs`의 `MapFallbackToFile`.
5. 배포가 필요해지면 `01/deploy/install.ps1`을 복사해 서비스 이름·포트·폴더를 02 값으로 바꾸고, 같은 터널(`goodmona`)의 ingress에 경로나 호스트를 추가.
