/**
 * 폰에서 찍은 사진을 올리기 전에 처리합니다.
 * - EXIF 회전을 픽셀에 반영 (서버·PC에서 옆으로 누워 보이는 문제 방지)
 * - 다시 그려서 EXIF(위치 정보 포함)를 제거
 * - 긴 변 2560px로 축소, JPEG 0.88로 재인코딩 → 보통 1~2MB
 */
export async function prepareJpeg(file: Blob, maxEdge = 2560, quality = 0.88): Promise<Blob> {
  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file, { imageOrientation: "from-image" });
  } catch {
    bitmap = await createImageBitmap(file);
  }
  const scale = Math.min(1, maxEdge / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(bitmap.width * scale);
  canvas.height = Math.round(bitmap.height * scale);
  const ctx = canvas.getContext("2d");
  if (!ctx) throw new Error("이 브라우저는 사진 변환을 지원하지 않습니다.");
  ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  return await new Promise<Blob>((resolve, reject) =>
    canvas.toBlob((b) => (b ? resolve(b) : reject(new Error("사진 변환에 실패했습니다."))), "image/jpeg", quality),
  );
}

/** 결정적 난수 (시연용 사진을 매번 같게 만들기 위해) */
function rng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** PC에서 시연할 때 쓰는 두피 근접 사진 흉내 이미지 (실제 사진 아님). */
export async function sampleScalpJpeg(seed: number, density = 0.7): Promise<Blob> {
  const c = document.createElement("canvas");
  c.width = 480;
  c.height = 640;
  const g = c.getContext("2d")!;
  const r = rng(seed);
  const skin = g.createRadialGradient(240, 300, 30, 240, 320, 430);
  skin.addColorStop(0, "#ebc6a8");
  skin.addColorStop(1, "#b98a6c");
  g.fillStyle = skin;
  g.fillRect(0, 0, 480, 640);
  const cx = 240 + (r() - 0.5) * 40;
  const cy = 300 + (r() - 0.5) * 40;
  for (let i = 0; i < 6200; i++) {
    const x = r() * 520 - 20;
    const y = r() * 680 - 20;
    const dx = x - cx;
    const dy = y - cy;
    const d = Math.min(1.2, Math.hypot(dx, dy) / 300);
    if (r() > Math.min(0.95, density * 0.45 + d * d * density * 1.1)) continue;
    const ang = Math.atan2(dy, dx) + Math.PI * 0.42 + (r() - 0.5) * 0.7;
    const len = 14 + r() * 20;
    g.strokeStyle = `rgba(${30 + ((r() * 22) | 0)},${22 + ((r() * 12) | 0)},${18 + ((r() * 8) | 0)},${0.78 + r() * 0.2})`;
    g.lineWidth = 1.3 + r() * 1.3;
    g.beginPath();
    g.moveTo(x, y);
    g.quadraticCurveTo(
      x + Math.cos(ang) * len * 0.5 + (r() - 0.5) * 5,
      y + Math.sin(ang) * len * 0.5 + (r() - 0.5) * 5,
      x + Math.cos(ang) * len,
      y + Math.sin(ang) * len,
    );
    g.stroke();
  }
  const v = g.createRadialGradient(240, 320, 180, 240, 320, 470);
  v.addColorStop(0, "rgba(0,0,0,0)");
  v.addColorStop(1, "rgba(0,0,0,.38)");
  g.fillStyle = v;
  g.fillRect(0, 0, 480, 640);
  return await new Promise<Blob>((res, rej) => c.toBlob((b) => (b ? res(b) : rej(new Error("샘플 생성 실패"))), "image/jpeg", 0.85));
}
