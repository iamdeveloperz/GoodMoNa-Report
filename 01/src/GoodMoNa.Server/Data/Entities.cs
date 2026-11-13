namespace GoodMoNa.Server.Data;

public enum VisitStatus
{
    Draft,
    Reported
}

public enum Progress
{
    Improved,
    Unchanged,
    Worsened
}

public sealed class Patient
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateOnly BirthDate { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<Visit> Visits { get; set; } = [];
}

/// <summary>환자 한 명의 하루 방문. 환자당 날짜별로 하나만 존재합니다.</summary>
public sealed class Visit
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    public DateOnly VisitDate { get; set; }
    public VisitStatus Status { get; set; }
    public Progress? Progress { get; set; }
    public string Memo { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ReportedAt { get; set; }
    public string? ReportedBy { get; set; }

    /// <summary>다시 찍은 사진도 모두 남깁니다. 화면에는 가장 최근 사진을 씁니다.</summary>
    public List<Photo> Photos { get; set; } = [];

    /// <summary>원장 소견 녹음. 한 방문에 여러 개가 쌓일 수 있습니다.</summary>
    public List<VoiceNote> VoiceNotes { get; set; } = [];
}

public enum NoteStatus
{
    /// <summary>업로드 완료, 변환 차례를 기다리는 중</summary>
    Queued,
    Transcribing,
    Transcribed,
    Failed
}

public enum NoteSource
{
    InApp,
    FilePick,
    NativeApp
}

/// <summary>원장 녹음 파일 하나. 변환이 끝나면 <see cref="Transcript"/>가 1:1로 붙습니다.</summary>
public sealed class VoiceNote
{
    public int Id { get; set; }
    public int VisitId { get; set; }
    public Visit Visit { get; set; } = null!;

    /// <summary>방문 안에서의 순번 (소견 1, 소견 2 …). 지워도 다른 녹음의 번호는 바뀌지 않습니다.</summary>
    public int Seq { get; set; }
    public required string StoragePath { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public int DurationMs { get; set; }
    public NoteSource Source { get; set; }
    public NoteStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }

    public Transcript? Transcript { get; set; }
}

/// <summary>녹음 하나를 Whisper로 변환한 스크립트. 녹음이 지워지면 함께 지워집니다(cascade).</summary>
public sealed class Transcript
{
    public int Id { get; set; }
    public int VoiceNoteId { get; set; }
    public VoiceNote VoiceNote { get; set; } = null!;
    public required string Text { get; set; }

    /// <summary>[{ "s": 시작ms, "e": 끝ms, "t": 문장 }] — 재생 위치와 문장을 맞출 때 씁니다.</summary>
    public required string SegmentsJson { get; set; }
    public required string Model { get; set; }
    public required string Runtime { get; set; }
    public int ElapsedMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Photo
{
    public int Id { get; set; }
    public int VisitId { get; set; }
    public Visit Visit { get; set; } = null!;

    /// <summary>데이터 폴더 기준 상대 경로 (photos/2026/11/xxxx.jpg).</summary>
    public required string StoragePath { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }

    /// <summary>파일 SHA-256 (hex). 이전 기록 가져오기를 다시 돌려도 같은 사진이 두 번 들어가지 않게 합니다.</summary>
    public string? ContentHash { get; set; }
}

/// <summary>QR로 폰에 넘기는 일회용 촬영 권한. 원문 토큰은 저장하지 않고 SHA-256 해시만 둡니다.</summary>
public sealed class CaptureToken
{
    public int Id { get; set; }
    public required string TokenHash { get; set; }
    public int VisitId { get; set; }
    public Visit Visit { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
