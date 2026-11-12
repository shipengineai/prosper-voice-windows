// Conversation history on this PC: one DPAPI-sealed file per conversation (screenshots included), and the HTML the
// history window and answer card show (Markdown answers rendered).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Prosper
{
    class Msg
    {
        public string Role { get; set; }      // user | assistant | tool | error | audio
        public string Text { get; set; }      // what was said, the answer, the tool call ("POST api…"), the file name
        public bool Status { get; set; }      // assistant: a status line after actions
        public string Image { get; set; }     // user: the screenshot sent (base64 JPEG)
        public string Request { get; set; }   // tool: its arguments
        public string Response { get; set; }  // tool: its output; audio: the transcript
        public bool Ok { get; set; }
        public long Ms { get; set; }
    }

    class Conversation
    {
        public string Id { get; set; }
        public string Kind { get; set; }      // dictation | rewrite | assistant | chat
        public string App { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public string ResponseId { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public List<Msg> Messages { get; set; }
        public Conversation() { Messages = new List<Msg>(); }
    }

    static class History
    {
        const int MaxBody = 100 * 1024;
        const int Keep = 500;
        static readonly Dictionary<string, Conversation> Memory = new Dictionary<string, Conversation>();
        static bool loaded;
        public static event Action Changed;

        static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); } }
        static string FileOf(string id) { return Path.Combine(Paths.History, id + ".bin"); }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            if (!Directory.Exists(Paths.History)) return;
            foreach (var f in Directory.GetFiles(Paths.History, "*.bin"))
            {
                try
                {
                    var c = Json.Read<Conversation>(Encoding.UTF8.GetString(Secret.Open(File.ReadAllBytes(f))));
                    if (c != null && c.Id != null) Memory[c.Id] = c;
                }
                catch { } // sealed for another Windows account, or damaged
            }
        }

        public static List<Conversation> All()
        {
            lock (Memory) { Load(); return Memory.Values.OrderByDescending(c => c.UpdatedAt).ToList(); }
        }

        public static Conversation Get(string id)
        {
            lock (Memory) { Load(); Conversation c; return id != null && Memory.TryGetValue(id, out c) ? c : null; }
        }

        static void Write(Conversation c)
        {
            if (!Settings.Current.KeepHistory) return; // kept for this session only
            try
            {
                Directory.CreateDirectory(Paths.History);
                File.WriteAllBytes(FileOf(c.Id), Secret.Seal(Encoding.UTF8.GetBytes(Json.Write(c))));
            }
            catch (Exception e) { Log.Write("history not saved: " + e.GetType().Name); }
        }

        static string OneLine(string s)
        {
            var line = (s ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            line = Markdown.Plain(line);
            return line.Length > 160 ? line.Substring(0, 160) : line;
        }

        static string Cut(string s) { return s != null && s.Length > MaxBody ? s.Substring(0, MaxBody) : s; }

        /// <summary>Adds one request and what came of it; a new conversation when [id] is null. Returns the conversation.</summary>
        public static Conversation Add(string id, string kind, string app, string said, AssistResult result, string error,
            byte[] screenshot = null, string audioName = null, string audioText = null)
        {
            Conversation c;
            lock (Memory)
            {
                Load();
                c = Get(id);
                if (c == null)
                {
                    string first = said;
                    if (string.IsNullOrWhiteSpace(first)) first = audioName ?? "";
                    c = new Conversation { Id = Now.ToString(), Kind = kind, App = app, Title = OneLine(first), CreatedAt = Now };
                    Memory[c.Id] = c;
                }
                if (audioName != null) c.Messages.Add(new Msg { Role = "audio", Text = audioName, Response = Cut(audioText) });
                if (!string.IsNullOrWhiteSpace(said) || audioName == null)
                    c.Messages.Add(new Msg { Role = "user", Text = said ?? "", Image = screenshot != null ? Convert.ToBase64String(screenshot) : null });
                if (result != null)
                {
                    foreach (var t in result.Calls)
                        c.Messages.Add(new Msg { Role = "tool", Text = t.What, Request = Cut(t.Arguments), Response = Cut(t.Error ?? t.Output), Ok = t.Ok, Ms = t.Ms });
                    c.Messages.Add(new Msg { Role = "assistant", Text = result.Text, Status = result.IsStatus });
                    if (result.ResponseId != null) c.ResponseId = result.ResponseId;
                }
                if (error != null) c.Messages.Add(new Msg { Role = "error", Text = error });
                c.Summary = OneLine(error ?? (result != null ? result.Text : audioName != null ? "Transcript · " + audioName : ""));
                c.UpdatedAt = Now;
                Write(c);
                Prune();
            }
            var h = Changed; if (h != null) h();
            return c;
        }

        static void Prune()
        {
            foreach (var old in Memory.Values.OrderByDescending(c => c.UpdatedAt).Skip(Keep).ToList()) Remove(old.Id);
        }

        static void Remove(string id)
        {
            Memory.Remove(id);
            try { File.Delete(FileOf(id)); } catch { }
        }

        public static void Delete(string id)
        {
            lock (Memory) { Load(); Remove(id); }
            var h = Changed; if (h != null) h();
        }

        public static void Clear()
        {
            lock (Memory)
            {
                Memory.Clear();
                try { if (Directory.Exists(Paths.History)) Directory.Delete(Paths.History, true); } catch { }
            }
            var h = Changed; if (h != null) h();
        }

        /// <summary>The conversation as plain text, for a follow-up OpenAI can no longer continue by id.</summary>
        public static string Transcript(Conversation c)
        {
            var b = new StringBuilder();
            foreach (var m in c.Messages)
            {
                switch (m.Role)
                {
                    case "user": b.AppendLine("User: " + m.Text); break;
                    case "assistant": b.AppendLine("You: " + m.Text); break;
                    case "tool": b.AppendLine("(tool " + (m.Ok ? "ran" : "failed") + ": " + m.Text + ")"); break;
                    case "audio": b.AppendLine("User attached audio " + m.Text + ". Its transcript:\n" + m.Response); break;
                    default: b.AppendLine("(error: " + m.Text + ")"); break;
                }
            }
            string s = b.ToString();
            return s.Length > 80000 ? s.Substring(s.Length - 80000) : s;
        }
    }

    /// <summary>Small Markdown to HTML: headings, lists, quotes, code, tables, links, bold and italics. Input is escaped first.</summary>
    static class Markdown
    {
        static string E(string s) { return WebUtility.HtmlEncode(s); }

        static string Inline(string s)
        {
            s = E(s);
            var codes = new List<string>();
            s = Regex.Replace(s, "`([^`]+)`", m => { codes.Add("<code>" + m.Groups[1].Value + "</code>"); return "\u0001" + (codes.Count - 1) + "\u0002"; });
            s = Regex.Replace(s, "!\\[([^\\]]*)\\]\\((https?://[^)\\s]+)\\)", "<a href=\"$2\">$1 (image)</a>");
            s = Regex.Replace(s, "\\[([^\\]]+)\\]\\((https?://[^)\\s]+)\\)", "<a href=\"$2\">$1</a>");
            s = Regex.Replace(s, "(?<![\"=>])(https?://[^\\s<]+[^\\s<.,;:!?)])", "<a href=\"$1\">$1</a>");
            s = Regex.Replace(s, "\\*\\*(.+?)\\*\\*", "<b>$1</b>");
            s = Regex.Replace(s, "(?<![\\w*])\\*(?!\\s)(.+?)(?<!\\s)\\*(?![\\w*])", "<i>$1</i>");
            s = Regex.Replace(s, "(?<!\\w)_(?!\\s)(.+?)(?<!\\s)_(?!\\w)", "<i>$1</i>");
            s = Regex.Replace(s, "~~(.+?)~~", "<s>$1</s>");
            return Regex.Replace(s, "\u0001(\\d+)\u0002", m => codes[int.Parse(m.Groups[1].Value)]);
        }

        public static string Html(string md)
        {
            var lines = (md ?? "").Replace("\r", "").Split('\n');
            var b = new StringBuilder();
            var para = new List<string>();
            string list = null;
            Action flush = () => { if (para.Count > 0) { b.Append("<p>" + string.Join("<br>", para.Select(Inline)) + "</p>"); para.Clear(); } };
            Action endList = () => { if (list != null) { b.Append("</" + list + ">"); list = null; } };
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.TrimStart().StartsWith("```"))
                {
                    flush(); endList();
                    var code = new StringBuilder();
                    for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Append(E(lines[i])).Append('\n');
                    b.Append("<pre>" + code + "</pre>");
                    continue;
                }
                if (l.Trim().Length == 0) { flush(); endList(); continue; }
                var h = Regex.Match(l, "^(#{1,6})\\s+(.*)$");
                if (h.Success) { flush(); endList(); int n = Math.Min(h.Groups[1].Length + 1, 6); b.Append("<h" + n + ">" + Inline(h.Groups[2].Value) + "</h" + n + ">"); continue; }
                if (Regex.IsMatch(l, "^\\s*([-*_])\\s*\\1\\s*\\1[\\s\\-*_]*$")) { flush(); endList(); b.Append("<hr>"); continue; }
                if (l.TrimStart().StartsWith("|") && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], "^\\s*\\|?\\s*:?-{3,}"))
                {
                    flush(); endList();
                    Func<string, string[]> cells = r => r.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                    b.Append("<table><tr>" + string.Concat(cells(l).Select(x => "<th>" + Inline(x) + "</th>")) + "</tr>");
                    for (i += 2; i < lines.Length && lines[i].TrimStart().StartsWith("|"); i++)
                        b.Append("<tr>" + string.Concat(cells(lines[i]).Select(x => "<td>" + Inline(x) + "</td>")) + "</tr>");
                    i--;
                    b.Append("</table>");
                    continue;
                }
                var q = Regex.Match(l, "^\\s*>\\s?(.*)$");
                if (q.Success) { flush(); endList(); b.Append("<blockquote>" + Inline(q.Groups[1].Value) + "</blockquote>"); continue; }
                var ul = Regex.Match(l, "^\\s*[-*+]\\s+(.*)$");
                var ol = Regex.Match(l, "^\\s*\\d+[.)]\\s+(.*)$");
                if (ul.Success || ol.Success)
                {
                    flush();
                    string want = ul.Success ? "ul" : "ol";
                    if (list != want) { endList(); b.Append("<" + want + ">"); list = want; }
                    b.Append("<li>" + Inline((ul.Success ? ul : ol).Groups[1].Value) + "</li>");
                    continue;
                }
                endList();
                para.Add(l);
            }
            flush(); endList();
            return b.ToString();
        }

        /// <summary>One-line previews: the text without Markdown marks.</summary>
        public static string Plain(string md)
        {
            string s = Regex.Replace(md ?? "", "^\\s{0,3}#{1,6}\\s+|^\\s*>\\s?", "");
            s = Regex.Replace(s, "!?\\[([^\\]]*)\\]\\([^)]*\\)", "$1");
            s = Regex.Replace(s, "(\\*\\*|~~|\\*|`)(?=\\S)(.+?)(?<=\\S)\\1", "$2");
            return s;
        }

        public const string Style = "<meta http-equiv='X-UA-Compatible' content='IE=edge'><meta charset='utf-8'><style>" +
            "body{font:14px 'Segoe UI',sans-serif;color:#18181B;background:#FAFAF9;margin:12px;line-height:1.5;word-wrap:break-word}" +
            "h2,h3,h4,h5,h6{margin:12px 0 6px}p{margin:6px 0}pre{background:#F1F1F3;padding:8px 10px;border-radius:6px;white-space:pre-wrap;font:12px Consolas,monospace}" +
            "code{background:#F1F1F3;padding:0 4px;border-radius:4px;font:12px Consolas,monospace}a{color:#0F6B4F}" +
            "table{border-collapse:collapse;margin:6px 0}td,th{border:1px solid #E4E4E7;padding:4px 8px;text-align:left}th{background:#F1F1F3}" +
            "blockquote{border-left:3px solid #E8C26A;margin:6px 0;padding:2px 10px;color:#52525B}ul,ol{margin:4px 0 4px 22px;padding:0}" +
            ".user{margin:10px 0 10px 60px;background:#0F6B4F;color:#fff;padding:8px 12px;border-radius:14px 14px 4px 14px}" +
            ".user img{max-width:220px;border-radius:8px;display:block;margin-bottom:6px}" +
            ".bot{margin:10px 60px 10px 0;background:#fff;border:1px solid #E4E4E7;padding:8px 12px;border-radius:14px 14px 14px 4px}" +
            ".tool{margin:4px 60px 4px 0;font:12px Consolas,monospace;color:#3F3F46}.tool pre{max-height:180px;overflow:auto}" +
            ".err{color:#B91C1C;margin:8px 0}.lbl{font-size:10px;letter-spacing:1px;color:#A16207;font-weight:600}" +
            ".audio{margin:10px 0 10px 60px;border:1px solid #E8C26A;background:#fff;padding:8px 12px;border-radius:14px}</style>";

        public static string Page(string inner) { return "<!DOCTYPE html><html><head>" + Style + "</head><body>" + inner + "</body></html>"; }

        static string Short(string s, int n) { return s == null ? "" : s.Length > n ? s.Substring(0, n) + "…" : s; }

        public static string Conversation(Conversation c)
        {
            var b = new StringBuilder();
            foreach (var m in c.Messages)
            {
                switch (m.Role)
                {
                    case "user":
                        b.Append("<div class='user'>");
                        if (m.Image != null) b.Append("<img src='data:image/jpeg;base64," + m.Image + "'>");
                        b.Append(E(string.IsNullOrWhiteSpace(m.Text) ? "(nothing heard)" : m.Text).Replace("\n", "<br>") + "</div>");
                        break;
                    case "assistant":
                        b.Append("<div class='bot'>" + (m.Status ? "<div class='lbl'>RESULT</div>" : "") + Html(m.Text) + "</div>");
                        break;
                    case "tool":
                        b.Append("<div class='tool'>" + (m.Ok ? "✓ " : "✗ ") + E(m.Text) + (m.Ms > 0 ? " · " + m.Ms + " ms" : "") +
                            (string.IsNullOrEmpty(m.Request) ? "" : "<pre>" + E(Short(m.Request, 3000)) + "</pre>") +
                            (string.IsNullOrEmpty(m.Response) ? "" : "<pre>" + E(Short(m.Response, 6000)) + "</pre>") + "</div>");
                        break;
                    case "audio":
                        b.Append("<div class='audio'><div class='lbl'>📎 " + E(m.Text) + " · TRANSCRIPT</div>" + E(m.Response ?? "").Replace("\n", "<br>") + "</div>");
                        break;
                    default:
                        b.Append("<div class='err'>" + E(m.Text) + "</div>");
                        break;
                }
            }
            return Page(b.ToString());
        }
    }
}
