# GoodMoNa Report

GoodMoNa 병원 경과 보고 시스템(PMS) 과제 모음입니다. 폰으로 찍고 보고하면, 원장 iPad에 바로 뜹니다.

| 폴더 | 과제 | 상태 |
| --- | --- | --- |
| [`01/`](01/) | ① 보고 흐름 + 원장 결과 확인: 환자 검색 → 방문 생성 → QR → 폰 촬영 → 실시간 표시 → 보고 → 원장 알림 → 녹음 → Whisper 자동 변환 → 환자 기록 재구성 · 사진 확대/전후 비교 모드 · 이전 기록 CSV 일괄 가져오기 | 완료 · https://goodmona.hsdevs.com |
| [`02/`](02/) | ② 사내 인트라넷: 직원 가입(승인 필수) · 로그인(JWT 출입증) · 직원 관리 · 공지(필독·읽음 확인) · 연차(근로기준법 자동 계산·승인·팀 달력) · 출퇴근(병원 네트워크/입구 QR) · 직원 주차 · 주소록 · CS 안내 도우미(자료 추출 → 승인 → 검색, 못 찾은 질문 → FAQ, 로컬 AI 답변 연결부), 01의 간호사·원장 권한 공유 → 다음: 품의·메신저·로컬 AI 평가 | 진행 중 · https://goodmona.hsdevs.com/login |
| [`03/`](03/) | ③ 쇼츠 문제 분석 및 해결방안: 쇼츠 634편 조회·주제 분석 → 문제 8가지(조회-사업 목표 불일치, 전환 경로 단절, 측정 부재 등) → 해결방안(측정 장치 · 쇼츠→롱폼→상담 연결 · 콘텐츠 비중 재배분 · 주간 리뷰). 편집자 없는 제작 자동화 설계(반자동 파이프라인: Whisper·ffmpeg·로컬 AI → 검토·승인 → 업로드). 자동화 1단계 구현: 촬영본 → 무음 컷·자막·제목 초안·표현 점검 → 검토 → 원장 승인 → 완성본(`/shorts`). 보고서 [분석](03/docs/shorts-analysis.md) · [자동화 설계](03/docs/shorts-automation-design.md) | 분석 완료 · 자동화 1단계 운영 · https://goodmona.hsdevs.com/shorts |

기술 스택: ASP.NET Core (.NET 10) Minimal API · SignalR · EF Core + SQLite · Whisper.net (large-v3-turbo, Vulkan GPU) · Vite + TypeScript · Cloudflare Tunnel

설계 문서: [docs/architecture.md](docs/architecture.md) (서버·언어·프레임워크·데이터 모델·API·포트·배포·알려진 함정)
1번 과제 보고서: [설계](01/docs/design-report.md) · [구현](01/docs/implementation-report.md) · [결과](01/docs/result-report.md) · [CRM 연동 방안(구현 가능안·대안)](01/docs/crm-integration.md)
작업 안내(에이전트용): [CLAUDE.md](CLAUDE.md)
배포: `deploy/install-all.ps1` (02 → 01 + 터널)
