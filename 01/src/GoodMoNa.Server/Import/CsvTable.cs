using System.Text;

namespace GoodMoNa.Server.Import;

/// <summary>
/// 엑셀·다른 전산에서 내보낸 CSV를 읽습니다.
/// 한국어 엑셀의 "CSV(쉼표로 분리)"는 UTF-8이 아니라 CP949로 저장되므로 인코딩을 자동으로 가립니다.
/// 따옴표로 감싼 칸 안의 쉼표·줄바꿈("" 이스케이프 포함)을 지원하고, 첫 줄에 탭이 더 많으면 탭 구분으로 읽습니다.
/// </summary>
public static class CsvTable
{
    static CsvTable() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(byte[] bytes, out string encodingName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encodingName = "UTF-8";
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encodingName = "UTF-16";
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            encodingName = "UTF-8";
            return text;
        }
        catch (DecoderFallbackException)
        {
            encodingName = "CP949 (엑셀 한글)";
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }

    /// <summary>행 목록. 인덱스 0이 머리글(엑셀 1행)입니다.</summary>
    public static List<string[]> Parse(string text)
    {
        var lineEnd = text.IndexOfAny(['\r', '\n']);
        var head = lineEnd < 0 ? text : text[..lineEnd];
        var delimiter = head.Count(c => c == '\t') > head.Count(c => c == ',') ? '\t' : ',';

        var rows = new List<string[]>();
        var fields = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var cellStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
                continue;
            }

            if (c == '"' && !cellStarted) { quoted = true; cellStarted = true; }
            else if (c == delimiter) { fields.Add(cell.ToString()); cell.Clear(); cellStarted = false; }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(cell.ToString());
                cell.Clear();
                cellStarted = false;
                rows.Add([.. fields]);
                fields.Clear();
            }
            else { cell.Append(c); cellStarted = true; }
        }
        if (cellStarted || fields.Count > 0)
        {
            fields.Add(cell.ToString());
            rows.Add([.. fields]);
        }
        return rows;
    }
}
