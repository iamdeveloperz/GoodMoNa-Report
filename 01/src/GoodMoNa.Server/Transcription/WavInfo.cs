using System.Buffers.Binary;

namespace GoodMoNa.Server.Transcription;

/// <summary>
/// 브라우저가 보낸 WAV 머리말을 읽어 Whisper 입력 형식(16kHz · 모노 · 16bit PCM)인지 확인하고 길이를 계산합니다.
/// 녹음 길이는 클라이언트가 말한 값이 아니라 실제 데이터 크기로 정합니다.
/// </summary>
public readonly record struct WavInfo(int SampleRate, int Channels, int BitsPerSample, int DataBytes)
{
    public int DurationMs => (int)(DataBytes * 1000L / (SampleRate * Channels * (BitsPerSample / 8)));

    public bool IsWhisperReady => SampleRate == 16000 && Channels == 1 && BitsPerSample == 16;

    public static bool TryRead(ReadOnlySpan<byte> file, out WavInfo info)
    {
        info = default;
        if (file.Length < 44 || !file[..4].SequenceEqual("RIFF"u8) || !file.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;

        int? rate = null, channels = null, bits = null;
        var pos = 12;
        while (pos + 8 <= file.Length)
        {
            var id = file.Slice(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(pos + 4, 4));
            if (size < 0) return false;
            var body = pos + 8;

            if (id.SequenceEqual("fmt "u8) && body + 16 <= file.Length)
            {
                var format = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body, 2));
                if (format != 1) return false; // PCM만
                channels = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (rate is null || channels is null || bits is null || rate <= 0 || channels <= 0 || bits < 8) return false;
                var data = Math.Min(size, file.Length - body);
                info = new WavInfo(rate.Value, channels.Value, bits.Value, data);
                return true;
            }

            pos = body + size + (size & 1); // 청크는 짝수 바이트로 정렬
        }
        return false;
    }
}
