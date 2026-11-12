// Optional MCP server sign-in: OAuth 2.1 with PKCE and dynamic client registration, the browser coming back to
// 127.0.0.1 (no admin rights, no firewall rule: loopback only). Servers without OAuth take a pasted token instead.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Prosper
{
    static class Mcp
    {
        const int Port = 47823;
        static readonly string Redirect = "http://127.0.0.1:" + Port + "/callback";
        static readonly HttpClient Http;
        static readonly object Gate = new object();

        static Mcp()
        {
            var proxy = WebRequest.GetSystemWebProxy();
            proxy.Credentials = CredentialCache.DefaultCredentials;
            Http = new HttpClient(new HttpClientHandler { Proxy = proxy, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(30) };
        }

        static Settings S { get { return Settings.Current; } }

        public static bool SignedIn { get { return Secret.OpenText(S.McpAccessSealed).Length > 0 || Secret.OpenText(S.McpManualSealed).Length > 0; } }

        /// <summary>Opens the browser and waits (up to 5 minutes) for the sign-in to come back. Call off the UI thread.</summary>
        public static void SignIn()
        {
            string server = S.McpServer.Trim();
            if (!server.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new Exception("The MCP server address must start with https://");
            var meta = Discover(server);
            string tokenUrl = Json.S_(meta, "token_endpoint");
            if (S.McpClientId.Length == 0)
            {
                string reg = Json.S_(meta, "registration_endpoint");
                if (reg.Length == 0) throw new Exception("This server doesn't support sign-in from apps. Paste an access token instead.");
                S.McpClientId = Register(reg);
                Settings.Save();
            }
            string verifier = Random(48);
            string challenge;
            using (var sha = SHA256.Create()) challenge = B64(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string state = Random(16);

            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            try
            {
                string url = Json.S_(meta, "authorization_endpoint")
                    + "?response_type=code&client_id=" + Uri.EscapeDataString(S.McpClientId)
                    + "&redirect_uri=" + Uri.EscapeDataString(Redirect) + "&scope=" + Uri.EscapeDataString("mcp:read mcp:write")
                    + "&state=" + state + "&code_challenge=" + challenge + "&code_challenge_method=S256"
                    + "&resource=" + Uri.EscapeDataString(server);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Log.Write("mcp: sign-in opened in the browser");
                var p = WaitForRedirect(listener);
                if (p.ContainsKey("error")) throw new Exception("Sign-in refused: " + (p.ContainsKey("error_description") ? p["error_description"] : p["error"]));
                if (!p.ContainsKey("state") || p["state"] != state) throw new Exception("The sign-in answer didn't match. Try again.");
                if (!p.ContainsKey("code")) throw new Exception("No sign-in code came back.");
                var json = Token(tokenUrl, new Dictionary<string, string> {
                    { "grant_type", "authorization_code" }, { "code", p["code"] }, { "redirect_uri", Redirect },
                    { "client_id", S.McpClientId }, { "code_verifier", verifier }, { "resource", server },
                });
                Save(tokenUrl, json, Secret.OpenText(S.McpRefreshSealed));
                Log.Write("mcp: signed in");
            }
            finally { listener.Stop(); }
        }

        public static void SignOut() { S.ClearMcp(); S.McpManualSealed = ""; Settings.Save(); }

        /// <summary>A valid token (refreshed when it is about to run out), a pasted one, or null when none is needed or it lapsed.</summary>
        public static string AccessToken()
        {
            lock (Gate)
            {
                string manual = Secret.OpenText(S.McpManualSealed);
                if (manual.Length > 0) return manual;
                string access = Secret.OpenText(S.McpAccessSealed);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (access.Length > 0 && S.McpExpires > now + 60000) return access;
                string refresh = Secret.OpenText(S.McpRefreshSealed);
                if (refresh.Length == 0 || S.McpTokenUrl.Length == 0) return access.Length > 0 ? access : null;
                try
                {
                    var json = Token(S.McpTokenUrl, new Dictionary<string, string> {
                        { "grant_type", "refresh_token" }, { "refresh_token", refresh }, { "client_id", S.McpClientId }, { "resource", S.McpServer.Trim() },
                    });
                    Save(S.McpTokenUrl, json, refresh);
                    Log.Write("mcp: token refreshed");
                    return Secret.OpenText(S.McpAccessSealed);
                }
                catch (Exception e)
                {
                    Log.Write("mcp: refresh failed: " + e.GetType().Name);
                    S.ClearMcp(); Settings.Save();
                    return null;
                }
            }
        }

        static void Save(string tokenUrl, Dictionary<string, object> json, string oldRefresh)
        {
            long expiresIn = 3600; long.TryParse(Json.S_(json, "expires_in"), out expiresIn); if (expiresIn <= 0) expiresIn = 3600;
            string refresh = Json.S_(json, "refresh_token");
            S.McpTokenUrl = tokenUrl;
            S.McpAccessSealed = Secret.SealText(Json.S_(json, "access_token"));
            S.McpRefreshSealed = Secret.SealText(refresh.Length > 0 ? refresh : oldRefresh); // some servers keep the same one
            S.McpExpires = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn * 1000;
            Settings.Save();
        }

        static Dictionary<string, object> Discover(string server)
        {
            var u = new Uri(server);
            var meta = Json.Obj(Read(Http.GetAsync(u.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-authorization-server").Result));
            if (meta == null || Json.S_(meta, "authorization_endpoint").Length == 0) throw new Exception("This server doesn't support sign-in from apps. Paste an access token instead.");
            return meta;
        }

        static string Register(string url)
        {
            var body = Json.O("client_name", "Prosper Voice for Windows", "redirect_uris", new object[] { Redirect },
                "grant_types", new object[] { "authorization_code", "refresh_token" }, "response_types", new object[] { "code" },
                "token_endpoint_auth_method", "none");
            var json = Json.Obj(Read(Http.PostAsync(url, new StringContent(Json.Write(body), Encoding.UTF8, "application/json")).Result));
            Log.Write("mcp: registered as a client");
            return Json.S_(json, "client_id");
        }

        static Dictionary<string, object> Token(string url, Dictionary<string, string> form)
        {
            return Json.Obj(Read(Http.PostAsync(url, new FormUrlEncodedContent(form)).Result));
        }

        static string Read(HttpResponseMessage r)
        {
            using (r)
            {
                string raw = r.Content.ReadAsStringAsync().Result;
                if (!r.IsSuccessStatusCode)
                {
                    var j = Json.Obj(raw);
                    string msg = Json.S_(j, "error_description"); if (msg.Length == 0) msg = Json.S_(j, "error");
                    throw new Exception("MCP server " + (int)r.StatusCode + (msg.Length > 0 ? ": " + msg : ""));
                }
                return raw;
            }
        }

        /// <summary>Answers the browser on 127.0.0.1 until the /callback request arrives; returns its query parameters.</summary>
        static Dictionary<string, string> WaitForRedirect(TcpListener listener)
        {
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                if (!listener.Pending()) { System.Threading.Thread.Sleep(100); continue; }
                using (var client = listener.AcceptTcpClient())
                using (var stream = client.GetStream())
                {
                    string line = new StreamReader(stream, Encoding.ASCII).ReadLine() ?? ""; // GET /callback?code=… HTTP/1.1
                    var parts = line.Split(' ');
                    string target = parts.Length > 1 ? parts[1] : "";
                    if (!target.StartsWith("/callback"))
                    {
                        var nf = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        stream.Write(nf, 0, nf.Length);
                        continue;
                    }
                    var p = new Dictionary<string, string>();
                    int q = target.IndexOf('?');
                    if (q >= 0)
                        foreach (var kv in target.Substring(q + 1).Split('&'))
                        {
                            int e = kv.IndexOf('='); if (e <= 0) continue;
                            p[Uri.UnescapeDataString(kv.Substring(0, e))] = Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
                        }
                    bool ok = p.ContainsKey("code");
                    string html = "<html><body style='font-family:Segoe UI,sans-serif;background:#09090B;color:#fff;display:flex;align-items:center;" +
                        "justify-content:center;height:90vh;text-align:center'><div><h2 style='color:#E8C26A'>" +
                        (ok ? "Prosper is connected" : "Sign-in didn't finish") + "</h2><p>You can close this tab.</p></div></body></html>";
                    var bytes = Encoding.UTF8.GetBytes(html);
                    var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(head, 0, head.Length); stream.Write(bytes, 0, bytes.Length); stream.Flush();
                    return p;
                }
            }
            throw new Exception("The sign-in didn't come back within 5 minutes.");
        }

        static string Random(int n) { var b = new byte[n]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return B64(b); }
        static string B64(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    }
}
