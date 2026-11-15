// 유튜브 채널 쇼츠 목록 수집 + 요약 (브라우저 개발자 도구 콘솔용)
// 사용법: 채널의 쇼츠 탭(https://www.youtube.com/@채널/shorts)을 열고 F12 → 콘솔에 이 파일 내용을 붙여 넣고 Enter.
// 결과: 콘솔에 요약 표가 나오고, 전체 목록(TSV: 순번·영상ID·제목·조회수)이 클립보드에 복사됩니다.
// 공개 화면에 보이는 값만 읽습니다. 조회수는 화면 표시값("1.2만")이라 반올림 오차가 있습니다.
(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  // 1) 끝까지 스크롤해서 전체 쇼츠를 불러옴 (더 늘지 않으면 멈춤)
  let prev = -1, still = 0;
  while (still < 3) {
    window.scrollTo(0, document.documentElement.scrollHeight);
    await sleep(1200);
    const n = document.querySelectorAll('ytm-shorts-lockup-view-model').length;
    still = n === prev ? still + 1 : 0;
    prev = n;
  }

  // 2) 목록 읽기 (최신순, 순번 0 = 가장 최근)
  const toNum = (s) => {
    s = (s || '').replace('조회수', '').replace('회', '').trim();
    const x = parseFloat(s.replace(/,/g, ''));
    if (Number.isNaN(x)) return 0;
    return s.endsWith('만') ? x * 1e4 : s.endsWith('천') ? x * 1e3 : x;
  };
  const rows = [...document.querySelectorAll('ytm-shorts-lockup-view-model')].map((el, i) => {
    const href = el.querySelector('a[href*="/shorts/"]')?.getAttribute('href') || '';
    const lines = el.innerText.split('\n').filter(Boolean);
    return { i, id: href.split('/').pop(), title: lines[0] || '', views: toNum(lines.find((l) => l.startsWith('조회수'))) };
  });

  // 3) 요약
  const median = (a) => { const s = [...a].sort((x, y) => x - y); return s.length ? s[Math.floor(s.length / 2)] : 0; };
  const total = rows.reduce((a, r) => a + r.views, 0);
  const top = [...rows].sort((a, b) => b.views - a.views);
  const top10pct = top.slice(0, Math.round(rows.length * 0.1)).reduce((a, r) => a + r.views, 0);

  // 분류는 보고서(03/docs/shorts-analysis.md §2 P1)와 같은 제목 키워드 기준
  const cats = {
    '모발이식': /모발이식|이식|수술|모낭|절개|비절개|헤어라인|생착|\d+모/,
    '샴푸·생활템': /샴푸|비누|다이소|올리브영|약국|꿀템|두피|감는|감기|감고|감아|린스|트리트먼트|드라이/,
    '탈모약': /미녹시딜|탈모약|피나|두타|프로페시아|아보다트|약을|복용|클라스코테론|신약/,
    '굵기·관리법': /굵어|굵게|굵기|가늘|얇아/,
    '여성': /여성|여자/,
    '시술·주사': /주사|보톡스|PRP|레이저|엑소좀|메조/,
    '흰머리·기타': /흰머리|새치|여드름|비듬|냄새|가려/,
  };
  const byCat = Object.entries(cats).map(([name, re]) => {
    const m = rows.filter((r) => re.test(r.title));
    const sum = m.reduce((a, r) => a + r.views, 0);
    return { 분류: name, 편수: m.length, 중앙값: median(m.map((r) => r.views)), 조회비중: (sum / total * 100).toFixed(1) + '%', 최근100편: m.filter((r) => r.i < 100).length };
  });
  const byBlock = [];
  for (let k = 0; k < rows.length; k += 50) {
    const v = rows.slice(k, k + 50).map((r) => r.views);
    byBlock.push({ 구간: `${k}~${k + v.length - 1}`, 중앙값: median(v), 최대: Math.max(...v) });
  }

  console.log(`쇼츠 ${rows.length}편 · 누적 조회 ${total.toLocaleString()} · 중앙값 ${median(rows.map((r) => r.views)).toLocaleString()} · 상위 10% 비중 ${(top10pct / total * 100).toFixed(1)}%`);
  console.table(byCat);
  console.table(byBlock);
  console.table(top.slice(0, 20).map((r) => ({ 순번: r.i, 조회: r.views, 제목: r.title })));

  // 4) 전체 목록을 클립보드로 (copy는 개발자 도구 콘솔 전용 함수)
  const tsv = ['순번\t영상ID\t제목\t조회수', ...rows.map((r) => `${r.i}\t${r.id}\t${r.title}\t${r.views}`)].join('\n');
  if (typeof copy === 'function') { copy(tsv); console.log('전체 목록(TSV)을 클립보드에 복사했습니다. 스프레드시트에 붙여 넣으세요.'); }
  else console.log(tsv);
})();
