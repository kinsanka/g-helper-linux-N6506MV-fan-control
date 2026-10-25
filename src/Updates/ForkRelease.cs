using System.Security.Cryptography;
using System.Text.Json;

namespace GHelper.Linux.Updates;

public sealed record ForkAsset(string Name, string Url);
public sealed record ForkRelease(string Version, string PageUrl, ForkAsset? Asset, string ChecksumUrl)
{
    public const string Repository = "kinsanka/g-helper-linux-N6506MV-fan-control";
    public const string AppImageName = "GHelper-N6506MV-x86_64.AppImage";
    public static string RepositoryUrl => "https://github.com/" + Repository;
    public static string AssetName(bool appImage) => appImage ? AppImageName : "ghelper";

    public static ForkRelease Parse(string json, bool appImage)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) throw new FormatException("草稿版本未发布");
        string tag = root.GetProperty("tag_name").GetString() ?? throw new FormatException("缺少版本");
        string version = tag.TrimStart('v'); _ = CompareVersions(version, version);
        string page = root.GetProperty("html_url").GetString() ?? "";
        if (!page.StartsWith(RepositoryUrl + "/releases/tag/", StringComparison.Ordinal)) throw new FormatException("更新源不匹配");
        string prefix = RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
        ForkAsset? selected = null; string? checksum = null;
        foreach (var item in root.GetProperty("assets").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString() ?? "";
            string url = item.GetProperty("browser_download_url").GetString() ?? "";
            if (!url.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (name == AssetName(appImage) && url == prefix + name) selected = new(name, url);
            if (name == "SHA256SUMS" && url == prefix + name) checksum = url;
        }
        return new(version, page, selected, checksum ?? prefix + "SHA256SUMS");
    }

    public static async Task<ForkRelease> LatestAsync(HttpClient http, bool appImage)
    {
        try
        {
            string json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            return Parse(json, appImage);
        }
        catch (HttpRequestException)
        {
            // Public release pages work even if the anonymous API quota is exhausted.
            using var response = await http.GetAsync(RepositoryUrl + "/releases/latest", HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            string page = response.RequestMessage?.RequestUri?.AbsoluteUri ?? "";
            if (!page.StartsWith(RepositoryUrl + "/releases/tag/", StringComparison.Ordinal)) throw new FormatException("未找到已发布版本");
            string tag = Uri.UnescapeDataString(new Uri(page).Segments[^1]);
            string version = tag.TrimStart('v'); _ = CompareVersions(version, version);
            string prefix = RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
            string checksum = prefix + "SHA256SUMS";
            string sums = await http.GetStringAsync(checksum);
            string name = AssetName(appImage);
            bool exists = FindChecksum(sums, name, false) != null;
            return new(version, page, exists ? new(name, prefix + name) : null, checksum);
        }
    }

    public static string? FindChecksum(string text, string filename, bool required = true)
    {
        if (text.Length > 65536) throw new FormatException("校验文件过大");
        var matches = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            var fields = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[1].TrimStart('*') == filename && fields[0].Length == 64 && fields[0].All(Uri.IsHexDigit))
                matches.Add(fields[0].ToLowerInvariant());
        }
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1 || required) throw new FormatException("找不到唯一的安装包SHA256");
        return null;
    }
    public static void VerifyFile(string path, string expected)
    {
        using var file = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(file));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装包SHA256不一致，更新已取消");
    }

    public static int CompareVersions(string a, string b)
    {
        static (int[] core, string[] pre) Parts(string input)
        {
            var halves = input.TrimStart('v').Split('+')[0].Split('-', 2);
            var core = halves[0].Split('.').Select(int.Parse).ToArray();
            if (core.Length != 3 || core.Any(x => x < 0)) throw new FormatException("版本号格式错误");
            var pre = halves.Length == 2 ? halves[1].Split('.') : [];
            if (pre.Any(x => x.Length == 0 || !x.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) throw new FormatException("版本号格式错误");
            return (core, pre);
        }
        var x = Parts(a); var y = Parts(b);
        for (int i = 0; i < 3; i++) { int c = x.core[i].CompareTo(y.core[i]); if (c != 0) return c; }
        if (x.pre.Length == 0 || y.pre.Length == 0) return (x.pre.Length == 0).CompareTo(y.pre.Length == 0);
        for (int i = 0; i < Math.Min(x.pre.Length, y.pre.Length); i++)
        {
            string l = x.pre[i], r = y.pre[i]; bool ln = l.All(char.IsAsciiDigit), rn = r.All(char.IsAsciiDigit); int c;
            if (ln && rn)
            {
                l = l.TrimStart('0'); r = r.TrimStart('0'); c = l.Length.CompareTo(r.Length);
                if (c == 0) c = string.CompareOrdinal(l, r);
            }
            else c = ln != rn ? (ln ? -1 : 1) : string.CompareOrdinal(l, r);
            if (c != 0) return c;
        }
        return x.pre.Length.CompareTo(y.pre.Length);
    }
}
