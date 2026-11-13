# GoodMoNa Report

GoodMoNa 병원 경과 보고 시스템(PMS) 과제 모음입니다. 폰으로 찍고 보고하면, 원장 iPad에 바로 뜹니다.

| 폴더 | 과제 | 상태 |
| --- | --- | --- |
| [`01/`](01/) | ① 보고 흐름 + 원장 결과 확인: 환자 검색 → 방문 생성 → QR → 폰 촬영 → 실시간 표시 → 보고 → 원장 알림 → 녹음 → Whisper 자동 변환 → 환자 기록 재구성 · 사진 확대/전후 비교 모드 · 이전 기록 CSV 일괄 가져오기 | 완료 · https://goodmona.hsdevs.com |
| [`02/`](02/) | ② 사내 인트라넷: 직원 가입(승인 필수) · 로그인(JWT 출입증) · 직원 관리, 01의 간호사·원장 권한 공유 → 다음: 공지·연차·출퇴근·품의·주차·메신저·LLM 위키 | 진행 중 · https://goodmona.hsdevs.com/login |

기술 스택: ASP.NET Core (.NET 10) Minimal API · SignalR · EF Core + SQLite · Whisper.net (large-v3-turbo, Vulkan GPU) · Vite + TypeScript · Cloudflare Tunnel

설계 문서: [docs/architecture.md](docs/architecture.md) (서버·언어·프레임워크·데이터 모델·API·포트·배포·알려진 함정)
작업 안내(에이전트용): [CLAUDE.md](CLAUDE.md)
배포: `deploy/install-all.ps1` (02 → 01 + 터널)
