/**
 * 원장 소견 녹음.
 * - 화면 녹음기: MediaRecorder(갤럭시·크롬은 webm/opus, 아이패드 사파리는 mp4/aac)
 * - 어떤 형식이든 올리기 전에 브라우저에서 16kHz 모노 16bit WAV로 바꿉니다.
 *   → 서버에 ffmpeg가 필요 없고, Whisper가 그대로 읽을 수 있으며, 어느 기기에서나 재생됩니다.
 */

export const MAX_RECORD_MS = 10 * 60 * 1000;

export interface Recorder {
  readonly startedAt: number;
  /** 0~1 사이 현재 소리 크기 (레벨 미터용) */
  level(): number;
  stop(): Promise<Blob>;
  cancel(): void;
}

type AudioContextCtor = typeof AudioContext;
const AC: AudioContextCtor = window.AudioContext ?? (window as unknown as { webkitAudioContext: AudioContextCtor }).webkitAudioContext;

export const canRecordInApp = (): boolean =>
  typeof window.MediaRecorder !== "undefined" && !!navigator.mediaDevices?.getUserMedia;

function pickMime(): string | undefined {
  const candidates = ["audio/webm;codecs=opus", "audio/mp4", "audio/webm", "audio/ogg;codecs=opus"];
  return candidates.find((m) => MediaRecorder.isTypeSupported?.(m));
}

export async function startRecorder(): Promise<Recorder> {
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
  });
  const mime = pickMime();
  const rec = new MediaRecorder(stream, mime ? { mimeType: mime, audioBitsPerSecond: 64000 } : undefined);
  const chunks: Blob[] = [];
  rec.ondataavailable = (e) => {
    if (e.data.size > 0) chunks.push(e.data);
  };

  // 레벨 미터
  const ctx = new AC();
  const analyser = ctx.createAnalyser();
  analyser.fftSize = 1024;
  ctx.createMediaStreamSource(stream).connect(analyser);
  const buf = new Uint8Array(analyser.fftSize);

  const release = () => {
    stream.getTracks().forEach((t) => t.stop());
    void ctx.close();
  };

  rec.start(1000); // 1초마다 조각을 받아 두면, 중간에 앱이 멈춰도 그때까지는 남습니다.
  const startedAt = performance.now();

  return {
    startedAt,
    level() {
      analyser.getByteTimeDomainData(buf);
      let sum = 0;
      for (const v of buf) sum += ((v - 128) / 128) ** 2;
      return Math.min(1, Math.sqrt(sum / buf.length) * 4);
    },
    stop() {
      return new Promise<Blob>((resolve) => {
        rec.onstop = () => {
          release();
          resolve(new Blob(chunks, { type: rec.mimeType || mime || "audio/webm" }));
        };
        if (rec.state !== "inactive") rec.stop();
        else rec.onstop(new Event("stop"));
      });
    },
    cancel() {
      rec.onstop = null;
      if (rec.state !== "inactive") rec.stop();
      release();
    },
  };
}

/** 어떤 오디오 파일이든 16kHz 모노 WAV로 바꿉니다. */
export async function toWav16k(file: Blob): Promise<{ wav: Blob; durationMs: number }> {
  const ctx = new AC();
  let decoded: AudioBuffer;
  try {
    decoded = await ctx.decodeAudioData(await file.arrayBuffer());
  } catch {
    throw new Error("이 녹음 파일 형식은 읽을 수 없습니다. 화면 녹음기로 다시 녹음해 주세요.");
  } finally {
    void ctx.close();
  }

  const rate = 16000;
  const frames = Math.max(1, Math.ceil(decoded.duration * rate));
  // 스테레오는 OfflineAudioContext가 모노 출력으로 자동 다운믹스합니다.
  const offline = new OfflineAudioContext(1, frames, rate);
  const src = offline.createBufferSource();
  src.buffer = decoded;
  src.connect(offline.destination);
  src.start();
  const pcm = (await offline.startRendering()).getChannelData(0);

  const bytes = new ArrayBuffer(44 + pcm.length * 2);
  const view = new DataView(bytes);
  const ascii = (o: number, s: string) => [...s].forEach((c, i) => view.setUint8(o + i, c.charCodeAt(0)));
  ascii(0, "RIFF");
  view.setUint32(4, 36 + pcm.length * 2, true);
  ascii(8, "WAVE");
  ascii(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true); // PCM
  view.setUint16(22, 1, true); // mono
  view.setUint32(24, rate, true);
  view.setUint32(28, rate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  ascii(36, "data");
  view.setUint32(40, pcm.length * 2, true);
  for (let i = 0, o = 44; i < pcm.length; i++, o += 2) {
    const s = Math.max(-1, Math.min(1, pcm[i]!));
    view.setInt16(o, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return { wav: new Blob([bytes], { type: "audio/wav" }), durationMs: Math.round(decoded.duration * 1000) };
}

/** 원장 앱의 새 보고 알림음. 아이패드는 사용자가 한 번 화면을 눌러야 소리를 낼 수 있어 unlock()을 먼저 부릅니다. */
export class Chime {
  private ctx: AudioContext | null = null;

  unlock(): void {
    this.ctx ??= new AC();
    void this.ctx.resume();
  }

  get ready(): boolean {
    return this.ctx?.state === "running";
  }

  play(): void {
    const ctx = this.ctx;
    if (!ctx || ctx.state !== "running") return;
    [880, 1318].forEach((f, i) => {
      const o = ctx.createOscillator();
      const g = ctx.createGain();
      const t = ctx.currentTime + i * 0.18;
      o.frequency.value = f;
      o.type = "sine";
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(0.2, t + 0.02);
      g.gain.exponentialRampToValueAtTime(0.0001, t + 0.3);
      o.connect(g).connect(ctx.destination);
      o.start(t);
      o.stop(t + 0.32);
    });
  }
}
