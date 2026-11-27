// Prosper Focus ⇄ Notion: one database ("Prosper Focus") shared with the phone. This PC reads routines, blocks,
// date overrides and the shared settings, and writes only sessions (what happened) and the settings row. It never
// edits the plan, and it never reads block names or goals: those stay on the phone.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Prosper
{
    static class Notion
    {
        public const string DbTitle = "Prosper Focus", SettingsName = "⚙ Focus settings";
        const string Base = "https://api.notion.com/v1/", Version = "2022-06-28";
        static readonly HttpMethod Patch = new HttpMethod("PATCH"); // not built in on .NET Framework
        static readonly HttpClient Http;

        static Notion()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var proxy = WebRequest.GetSystemWebProxy();
            proxy.Credentials = CredentialCache.DefaultCredentials;
            Http = new HttpClient(new HttpClientHandler { Proxy = proxy, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(30) };
        }

        static Settings S { get { return Settings.Current; } }

        /// <summary>One call to Notion, waiting and trying again when it says to slow down (429) or is briefly down.</summary>
        static Dictionary<string, object> Call(HttpMethod method, string path, object body)
        {
            string token = S.NotionToken;
            if (token.Length == 0) throw new Exception("Add your Notion integration token in Settings → Focus.");
            for (int attempt = 0; ; attempt++)
            {
                var req = new HttpRequestMessage(method, Base + path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("Notion-Version", Version);
                if (body != null) req.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
                var sw = Stopwatch.StartNew();
                using (var resp = Http.SendAsync(req).Result)
                {
                    string raw = resp.Content.ReadAsStringAsync().Result;
                    int code = (int)resp.StatusCode;
                    Log.Write("Notion " + method.Method + " HTTP " + code + " " + sw.ElapsedMilliseconds + " ms");
                    if (resp.IsSuccessStatusCode) return Json.Obj(raw) ?? new Dictionary<string, object>();
                    if ((code == 429 || code == 409 || code >= 500) && attempt < 3)
                    {
                        int wait = resp.Headers.RetryAfter != null && resp.Headers.RetryAfter.Delta.HasValue ? (int)resp.Headers.RetryAfter.Delta.Value.TotalMilliseconds : 1000 * (attempt + 1);
                        Thread.Sleep(Math.Min(wait, 10000));
                        continue;
                    }
                    string msg = Json.S_(Json.Obj(raw), "message");
                    if (code == 401) throw new Exception("Notion rejected the token (401). Paste it again in Settings → Focus.");
                    if (code == 404) throw new Exception("Notion can't see that page or database (404). Share it with your integration: ••• → Connections.");
                    throw new Exception("Notion error " + code + (msg.Length > 0 ? ": " + (msg.Length > 200 ? msg.Substring(0, 200) : msg) : ""));
                }
            }
        }

        /// <summary>The 32-character id inside a Notion link or id, with or without dashes; "" when there is none.</summary>
        public static string IdOf(string linkOrId)
        {
            if (string.IsNullOrEmpty(linkOrId)) return "";
            string s = linkOrId.Trim();
            int q = s.IndexOf('?'); if (q >= 0) s = s.Substring(0, q);
            var m = Regex.Matches(s.Replace("-", ""), "[0-9a-fA-F]{32}");
            return m.Count > 0 ? m[m.Count - 1].Value.ToLowerInvariant() : "";
        }

        public static string Test()
        {
            var me = Call(HttpMethod.Get, "users/me", null);
            string name = Json.S_(me, "name");
            return "Connected" + (name.Length > 0 ? " as " + name : "") + ".";
        }

        /// <summary>The Prosper Focus database the integration can see (the phone may have made it already).</summary>
        public static string FindDatabase()
        {
            var r = Call(HttpMethod.Post, "search", Json.O("query", DbTitle, "filter", Json.O("property", "object", "value", "database")));
            foreach (var db in Json.A(r, "results"))
            {
                if (Json.S_(db, "archived") == "true" || Json.S_(db, "in_trash") == "true") continue;
                if (Plain(Json.A(db, "title")) == DbTitle) return IdOf(Json.S_(db, "id"));
            }
            return "";
        }

        /// <summary>Makes the database inside a page the integration can edit, with every property the phone uses.</summary>
        public static string CreateDatabase(string parentPage)
        {
            string page = IdOf(parentPage);
            if (page.Length == 0) throw new Exception("Paste the link of a Notion page to put the database in.");
            var props = new Dictionary<string, object>();
            props["Name"] = Json.O("title", Json.O());
            foreach (var kv in Schema()) if (kv.Key != "Routine" && kv.Key != "Block") props[kv.Key] = kv.Value;
            var db = Call(HttpMethod.Post, "databases", Json.O(
                "parent", Json.O("type", "page_id", "page_id", page),
                "title", new object[] { Json.O("type", "text", "text", Json.O("content", DbTitle)) },
                "properties", props));
            string id = IdOf(Json.S_(db, "id"));
            EnsureSchema(id); // the two relations point at the database itself, so they come once it exists
            return id;
        }

        static Dictionary<string, object> Select(params string[] names)
        {
            return Json.O("options", names.Select(n => (object)Json.O("name", n)).ToArray());
        }

        /// <summary>The shared schema. The phone and this PC must agree on every name here.</summary>
        static Dictionary<string, object> Schema(string db = null)
        {
            var text = Json.O("rich_text", Json.O()); var number = Json.O("number", Json.O("format", "number")); var date = Json.O("date", Json.O());
            var p = new Dictionary<string, object>();
            p["Kind"] = Json.O("select", Select("Routine", "Block", "Override", "Session", "Parking", "Settings"));
            p["Type"] = Json.O("select", Select(FocusType.Focus, FocusType.Learning, FocusType.Work, FocusType.Break, FocusType.MeTime, FocusType.Travel, FocusType.Free, FocusType.Other));
            p["Days"] = Json.O("multi_select", Select("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"));
            p["Start"] = text; p["End"] = text; p["Date"] = date; p["Goal"] = text; p["Check-in"] = number;
            p["Key"] = text; p["Planned"] = date; p["Actual"] = date; p["Ends at"] = date;
            p["State"] = Json.O("select", Select("Upcoming", FocusState.Running, FocusState.Done, FocusState.Parked, FocusState.Skipped, FocusState.EndedEarly));
            p["Extensions"] = number; p["Check-ins"] = text; p["Skip reason"] = text; p["Meeting min"] = number;
            p["Device"] = text; p["Rev"] = number; p["Data"] = text;
            if (db != null)
            {
                var rel = Json.O("relation", Json.O("database_id", db, "type", "single_property", "single_property", Json.O()));
                p["Routine"] = rel; p["Block"] = rel;
            }
            return p;
        }

        /// <summary>Adds any property the database is missing (an older or hand-made one); never removes or renames.</summary>
        public static void EnsureSchema(string db)
        {
            var have = Json.D(Call(HttpMethod.Get, "databases/" + db, null), "properties") ?? new Dictionary<string, object>();
            var add = new Dictionary<string, object>();
            foreach (var kv in Schema(db)) if (!have.ContainsKey(kv.Key)) add[kv.Key] = kv.Value;
            if (add.Count > 0) Call(Patch, "databases/" + db, Json.O("properties", add));
        }

        static List<object> QueryAll(string db, object filter)
        {
            var all = new List<object>(); string cursor = null;
            do
            {
                var body = Json.O("filter", filter, "page_size", 100, "start_cursor", cursor);
                var r = Call(HttpMethod.Post, "databases/" + db + "/query", body);
                all.AddRange(Json.A(r, "results"));
                cursor = Json.S_(r, "has_more") == "true" ? Json.S_(r, "next_cursor") : null;
            } while (!string.IsNullOrEmpty(cursor));
            return all;
        }

        static object KindIs(string k) { return Json.O("property", "Kind", "select", Json.O("equals", k)); }

        /// <summary>
        /// Everything this PC needs, in one paged query: the plan rows, the settings row and the sessions from
        /// yesterday on. Returns a fresh FocusData (plan and settings) plus the sessions to merge.
        /// </summary>
        public static FocusData Pull(string db, DateTime localToday, out List<FocusSession> sessions, out FocusConfig config, out string settingsPage)
        {
            var filter = Json.O("or", new object[] {
                KindIs("Routine"), KindIs("Block"), KindIs("Override"), KindIs("Settings"),
                Json.O("and", new object[] { KindIs("Session"), Json.O("property", "Date", "date", Json.O("on_or_after", Iso.Day(localToday.AddDays(-1)))) }),
            });
            var d = new FocusData(); sessions = new List<FocusSession>(); config = null; settingsPage = null;
            long settingsCreated = long.MaxValue;
            foreach (var page in QueryAll(db, filter))
            {
                var p = Json.D(page, "properties");
                if (p == null) continue;
                string id = IdOf(Json.S_(page, "id"));
                switch (Sel(p, "Kind"))
                {
                    case "Routine": d.Routines.Add(new FocusRoutine { Id = id, Name = Title(p), Days = string.Join(",", Multi(p, "Days")) }); break;
                    case "Block":
                        var b = FocusMap.Block(id, p);
                        if (b != null) d.Blocks.Add(b);
                        break;
                    case "Override": d.Overrides.Add(new FocusOverride { Id = id, Date = DateOnly(p, "Date"), Routine = Rel(p, "Routine") }); break;
                    case "Settings":
                        long created = Iso.Parse(Json.S_(page, "created_time"));
                        if (created < settingsCreated)
                        {
                            settingsCreated = created; settingsPage = id;
                            try { config = Json.Read<FocusConfig>(Text(p, "Data")); } catch { config = null; }
                        }
                        break;
                    case "Session":
                        var s = FocusMap.Session(id, p);
                        s.Created = Iso.Parse(Json.S_(page, "created_time"));
                        if (!string.IsNullOrEmpty(s.Key)) sessions.Add(s);
                        break;
                }
            }
            return d;
        }

        /// <summary>Fresh copy of one session, to check the extension count right before extending.</summary>
        public static FocusSession ReadSession(string pageId)
        {
            var page = Call(HttpMethod.Get, "pages/" + pageId, null);
            return FocusMap.Session(pageId, Json.D(page, "properties"));
        }

        /// <summary>Writes one session: finds its page by Key first, so two devices don't make two rows for one block.</summary>
        public static string PushSession(string db, FocusSession s)
        {
            string page = s.PageId;
            if (string.IsNullOrEmpty(page))
            {
                var found = QueryAll(db, Json.O("and", new object[] { KindIs("Session"), Json.O("property", "Key", "rich_text", Json.O("equals", s.Key)) }));
                var oldest = found.OrderBy(f => Iso.Parse(Json.S_(f, "created_time"))).FirstOrDefault();
                if (oldest != null) page = IdOf(Json.S_(oldest, "id"));
            }
            var props = FocusMap.SessionProps(s);
            if (string.IsNullOrEmpty(page))
                page = IdOf(Json.S_(Call(HttpMethod.Post, "pages", Json.O("parent", Json.O("database_id", db), "properties", props)), "id"));
            else Call(Patch, "pages/" + page, Json.O("properties", props));
            return page;
        }

        public static string PushConfig(string db, string page, FocusConfig c)
        {
            var props = Json.O("Name", FocusMap.TitleProp(SettingsName), "Kind", Json.O("select", Json.O("name", "Settings")), "Data", FocusMap.TextProp(Json.Write(c)));
            if (string.IsNullOrEmpty(page))
                return IdOf(Json.S_(Call(HttpMethod.Post, "pages", Json.O("parent", Json.O("database_id", db), "properties", props)), "id"));
            Call(Patch, "pages/" + page, Json.O("properties", props));
            return page;
        }

        public static void Archive(string page) { Call(Patch, "pages/" + page, Json.O("archived", true)); }

        // ---- reading properties ----

        public static string Plain(object[] richText) { return string.Concat(richText.Select(t => Json.S_(t, "plain_text"))); }
        public static string Title(Dictionary<string, object> p) { return Plain(Json.A(Json.D(p, "Name"), "title")); }
        public static string Text(Dictionary<string, object> p, string k) { return Plain(Json.A(Json.D(p, k), "rich_text")); }
        public static string Sel(Dictionary<string, object> p, string k) { return Json.S_(Json.D(Json.D(p, k), "select"), "name"); }
        public static List<string> Multi(Dictionary<string, object> p, string k) { return Json.A(Json.D(p, k), "multi_select").Select(o => Json.S_(o, "name")).ToList(); }
        public static string Rel(Dictionary<string, object> p, string k)
        {
            var a = Json.A(Json.D(p, k), "relation");
            return a.Length > 0 ? IdOf(Json.S_(a[0], "id")) : null;
        }
        public static int Num(Dictionary<string, object> p, string k, int missing)
        {
            string s = Json.S_(Json.D(p, k), "number"); double v;
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? (int)Math.Round(v) : missing;
        }
        public static long DateStart(Dictionary<string, object> p, string k) { return Iso.Parse(Json.S_(Json.D(Json.D(p, k), "date"), "start")); }
        public static long DateEnd(Dictionary<string, object> p, string k) { return Iso.Parse(Json.S_(Json.D(Json.D(p, k), "date"), "end")); }
        public static string DateOnly(Dictionary<string, object> p, string k)
        {
            string s = Json.S_(Json.D(Json.D(p, k), "date"), "start");
            return s.Length >= 10 ? s.Substring(0, 10) : null;
        }
    }

    /// <summary>Notion rows ⇄ Focus objects (kept apart from HTTP so they can be tested offline).</summary>
    static class FocusMap
    {
        public static FocusBlock Block(string id, Dictionary<string, object> p)
        {
            int start = Iso.Minutes(Notion.Text(p, "Start")), end = Iso.Minutes(Notion.Text(p, "End"));
            if (start < 0 || end <= start) return null; // a block without proper times isn't on the clock
            string type = Notion.Sel(p, "Type");
            return new FocusBlock
            {
                Id = id, Type = type.Length > 0 ? type : FocusType.Focus, Start = start, End = end,
                Checkin = Notion.Num(p, "Check-in", -1), Routine = Notion.Rel(p, "Routine"), Date = Notion.DateOnly(p, "Date"),
            };
        }

        public static FocusSession Session(string id, Dictionary<string, object> p)
        {
            if (p == null) return null;
            string state = Notion.Sel(p, "State");
            return new FocusSession
            {
                PageId = id, Key = Notion.Text(p, "Key"), Block = Notion.Rel(p, "Block"), Type = Notion.Sel(p, "Type"),
                Date = Notion.DateOnly(p, "Date"), PlannedStart = Notion.DateStart(p, "Planned"), PlannedEnd = Notion.DateEnd(p, "Planned"),
                ActualStart = Notion.DateStart(p, "Actual"), ActualEnd = Notion.DateEnd(p, "Actual"), EndsAt = Notion.DateStart(p, "Ends at"),
                State = state.Length > 0 ? state : FocusState.Running, Extensions = Notion.Num(p, "Extensions", 0),
                Checkins = Notion.Text(p, "Check-ins"), SkipReason = Notion.Text(p, "Skip reason"), MeetingMin = Notion.Num(p, "Meeting min", 0),
                Device = Notion.Text(p, "Device"), Rev = Notion.Num(p, "Rev", 0),
            };
        }

        public static object TitleProp(string s) { return Json.O("title", Rich(s)); }
        public static object TextProp(string s) { return Json.O("rich_text", Rich(s)); }

        /// <summary>Notion allows 2000 characters per text piece: longer text goes in several pieces.</summary>
        static object[] Rich(string s)
        {
            var parts = new List<object>();
            s = s ?? "";
            for (int i = 0; i < s.Length; i += 2000) parts.Add(Json.O("type", "text", "text", Json.O("content", s.Substring(i, Math.Min(2000, s.Length - i)))));
            return parts.ToArray();
        }

        static object Range(long start, long end)
        {
            if (start <= 0) return Null("date"); // Json.O drops nulls; Notion needs "date": null to clear it
            var d = new Dictionary<string, object>(); d["start"] = Iso.Write(start);
            if (end > 0) d["end"] = Iso.Write(end);
            return Json.O("date", d);
        }

        static object Sel(string v) { return string.IsNullOrEmpty(v) ? Null("select") : Json.O("select", Json.O("name", v)); }

        static object Null(string k) { var d = new Dictionary<string, object>(); d[k] = null; return d; }

        /// <summary>A session's properties. Its title is only the kind and the time ("Focus 14:15"), never a task name.</summary>
        public static Dictionary<string, object> SessionProps(FocusSession s)
        {
            var p = new Dictionary<string, object>();
            p["Name"] = TitleProp(FocusType.Label(s.Type) + " " + Iso.Local(s.PlannedStart > 0 ? s.PlannedStart : s.ActualStart).ToString("HH:mm"));
            p["Kind"] = Sel("Session");
            p["Type"] = Sel(s.Type);
            p["Key"] = TextProp(s.Key);
            if (!string.IsNullOrEmpty(s.Date)) p["Date"] = Json.O("date", Json.O("start", s.Date));
            p["Block"] = Json.O("relation", string.IsNullOrEmpty(s.Block) ? new object[0] : new object[] { Json.O("id", s.Block) });
            p["Planned"] = Range(s.PlannedStart, s.PlannedEnd);
            p["Actual"] = Range(s.ActualStart, s.ActualEnd);
            p["Ends at"] = Range(s.EndsAt, 0);
            p["State"] = Sel(s.State);
            p["Extensions"] = Json.O("number", s.Extensions);
            p["Check-ins"] = TextProp(s.Checkins);
            p["Skip reason"] = TextProp(s.SkipReason);
            p["Meeting min"] = Json.O("number", s.MeetingMin);
            p["Device"] = TextProp(s.Device);
            p["Rev"] = Json.O("number", s.Rev);
            return p;
        }
    }
}
