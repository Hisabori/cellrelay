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
        var report = Diagnostics.Redact("echo " + key + " " + Code(good), key);
        if (report.Contains(key) || report.Contains("CR1:")) throw new Exception("Diagnostic credential redaction failed");
        foreach (var input in new[] { "CR1:invalid!", Code(new { host = "http://example.com", token = key, port = 49152 }), Code(new { host = "172.20.10.1", token = key, port = 80 }), Code(new { host = "172.20.10.1", token = "bad", port = 49152 }), "CR1:" + new string('A', 5000) })
        {
            bool rejected = false; try { ConnectionProfile.Parse(input); } catch (FormatException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid profile accepted");
        }
        var path = Path.GetFullPath(Path.Combine("work", "settings-self-test.json"));
        new AppSettings("192.168.1.2", 8899).Save(path);
        var restored = AppSettings.Load(path);
        if (restored.Host != "192.168.1.2" || restored.ProxyPort != 8899 || File.ReadAllText(path).Contains(key)) throw new Exception("Settings persistence failed");
        File.WriteAllText(path, "{broken");
        if (AppSettings.Load(path) != new AppSettings()) throw new Exception("Corrupt settings fallback failed");
        new AppSettings().Save(path);
    }
}
