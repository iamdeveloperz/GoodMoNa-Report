using GoodMoNa.Server.Infrastructure;

namespace GoodMoNa.Server.Import;

/// <summary>
/// 가져오기 폴더(데이터 폴더\import) 안의 사진 목록. CSV의 '사진' 칸은 하위 폴더를 포함한 경로(2025/kim.jpg)나
/// 파일 이름만(kim.jpg) 써도 됩니다. 파일 이름만 쓴 경우 같은 이름이 두 개 이상이면 어느 것인지 알 수 없으므로 오류입니다.
/// </summary>
public sealed class PhotoIndex
{
    private readonly string _root;
    private readonly Dictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Extension, string ContentType)?> _sniffed = new(StringComparer.OrdinalIgnoreCase);

    private PhotoIndex(string root) => _root = root;

    public static PhotoIndex Build(string root)
    {
        var index = new PhotoIndex(root);
        if (!Directory.Exists(root)) return index;
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (full.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            index._byPath[Normalize(Path.GetRelativePath(root, full))] = full;
            var name = Path.GetFileName(full);
            if (!index._byName.TryGetValue(name, out var list)) index._byName[name] = list = [];
            list.Add(full);
        }
        return index;
    }

    public static string Normalize(string relative) => relative.Replace('\\', '/').Trim().TrimStart('.', '/');

    public PhotoFile? Resolve(string reference, out string? problem)
    {
        problem = null;
        var key = Normalize(reference);
        if (key.Length == 0 || key.Split('/').Any(p => p == ".."))
        {
            problem = $"사진 '{reference}' 경로가 올바르지 않음";
            return null;
        }

        if (!_byPath.TryGetValue(key, out var full))
        {
            var matches = _byName.GetValueOrDefault(Path.GetFileName(key));
            if (matches is null or { Count: 0 })
            {
                problem = $"사진 '{reference}'을(를) 가져오기 폴더에서 찾지 못함";
                return null;
            }
            if (matches.Count > 1)
            {
                problem = $"사진 '{reference}'과(와) 이름이 같은 파일이 {matches.Count}개 — 하위 폴더까지 적어 주세요";
                return null;
            }
            full = matches[0];
        }

        var info = new FileInfo(full);
        if (info.Length > LegacyImporter.MaxPhotoBytes)
        {
            problem = $"사진 '{reference}'이(가) {LegacyImporter.MaxPhotoBytes / 1024 / 1024}MB보다 큼";
            return null;
        }

        if (!_sniffed.TryGetValue(full, out var kind))
        {
            Span<byte> head = stackalloc byte[12];
            using (var fs = File.OpenRead(full))
                head = head[..fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
            _sniffed[full] = kind = MediaStorage.SniffImage(head);
        }
        if (kind is null)
        {
            problem = $"사진 '{reference}'은(는) JPG·PNG·WebP가 아님 (아이폰 HEIC는 JPG로 바꿔 주세요)";
            return null;
        }

        return new PhotoFile(full, kind.Value.Extension, kind.Value.ContentType);
    }
}
