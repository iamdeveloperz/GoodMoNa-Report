namespace GoodMoNa.Server.Infrastructure;

public sealed class PhotoStorage(DataPaths paths)
{
    /// <summary>JPEG 파일 시그니처(FF D8 FF)인지 확인합니다. 폰 페이지가 항상 JPEG로 다시 인코딩해서 보냅니다.</summary>
    public static bool LooksLikeJpeg(ReadOnlySpan<byte> head) =>
        head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;

    public async Task<string> SaveAsync(ReadOnlyMemory<byte> bytes, string extension, DateTime utcNow, CancellationToken ct)
    {
        var relative = Path.Combine("photos", utcNow.ToString("yyyy"), utcNow.ToString("MM"), $"{Guid.NewGuid():N}{extension}");
        var full = paths.Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // 임시 파일에 다 쓴 뒤 옮겨서, 중간에 끊겨도 반쯤 쓴 사진이 남지 않게 합니다.
        var temp = full + ".part";
        await File.WriteAllBytesAsync(temp, bytes.ToArray(), ct);
        File.Move(temp, full);
        return relative.Replace('\\', '/');
    }

    public string GetFullPath(string relativePath) => paths.Resolve(relativePath);
}
