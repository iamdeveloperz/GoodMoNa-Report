# GoodMoNa — 2번 과제: 사내 인트라넷

굿모나의원 직원이 매일 쓰는 인트라넷입니다. 첫 단계로 **직원 계정(가입 → 승인)과 로그인**을 만들었고,
이 로그인으로 1번 과제(보고·결과 확인)의 간호사·원장 권한도 함께 나눕니다.
과제 원문: 기본 인트라넷 형태 + 연차 관리 + 차 관리 + LLM 위키로 CS 응대. 전체 설계는 [../docs/architecture.md](../docs/architecture.md).

| 주소 | 화면 | 누가 |
| --- | --- | --- |
| `/login` | 로그인 | 누구나 |
| `/signup` | 가입 신청 (관리자가 없고 서버 PC에서 열면 첫 관리자 만들기) | 누구나 |
| `/intranet` | 인트라넷 홈: 직무별 바로가기 | 로그인한 직원 |
| `/intranet/admin` | 직원 관리: 가입 승인·반려, 직무 변경, 사용 중지 | 대표원장·관리자 |

## 요구사항과 진행

- [x] 로그인 · 회원가입 (**승인해야만 로그인 가능**) · 직무 권한
- [x] 1번 과제와 로그인 공유 (간호사·원장 구분)
- [ ] 공지사항 (필독·읽음 확인)
- [ ] 연차 (신청 → 승인, 근로기준법 기준 잔여 자동 계산, 팀 달력)
- [ ] 출퇴근 (병원 와이파이 공인 IP 또는 입구 QR)
- [ ] 품의·결재 (연차·품의·주차가 같은 승인 흐름을 씀)
- [ ] 주차·차량 (직원 차량 등록·주차 관련 — 정확한 범위 확인 필요)
- [ ] 사내 메신저 (1:1·부서방, SignalR)
- [ ] LLM 위키로 CS 응대 (원장 유튜브 대본 = Whisper 변환, 매뉴얼·온보딩, 출처 표시 답변)
- [ ] 홈 화면 설치(PWA)·웹 푸시 알림

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
| 직원 관리 (대표원장·관리자) | `GET /api/admin/staff?status=`, `POST /api/admin/staff/{id}/approve · reject · disable · enable`, `PUT /api/admin/staff/{id}/role` |
| 공개키 (01이 사용) | `GET /.well-known/openid-configuration`, `GET /.well-known/jwks.json` |

## 구성

```
src/GoodMoNa02.Server
  Program.cs              DI · 인증 · 요청 제한 · 화면 주소
  Auth/                   SigningKeys(RSA 키) · TokenService(출입증·쿠키) · AuthSetup(JWT 확인·정책) · PageGate(화면 보호)
  Endpoints/              AuthEndpoints · AdminEndpoints · Problems
  Data/                   Staff · StaffSession · AppDbContext · Migrations/
  Contracts/              DTO · StaffInput(입력 검사)
tests/GoodMoNa02.Server.Tests   통합 테스트 16개 (가입·승인·잠금·갱신 회전·도난 감지·화면 보호)
web/                      login · signup · intranet · intranet-admin (정적 파일은 /intranet-assets/ — 01의 /assets/와 겹치지 않게)
deploy/install.ps1        서비스 GoodMoNa02 (localhost:5002)
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

설치 후 처음 한 번, **서버 PC의 브라우저에서** http://localhost:5002/signup 으로 첫 관리자(대표원장)를 만듭니다. 그 뒤 직원은 https://goodmona.hsdevs.com/signup 에서 가입 신청 → 관리자가 `/intranet/admin`에서 승인.

| 항목 | 값 |
| --- | --- |
| 서비스 | `GoodMoNa02` (localhost:5002) |
| 설치 · 데이터 | `C:\GoodMoNa\02\app` · `C:\GoodMoNa\02\data` (SQLite + 서명 키) |
| 외부 경로 | Cloudflare 터널(01 설치 스크립트가 설정)이 `/login`, `/signup`, `/intranet*`, `/api/auth/*`, `/api/admin/*`, `/.well-known/*`를 02로 보냄 |
