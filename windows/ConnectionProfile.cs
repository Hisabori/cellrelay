using System.Text;
using System.Text.Json;

namespace CellRelay;

static class Diagnostics
{
    public static string Redact(string text, string token)
    {
        if (token.Length >= 16) text = text.Replace(token, "[연결 키 숨김]", StringComparison.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(text, @"CR1:[A-Za-z0-9+/=]+", "[연결 코드 숨김]");
    }
}

sealed record ConnectionProfile(string Host, string Token, int Port = 49152)
{
    public static ConnectionProfile Parse(string text)
    {
        text = text.Trim();
        if (text.Length > 4096 || !text.StartsWith("CR1:", StringComparison.Ordinal)) throw new FormatException("CellRelay 연결 정보(CR1:)를 붙여 넣으세요.");
        try
        {
            var profile = JsonSerializer.Deserialize<ConnectionProfile>(Convert.FromBase64String(text[4..]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (profile is null || string.IsNullOrEmpty(profile.Host) || Uri.CheckHostName(profile.Host) == UriHostNameType.Unknown || profile.Port != 49152 || profile.Token is null || profile.Token.Length != 32 || !profile.Token.All(Uri.IsHexDigit)) throw new FormatException();
            return profile;
        }
        catch (Exception e) when (e is JsonException or FormatException) { throw new FormatException("연결 정보가 손상됐거나 지원하지 않는 형식입니다."); }
    }
}

sealed record AppSettings(string Host = "172.20.10.1", int ProxyPort = 8877)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CellRelay", "settings.json");
    public static AppSettings Load(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            return value is not null && Uri.CheckHostName(value.Host ?? "") != UriHostNameType.Unknown && value.ProxyPort is >= 1024 and <= 65535 ? value : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temp, path, true);
    }
}
