namespace GoodMoNa.Server.Infrastructure;

/// <summary>사진·녹음 파일을 데이터 폴더 아래 연/월 폴더에 저장합니다.</summary>
public sealed class MediaStorage(DataPaths paths)
{
    /// <summary>JPEG 파일 시그니처(FF D8 FF)인지 확인합니다. 폰 페이지가 항상 JPEG로 다시 인코딩해서 보냅니다.</summary>
    public static bool LooksLikeJpeg(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;

    public async Task<string> SaveAsync(ReadOnlyMemory<byte> bytes, string folder, string extension, DateTime utcNow, CancellationToken ct)
    {
        var relative = Path.Combine(folder, utcNow.ToString("yyyy"), utcNow.ToString("MM"), $"{Guid.NewGuid():N}{extension}");
        var full = paths.Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // 임시 파일에 다 쓴 뒤 옮겨서, 중간에 끊겨도 반쯤 쓴 파일이 남지 않게 합니다.
        var temp = full + ".part";
        await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await fs.WriteAsync(bytes, ct);
        File.Move(temp, full);
        return relative.Replace('\\', '/');
    }

    public string GetFullPath(string relativePath) => paths.Resolve(relativePath);

    /// <summary>지우다 실패해도(다른 곳에서 열고 있는 경우) 예외를 던지지 않습니다. 실패하면 false.</summary>
    public bool TryDelete(string relativePath)
    {
        try
        {
            var full = paths.Resolve(relativePath);
            if (File.Exists(full)) File.Delete(full);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
