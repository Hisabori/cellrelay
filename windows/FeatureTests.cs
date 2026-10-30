using System.Text;
using System.Text.Json;

namespace CellRelay;

static class FeatureTests
{
    public static void Run()
    {
        static string Code(object value) => "CR1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));
        const string key = "0123456789abcdef0123456789abcdef";
        var good = ConnectionProfile.Parse(Code(new { host = "172.20.10.1", token = key, port = 49152 }));
        if (good.Host != "172.20.10.1" || good.Token != key) throw new Exception("iOS profile import failed");
        var lowerPrefix = Code(new { host = "172.20.10.1", token = key, port = 60532 });
        var decoded = ConnectionProfile.Parse("  cr1:" + lowerPrefix[4..] + "\r\n");
        if (decoded.Port != 60532 || decoded.Token != key) throw new Exception("Lowercase prefix/whitespace import failed");
        bool keyExplained = false; try { ConnectionProfile.Parse(key); } catch (FormatException e) { keyExplained = e.Message.Contains("연결 키만"); }
        if (!keyExplained) throw new Exception("Raw key not distinguished from connection code");
        if (ConnectionProfile.Parse(Code(new { host = "172.20.10.1", token = key, port = 55123 })).Port != 55123) throw new Exception("Dynamic iPhone port import failed");
        var report = Diagnostics.Redact("echo " + key + " " + Code(good), key);
        if (report.Contains(key) || report.Contains("CR1:")) throw new Exception("Diagnostic credential redaction failed");
        if (Diagnostics.Redact("cr1:" + lowerPrefix[4..], key).Contains(lowerPrefix[4..])) throw new Exception("Lowercase code leaked in report");
        foreach (var input in new[] { "CR1:invalid!", Code(new { host = "http://example.com", token = key, port = 49152 }), Code(new { host = "172.20.10.1", token = key, port = 80 }), Code(new { host = "172.20.10.1", token = "bad", port = 49152 }), "CR1:" + new string('A', 5000) })
        {
            bool rejected = false; try { ConnectionProfile.Parse(input); } catch (FormatException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid profile accepted");
        }
        var path = Path.GetFullPath(Path.Combine("work", "settings-self-test.json"));
        new AppSettings("192.168.1.2", 8899, 55123).Save(path);
        var restored = AppSettings.Load(path);
        if (restored.Host != "192.168.1.2" || restored.ProxyPort != 8899 || restored.PhonePort != 55123 || File.ReadAllText(path).Contains(key)) throw new Exception("Settings persistence failed");
        File.WriteAllText(path, "{\"Host\":\"192.168.1.2\",\"ProxyPort\":8877}");
        if (AppSettings.Load(path).PhonePort != 49152) throw new Exception("Old settings migration failed");
        File.WriteAllText(path, "{broken");
        if (AppSettings.Load(path) != new AppSettings()) throw new Exception("Corrupt settings fallback failed");
        new AppSettings(CloseToTray:false).Save(path);if(AppSettings.Load(path).CloseToTray)throw new Exception("Tray preference persistence failed");
        File.WriteAllText(path,"{\"Host\":\"192.168.1.2\",\"ProxyPort\":8877}");if(!AppSettings.Load(path).CloseToTray)throw new Exception("Legacy tray preference default failed");
        new AppSettings().Save(path);
    }
}

