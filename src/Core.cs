// Prosper Voice for Windows: settings, encrypted secrets, JSON and the content-free log.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Run with .NET Framework 4.8 behaviour (TLS defaults chosen by Windows, not the legacy ones).
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Prosper Voice")]
[assembly: AssemblyProduct("Prosper Voice")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace Prosper
{
    /// <summary>Where Prosper keeps its files: %APPDATA%\ProsperVoice, the user's own profile (no admin rights).</summary>
    static class Paths
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProsperVoice");
        public static readonly string History = Path.Combine(Dir, "history");
        public static readonly string Temp = Path.Combine(Path.GetTempPath(), "ProsperVoice");
        public static string File(string name) { Directory.CreateDirectory(Dir); return Path.Combine(Dir, name); }
    }

    /// <summary>Timings and status codes only: never text, audio, screenshots or keys.</summary>
    static class Log
    {
        static readonly object Gate = new object();
        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    string f = Paths.File("prosper.log");
                    if (System.IO.File.Exists(f) && new FileInfo(f).Length > 512 * 1024) System.IO.File.Delete(f);
                    System.IO.File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    /// <summary>Windows DPAPI, per user: only this Windows account on this PC can read what is sealed.</summary>
    static class Secret
    {
        static readonly byte[] Salt = Encoding.UTF8.GetBytes("ProsperVoice.v1");
        public static byte[] Seal(byte[] data) { return ProtectedData.Protect(data, Salt, DataProtectionScope.CurrentUser); }
        public static byte[] Open(byte[] data) { return ProtectedData.Unprotect(data, Salt, DataProtectionScope.CurrentUser); }
        public static string SealText(string s) { return string.IsNullOrEmpty(s) ? "" : Convert.ToBase64String(Seal(Encoding.UTF8.GetBytes(s))); }
        public static string OpenText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Encoding.UTF8.GetString(Open(Convert.FromBase64String(s))); } catch { return ""; }
        }
    }

    /// <summary>JSON through the framework's own serializer: objects are Dictionary&lt;string, object&gt;, arrays object[].</summary>
    static class Json
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
        public static string Write(object o) { return S.Serialize(o); }
        public static T Read<T>(string s) { return S.Deserialize<T>(s); }
        public static Dictionary<string, object> Obj(string s)
        {
            try { return S.DeserializeObject(s) as Dictionary<string, object>; } catch { return null; }
        }
        /// <summary>Builds an object: Json.O("model", m, "input", x).</summary>
        public static Dictionary<string, object> O(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) if (kv[i + 1] != null) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
        public static Dictionary<string, object> D(object o, string k)
        {
            var d = o as Dictionary<string, object>; object v;
            return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }
        public static object[] A(object o, string k)
        {
            var d = o as Dictionary<string, object>; object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return new object[0];
            var arr = v as object[]; if (arr != null) return arr;
            var list = v as ArrayList; return list != null ? list.ToArray() : new object[0];
        }
        /// <summary>A value as text ("" when missing or null); numbers and booleans as written.</summary>
        public static string S_(object o, string k)
        {
            var d = o as Dictionary<string, object>; object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return "";
            var s = v as string; return s ?? (v is bool ? ((bool)v ? "true" : "false") : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
        }
        public static bool Has(object o, string k)
        {
            var d = o as Dictionary<string, object>; object v;
            return d != null && d.TryGetValue(k, out v) && v != null;
        }
    }

    /// <summary>Everything the user sets. Secrets are stored sealed with <see cref="Secret"/>.</summary>
    class Settings
    {
        public string ApiBase { get; set; }          // https://api.openai.com/v1, or an Azure OpenAI / company endpoint
        public string Model { get; set; }
        public string TranscribeModel { get; set; }
        public string AboutMe { get; set; }
        public bool Cleanup { get; set; }
        public bool ScreenshotButton { get; set; }
        public bool KeepHistory { get; set; }
        public bool ShowBubble { get; set; }
        public int BarX { get; set; }
        public int BarY { get; set; }
        public string ApiKeySealed { get; set; }
        // MCP: off until the user connects a server.
        public bool McpOn { get; set; }
        public string McpServer { get; set; }
        public string McpAllowed { get; set; }       // tools that may run without asking, comma separated
        public string McpClientId { get; set; }
        public string McpTokenUrl { get; set; }
        public long McpExpires { get; set; }
        public string McpAccessSealed { get; set; }
        public string McpRefreshSealed { get; set; }
        public string McpManualSealed { get; set; }  // a token pasted by hand, for servers without OAuth

        public Settings()
        {
            ApiBase = "https://api.openai.com/v1"; Model = "gpt-6-luna"; TranscribeModel = "gpt-4o-mini-transcribe"; AboutMe = "";
            Cleanup = true; ScreenshotButton = true; KeepHistory = true; ShowBubble = true; BarX = -1; BarY = -1;
            ApiKeySealed = ""; McpServer = ""; McpAllowed = ""; McpClientId = ""; McpTokenUrl = "";
            McpAccessSealed = ""; McpRefreshSealed = ""; McpManualSealed = "";
        }

        public string ApiKey { get { return Secret.OpenText(ApiKeySealed); } }
        public void SetApiKey(string k) { ApiKeySealed = Secret.SealText((k ?? "").Trim()); }
        public bool Configured { get { return ApiKey.Length > 0; } }

        public List<string> Allowed()
        {
            var l = new List<string>();
            foreach (var t in (McpAllowed ?? "").Split(',')) if (t.Trim().Length > 0) l.Add(t.Trim());
            return l;
        }
        public void Allow(string tool)
        {
            var l = Allowed(); if (!l.Contains(tool)) l.Add(tool);
            McpAllowed = string.Join(", ", l);
        }
        public void ClearMcp()
        {
            McpClientId = ""; McpTokenUrl = ""; McpExpires = 0; McpAccessSealed = ""; McpRefreshSealed = "";
        }

        static Settings current;
        public static Settings Current
        {
            get
            {
                if (current != null) return current;
                try { current = Json.Read<Settings>(System.IO.File.ReadAllText(Paths.File("settings.json"))); } catch { }
                if (current == null) current = new Settings();
                return current;
            }
        }
        public static void Save()
        {
            try { System.IO.File.WriteAllText(Paths.File("settings.json"), Json.Write(Current)); } catch (Exception e) { Log.Write("settings not saved: " + e.GetType().Name); }
        }
    }
}
