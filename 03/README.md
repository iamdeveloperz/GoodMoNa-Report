# 03 — 쇼츠 문제 분석 · 해결방안 · 제작 자동화

병원 유튜브 채널 「이건민의 풍성한 이야기」의 쇼츠를 분석해 해결방안을 제시하고, 편집자 없이 쇼츠를 만드는 반자동 제작 도구를 만듭니다.

| 파일 | 내용 |
| --- | --- |
| [docs/shorts-analysis.md](docs/shorts-analysis.md) | 보고서 — 현황 수치, 문제 8가지(P1~P8), 원인, 해결방안 7가지(S1~S7), 실행 계획, 확인 필요 정보 |
| [docs/shorts-automation-design.md](docs/shorts-automation-design.md) | 쇼츠 제작 자동화 설계 — 방법 4가지 장단점, 권장안(반자동), 구현 설계·일정·위험 |
| `src/GoodMoNa03.Server` · `web/` · `tests/` | **자동화 1단계 구현** (아래) |
| [tools/collect-shorts.js](tools/collect-shorts.js) | 공개 데이터 재현용 브라우저 콘솔 스크립트 — 쇼츠 전체 목록(TSV)과 요약 |

## 결론 요약

- **분석**: 쇼츠는 조회는 잘 나오지만(634편, 누적 약 1,289만 회) 조회가 큰 주제는 샴푸·약국템이고, 병원 핵심인 모발이식 쇼츠는 가장 덜 봅니다(중앙값 3,700). 쇼츠에서 상담으로 가는 클릭 경로가 없고, 어떤 영상이 상담을 만들었는지 재는 장치도 없습니다. 목표 지표를 조회수에서 **유튜브발 상담 신청 수**로 바꾸는 것이 핵심입니다.
- **자동화**: 원장 단독 출연 + 큰 자막 형식이라 컷·자막·제목 초안·점검을 자동화할 수 있습니다. **초안은 기계가, 공개 승인은 원장이** 합니다(의료광고 책임).

## 자동화 1단계 — 촬영본 → 검토 → 승인 → 완성본

```
촬영본 올리기(직원) ─▶ ffprobe(회전 반영) ─▶ 무음 감지(ffmpeg silencedetect) ─▶ Whisper 받아쓰기(단어별 시간)
   ─▶ 무음 자르기 · 자막(12자×2줄) · 용어 교정 ─▶ 제목·훅 초안(AI 또는 첫 문장) · 영상 코드 · 설명란
   ─▶ 표현 점검(규칙 사전 + AI) · 반복 제목 경고 ─▶ 미리보기 540×960
   ─▶ 검토 화면: 자막·자른 곳·제목·훅 고치기, 점검 해소 ─▶ 원장 승인 ─▶ 완성본 1080×1920 ─▶ 받아서 Studio 업로드 ─▶ 게시 기록
```

| 화면 | 주소 | 권한 |
| --- | --- | --- |
| 올리기 · 초안 목록 · 기존 제목 가져오기 | `/shorts` | 대표원장 · 원장 · 직원 · 관리자 |
| 검토 · 승인 · 완성본 받기 · 게시 기록 | `/shorts/review?id=` | 같음 (승인·반려·승인 취소는 대표원장·원장만) |

로그인은 02 인트라넷 출입증을 그대로 씁니다(01과 같은 방식). 간호사는 쓸 수 없습니다.

### 승인 조건

- 상태가 검토 대기이고, 화면이 본 버전과 서버 버전이 같아야 함(그사이 누가 고쳤으면 409).
- 표현 점검(규칙·AI)을 모두 **고치거나 "문제없음 + 이유"로 해소**. 해소 기록(누가·언제·이유)이 남습니다. 반복 제목 경고는 참고용(승인을 막지 않음).
- "환자가 나오는 영상"으로 표시했다면 출연 동의서 확인 체크.

### API (`/api/shorts`)

| 경로 | 설명 | 권한 |
| --- | --- | --- |
| `POST /sources?name=파일명` | 촬영본 올리기 — 본문은 영상 바이트 그대로(`video/*` 또는 `application/octet-stream`), 202 | 편집 |
| `GET /drafts` · `GET /drafts/{id}` | 목록 · 상세(자막·자른 곳·점검·승인 조건) | 편집 |
| `PUT /drafts/{id}` | 고치기 `{version, title, hook, description, hashtags, hasPatient, consentConfirmed, cues[{id,text}], cuts[{index,removed}]}` — 자막·자르기·훅이 바뀌면 미리보기 다시 | 편집 |
| `POST /drafts/{id}/findings/{fid}/resolve` | 점검 해소 `{note}` | 편집 |
| `POST /drafts/{id}/retry` · `DELETE /drafts/{id}` | 실패 다시 시도 · 지우기(게시한 것·작업 중인 것은 불가) | 편집 |
| `POST /drafts/{id}/publish` | 게시 기록 `{youTubeUrl, relatedVideoLinked}` → 제목이 반복 경고 목록에 들어감 | 편집 |
| `GET /drafts/{id}/preview.mp4` · `final.mp4?download=true` | 미리보기 · 완성본(파일 이름 = 영상 코드) | 편집 |
| `POST /drafts/{id}/approve` `{version}` · `reject` `{reason}` · `reopen` | 승인 · 반려 · 다시 검토로 | 승인 |
| `GET /status` | ffmpeg·Whisper 모델·AI 상태, 승인 권한 | 편집 |
| `POST /titles/import` `{text}` | 기존 쇼츠 제목 가져오기(`collect-shorts.js` TSV 또는 한 줄에 하나) | 편집 |

### 설정 (`App` 섹션)

운영에서 바꿀 값은 데이터 폴더의 `appsettings.local.json`에 둡니다(02와 같은 방식, 리포·채팅에 키를 넣지 않음).

| 키 | 기본값 | 설명 |
| --- | --- | --- |
| `DataRoot` | `%LOCALAPPDATA%\GoodMoNa03` | DB(`shorts.db`)와 영상(`media\src-{id}\`). OneDrive 폴더 피하기 |
| `MaxUploadMegabytes` · `MaxSourceMinutes` | 2048 · 5 | 촬영본 최대 크기 · 길이 |
| `Ffmpeg:FfmpegPath · FfprobePath` | `ffmpeg` · `ffprobe` (PATH) | **서버 PC에 설치 필요** |
| `Ffmpeg:VideoEncoder` | `h264_nvenc` | GPU 인코딩, 실패하면 `libx264`로 자동 재시도 |
| `Whisper:ModelPath` | `C:\GoodMoNa\models\ggml-large-v3-turbo.bin` | 01과 같은 모델 파일 |
| `Edit:MinSilenceMs · KeepPadMs · SilenceNoiseDb` | 400 · 150 · -35 | 이보다 긴 무음을 자르고 앞뒤 여유를 남김. 잡음이 크면 -30 쪽으로 |
| `Edit:MaxLineChars · MaxLines · CueGapMs` | 12 · 2 · 600 | 자막 한 줄 글자 수 · 줄 수 · 새 장 기준 |
| `Render:FontName · FontsDir` | `Malgun Gothic` · (시스템) | 자막 글꼴. 다른 글꼴은 파일을 FontsDir에(상업 사용 가능한 것만) |
| `Render:EndCardText · EndCardSeconds` | "전체 설명은 아래 관련 영상에서" · 2.5 | 끝 화면 + "상담 시 영상 코드 S1116A" |
| `Copy:DescriptionTemplate` | 요약 + 영상 코드 안내 + 해시태그 | `{summary}` `{code}` `{landingUrl}`(UTM 자동) `{hashtags}` |
| `Copy:LandingUrl · DefaultHashtags` | (비움) · #탈모 #모발이식 #탈모치료 | |
| `Ai:Mode · Model · OllamaUrl · ApiKey` | `Off` | `Ollama`(서버 PC, 외부 전송 없음) 또는 `Claude`(**자막 글자만** Anthropic으로 전송, 영상·음성은 안 나감) |
| `Terms` | 미녹시딜·피나스테리드 등 | Whisper 오표기 → 바른 표기 |

### 배포 · 접근 (운영)

`deploy/install-all.ps1`(02 → 03 → 01 + 터널)이 03을 서비스 `GoodMoNa03`(localhost:5003)으로 설치하고, 터널이 `/shorts` · `/shorts-assets` · `/api/shorts/*`를 03으로 보냅니다. 03만 다시 올릴 때는 `03/deploy/install.ps1`(터널 규칙이 바뀌었으면 01도).

| 어디서 | 주소 | 비고 |
| --- | --- | --- |
| 어디서나 | https://goodmona.hsdevs.com/shorts | 02 인트라넷 계정으로 로그인. **촬영본 95MB까지**(Cloudflare 본문 100MB 제한) |
| 서버 PC | http://localhost:5003/shorts | 큰 촬영본(최대 2GB)은 여기서. 로그인은 localhost:5002로 이동했다 돌아옴 |

- 설치 폴더: `C:\GoodMoNa\03\{app,data,tools}` — data에 DB·촬영본·미리보기·완성본, tools에 ffmpeg·ffprobe(서비스 계정은 사용자 PATH를 못 봐서 복사).
- 처음 설치할 때 `data\appsettings.local.json`을 만들어 서버 PC Ollama(qwen3:4b-instruct)로 제목·훅 초안을 씁니다. 끄려면 `App:Ai:Mode`를 `Off`, Claude로 바꾸려면 `Claude` + `ApiKey`(이 파일에만). 바꾼 뒤 서비스 재시작.
- 권한: 편집 = 대표원장·원장·직원·관리자, 승인 = 대표원장·원장. 간호사는 쓸 수 없음.

### 실행 (개발)

```powershell
# 서버 PC에 ffmpeg가 없으면 먼저 (한 번)
winget install --id Gyan.FFmpeg -e

cd 03
cd web; npm install; npm run build; cd ..
dotnet run --project src/GoodMoNa03.Server      # http://localhost:5013 (02 개발 서버 5012가 로그인 담당)
cd web; npm run dev                             # http://localhost:5175 (로그인 경로는 5012로 프록시)
dotnet test                                     # 52개 (+ 실제 도구 시험 3개: 아래 "실제 도구 시험")
```

### 실제 도구 시험 (서버 PC)

평소 `dotnet test`에서는 건너뛰고, 환경 변수를 주면 실제 ffmpeg·Whisper로 돕니다.

```powershell
$env:GOODMONA03_REAL = "1"
$env:GOODMONA03_FFMPEG_DIR = "$env:LOCALAPPDATAMicrosoftWinGetLinks"   # PATH에 ffmpeg가 있으면 생략
$env:GOODMONA03_REAL_SOURCE = "C:…음성이 든 세로 영상.mp4"                # 받아쓰기까지 시험(선택)
$env:GOODMONA03_REAL_OUT = "C:…결과 폴더"                                 # 결과 영상·cues.txt·draft.txt를 남김(선택)
$env:GOODMONA03_REAL_AI_MODEL = "qwen3:4b-instruct"                         # 전체 흐름 시험에서 Ollama 초안까지(선택)
dotnet test --filter RealToolsTests
```

2026-11-16 서버 PC(RTX 2080, ffmpeg 9.0.2)에서 확인한 것 — 합성 화면 + Windows 한국어 음성 합성(가짜 대본, 실제 원장·환자 음성 아님):

| 항목 | 결과 |
| --- | --- |
| 받아쓰기 | 22.7초 음성 → 0.65초 (Whisper large-v3-turbo, Vulkan GPU), 문장 오류 없음 |
| 무음 컷 | 22.7초 → 14.9초 (8곳) |
| 렌더 | h264_nvenc(GPU)로 인코딩, 맑은 고딕 한글 자막·훅 띠·끝 화면(영상 코드) 정상, 자막과 소리 맞음 |
| 고친 것 | Whisper가 단어 경계를 쉼까지 늘려 잡음(첫 단어 0초부터, 문장 끝 단어가 뒤 쉼 1.6초까지) → ffmpeg 무음 구간으로 단어 경계를 당김(`WordAligner`). 통째로 무음 안에 놓인 단어는 무음 끝으로 옮김(안 그러면 컷에 잘려 자막에서 사라짐) |

**2026-11-16 실제 쇼츠 1편으로 서버 전체 흐름**: 참고 채널의 공개 쇼츠 1편(원장 단독, 54초)을 yt-dlp로 받아 시험하고, 시험 뒤 영상은 지웠습니다(리포에 넣지 않음). 흐름은 업로드 → 실제 ffmpeg·Whisper·Ollama → 점검 해소 → 원장 승인 → 완성본.

| 항목 | 결과 |
| --- | --- |
| 속도 | 분석+미리보기 15~30초(받아쓰기 2.3~3.1초, 첫 실행은 모델 적재 포함) · 승인 후 완성본 8~10초 |
| 받아쓰기 | 대체로 정확. 오류 4곳: 클라스코테론→"클라스코테로"(→ 용어 사전·프롬프트에 추가해 해결), 1465명→"1465년", 국내 출시→"국내 출신", 기다리되→"기다리돼" — 숫자·동음이의는 검토에서 고쳐야 함 |
| 표현 점검 | 처음 1건("노벨상감")만 잡음 → 놓친 "확실히 더 낫고"·"역사상 최대 규모"를 규칙에 추가하고, 자막 두 장에 걸친 표현("확실히 / 풍성해질")은 이어 붙여 한 번 더 검사 → 4건 |
| 로컬 AI(qwen3:4b) 초안 | 쓸 만한 제목도 있으나 사실과 다른 것("바르는 약이 1465명을 돌파")·이상한 해시태그가 섞임 → 사람 선택 필수. 품질이 중요하면 Claude 쪽 |
| 무음 컷 | 0곳 — 이미 편집·배경음이 깔린 쇼츠라 정상. 원본 촬영본에서는 위 합성 시험처럼 잘림 |
| 화면 | 이미 자막이 들어간 편집본이라 우리 자막이 겹쳐 보임(원본 촬영본에서는 생기지 않음). 끝 화면이 원래 자막을 가림 |

### 아직 안 된 것 (2단계 이후)

- 외부 주소로 큰 촬영본 올리기(지금은 95MB까지, 나눠 올리기 필요) · 인트라넷 홈에 쇼츠 바로가기.
- 한 번에 여러 편 몰아 찍기 자동 분할, 롱폼 → 쇼츠 구간 추천, 얼굴 따라 9:16 자르기(지금은 가로 영상이면 가운데를 자름), 템플릿 여러 개, 배경음.
- YouTube API 예약 업로드·성과 수집(지금은 완성본을 받아 Studio에서 올리고 주소를 기록).
- 실제 원장 촬영본(진료실 잡음·말 빠르기)으로 자막 품질·무음 기준(`Edit:*`) 맞추기.

## 데이터 재현 (분석 보고서)

1. 채널 쇼츠 탭을 브라우저로 엽니다.
2. F12 → 콘솔에 `tools/collect-shorts.js` 내용을 붙여 넣고 실행합니다.
3. 콘솔에 요약 표가 나오고, 전체 목록은 클립보드에 복사됩니다. 이 목록을 `/shorts` 화면의 "기존 쇼츠 제목 가져오기"에 붙여 넣으면 반복 제목 경고에 쓰입니다.
