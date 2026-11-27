// Prosper Focus: timeboxed blocks from the plan in Notion, check-ins, one extension, breaks and meeting mode.
// The plan is made on the phone (or in Notion); this PC only reads it and writes back what happened (sessions).
// Everything here is plain logic with an injected clock, so it can be tested and survives restarts: time left is
// always worked out from stored instants (ends at − now), never from a countdown kept in memory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Prosper
{
    /// <summary>Block types, as the "Type" select in Notion.</summary>
    static class FocusType
    {
        public const string Focus = "Focus", Learning = "Learning", Work = "Work", Break = "Break", MeTime = "Me-time", Travel = "Travel", Free = "Free", Other = "Other";
        /// <summary>Work-like blocks: they get check-ins and the time's up question.</summary>
        public static bool IsWork(string t) { return t == Focus || t == Learning || t == Work || t == Other || string.IsNullOrEmpty(t); }
        /// <summary>What this PC may show for a block: only its kind, never its name (colleagues can see the screen).</summary>
        public static string Label(string t) { return t == Break ? "Break" : t == MeTime ? "Me-time" : t == Travel ? "Travel" : "Focus"; }
    }

    /// <summary>Session states, as the "State" select in Notion. Done, Parked, Skipped and Ended early are final.</summary>
    static class FocusState
    {
        public const string Running = "Running", Done = "Done", Parked = "Parked", Skipped = "Skipped", EndedEarly = "Ended early";
        public static bool Final(string s) { return s == Done || s == Parked || s == Skipped || s == EndedEarly; }
    }

    class FocusRoutine
    {
        public string Id { get; set; }
        public string Name { get; set; }     // kept only to order routines; never shown on this PC
        public string Days { get; set; }     // "Mon,Tue,Wed"
        public bool On(DayOfWeek d) { return ("," + (Days ?? "") + ",").Contains("," + d.ToString().Substring(0, 3) + ","); }
    }

    /// <summary>A planned block. Its title and goal stay on the phone: this PC never reads them.</summary>
    class FocusBlock
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public int Start { get; set; }       // minutes after midnight, local time
        public int End { get; set; }
        public int Checkin { get; set; }     // minutes; 0 = off; -1 = the shared setting
        public string Routine { get; set; }  // routine page id, or null for a one-off block
        public string Date { get; set; }     // yyyy-MM-dd for a one-off block on that date only
        public int Minutes { get { return End - Start; } }
    }

    class FocusOverride
    {
        public string Id { get; set; }
        public string Date { get; set; }
        public string Routine { get; set; }
    }

    /// <summary>One block (or break) as it actually happened. Instants are Unix milliseconds, UTC.</summary>
    class FocusSession
    {
        public string PageId { get; set; }
        public string Key { get; set; }        // yyyy-MM-dd|blockId|HH:mm, or yyyy-MM-dd|auto-break|HH:mm
        public string Block { get; set; }      // block page id; null for an automatic break
        public string Type { get; set; }
        public string Date { get; set; }
        public long PlannedStart { get; set; }
        public long PlannedEnd { get; set; }
        public long ActualStart { get; set; }
        public long ActualEnd { get; set; }    // 0 while running
        public long EndsAt { get; set; }       // the live end both devices count down to
        public string State { get; set; }
        public int Extensions { get; set; }
        public string Checkins { get; set; }   // "14:45 on-track | 15:15 stuck"
        public string SkipReason { get; set; }
        public int MeetingMin { get; set; }
        public string Device { get; set; }
        public int Rev { get; set; }
        public bool Dirty { get; set; }        // changed here, not yet in Notion
        public long Created { get; set; }      // Notion created_time, for choosing between duplicates

        public int CheckinCount { get { return string.IsNullOrEmpty(Checkins) ? 0 : Checkins.Split('|').Length; } }
    }

    /// <summary>The one shared configuration, kept in the "⚙ Focus settings" row so the phone and every laptop agree.</summary>
    class FocusConfig
    {
        public int checkinMin { get; set; }
        public int wrapUpMin { get; set; }
        public int warnSec { get; set; }
        public int breakMin { get; set; }
        public int extendMin { get; set; }
        public int autoBreakAfterMin { get; set; }
        public int meetingAutoOffMin { get; set; }
        public int snoozeMin { get; set; }
        public int startGraceMin { get; set; }
        public int meTimeTargetMin { get; set; }
        public string meetingUntil { get; set; } // ISO instant while meeting mode is on, else null
        public int rev { get; set; }

        public FocusConfig()
        {
            checkinMin = 30; wrapUpMin = 10; warnSec = 10; breakMin = 15; extendMin = 15; autoBreakAfterMin = 90;
            meetingAutoOffMin = 120; snoozeMin = 5; startGraceMin = 2; meTimeTargetMin = 90;
        }

        public long MeetingUntilMs { get { return Iso.Parse(meetingUntil); } set { meetingUntil = value > 0 ? Iso.Write(value) : null; } }

        public FocusConfig Copy() { return Json.Read<FocusConfig>(Json.Write(this)); }
    }

    /// <summary>ISO 8601 instants, as Notion writes them, to and from Unix milliseconds.</summary>
    static class Iso
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static long Ms(DateTime t) { return (long)(t.ToUniversalTime() - Epoch).TotalMilliseconds; }
        public static DateTime Local(long ms) { return Epoch.AddMilliseconds(ms).ToLocalTime(); }
        public static string Write(long ms) { return Epoch.AddMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture); }
        public static long Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            DateTimeOffset d;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out d)) return d.ToUnixTimeMilliseconds();
            return 0;
        }
        public static string Day(DateTime local) { return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        /// <summary>"14:15" → 855 minutes; -1 when it isn't a time.</summary>
        public static int Minutes(string hhmm)
        {
            if (string.IsNullOrEmpty(hhmm)) return -1;
            var p = hhmm.Trim().Split(':'); int h, m;
            if (p.Length != 2 || !int.TryParse(p[0], out h) || !int.TryParse(p[1], out m) || h < 0 || h > 24 || m < 0 || m > 59) return -1;
            return h * 60 + m;
        }
        public static string Clock(int minutes) { return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00"); }
    }

    /// <summary>Everything Focus knows: the plan, today's sessions and the shared settings. Cached on disk, sealed.</summary>
    class FocusData
    {
        public List<FocusRoutine> Routines { get; set; }
        public List<FocusBlock> Blocks { get; set; }
        public List<FocusOverride> Overrides { get; set; }
        public List<FocusSession> Sessions { get; set; }
        public FocusConfig Config { get; set; }
        public bool ConfigDirty { get; set; }
        public string SettingsPage { get; set; }
        public long Synced { get; set; }       // last good pull, Unix ms

        public FocusData()
        {
            Routines = new List<FocusRoutine>(); Blocks = new List<FocusBlock>(); Overrides = new List<FocusOverride>();
            Sessions = new List<FocusSession>(); Config = new FocusConfig();
        }

        public FocusSession Session(string key) { return Sessions.FirstOrDefault(s => s.Key == key); }

        static string CacheFile { get { return Paths.File("focus.json"); } }

        public static FocusData Load()
        {
            try
            {
                var sealedText = System.IO.File.ReadAllText(CacheFile);
                var d = Json.Read<FocusData>(Secret.OpenText(sealedText));
                if (d != null) return d.Fixed();
            }
            catch { }
            return new FocusData();
        }

        public void Save()
        {
            try { System.IO.File.WriteAllText(CacheFile, Secret.SealText(Json.Write(this))); }
            catch (Exception e) { Log.Write("focus cache not saved: " + e.GetType().Name); }
        }

        FocusData Fixed()
        {
            if (Routines == null) Routines = new List<FocusRoutine>();
            if (Blocks == null) Blocks = new List<FocusBlock>();
            if (Overrides == null) Overrides = new List<FocusOverride>();
            if (Sessions == null) Sessions = new List<FocusSession>();
            if (Config == null) Config = new FocusConfig();
            return this;
        }
    }

    /// <summary>Which blocks make up a given day.</summary>
    static class FocusPlan
    {
        /// <summary>
        /// One-off blocks dated that day win; else a date override names the routine; else the routine whose weekdays
        /// include that day (the first by name if several do); else the day is free. Free blocks are left out: gaps
        /// and Free blocks are both idle time.
        /// </summary>
        public static List<FocusBlock> Day(FocusData d, DateTime localDay)
        {
            string day = Iso.Day(localDay);
            var list = d.Blocks.Where(b => b.Date == day).ToList();
            if (list.Count == 0)
            {
                var ov = d.Overrides.FirstOrDefault(o => o.Date == day && !string.IsNullOrEmpty(o.Routine));
                string routine = ov != null ? ov.Routine : null;
                if (routine == null)
                {
                    var r = d.Routines.Where(x => x.On(localDay.DayOfWeek)).OrderBy(x => x.Name ?? "", StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (r != null) routine = r.Id;
                }
                if (routine != null) list = d.Blocks.Where(b => b.Routine == routine && string.IsNullOrEmpty(b.Date)).ToList();
            }
            return list.Where(b => b.Type != FocusType.Free && b.End > b.Start).OrderBy(b => b.Start).ToList();
        }

        public static string Key(string day, FocusBlock b) { return day + "|" + b.Id + "|" + Iso.Clock(b.Start); }
    }

    enum FocusPhase { Idle, Upcoming, Running, TimeUp, Warning, Break, DayDone }

    /// <summary>What to show right now. It carries no names or goals: only the kind of block and the time.</summary>
    class FocusView
    {
        public FocusPhase Phase;
        public string Label = "";        // Focus, Break, Me-time, Travel
        public long Left;                // ms until the end (Running/Break), or until the warning ends
        public long Total;               // ms of the whole block
        public double Progress;          // 0..1
        public bool WrapUp, Meeting, CanExtend, CheckinDue, Waiting;
        public int Minutes;              // the block's planned length (start card, time's up)
        public string Key;
        public bool IsWork { get { return Label == "Focus"; } }
    }

    /// <summary>
    /// The Focus engine. Tick(now) moves sessions along (auto-start, end of breaks, check-ins nobody answered, meeting
    /// mode turning itself off) and View() says what to show. Every change marks the session dirty and bumps its Rev,
    /// so sync sends it to Notion. Passing choices (a snooze, a warning on screen) live only in memory: after a
    /// restart a snooze is forgotten and a due break simply warns again.
    /// </summary>
    class FocusEngine
    {
        public readonly FocusData Data;
        public Func<long> Now = () => Iso.Ms(DateTime.UtcNow);
        public string Device = "Laptop-" + Environment.MachineName;
        public event Action<string> Changed; // a session or the settings changed here: sync soon

        // Local state, not shared: what is snoozed, which warning is showing, the check-in on screen.
        readonly Dictionary<string, long> snoozed = new Dictionary<string, long>();
        string warnKey; long warnUntil;     // a break warning on screen
        string heldBreak;                   // a break waiting for meeting mode to end
        long checkinShown;                  // when the check-in card appeared (0 = none)
        string dayDoneShown;
        long autoBreakSnooze;

        public FocusEngine(FocusData data) { Data = data; }

        FocusConfig C { get { return Data.Config; } }
        long Min(int m) { return m * 60000L; }
        static string Hhmm(long ms) { return Iso.Local(ms).ToString("HH:mm", CultureInfo.InvariantCulture); }

        public bool MeetingOn { get { return C.MeetingUntilMs > Now(); } }

        long DayStart(DateTime localDay) { return Iso.Ms(localDay.Date); }
        long At(DateTime localDay, int minutes) { return Iso.Ms(localDay.Date.AddMinutes(minutes)); }

        public FocusSession Running()
        {
            return Data.Sessions.Where(s => s.State == FocusState.Running).OrderByDescending(s => s.ActualStart).FirstOrDefault();
        }

        void Touch(FocusSession s) { s.Rev++; s.Dirty = true; s.Device = Device; if (Changed != null) Changed(s.Key); }
        void TouchConfig() { C.rev++; Data.ConfigDirty = true; if (Changed != null) Changed("settings"); }

        /// <summary>The block the plan has now (or a break due), if nothing has been recorded for it yet.</summary>
        FocusBlock Due(long now, out string key)
        {
            key = null;
            var today = Iso.Local(now).Date;
            string day = Iso.Day(today);
            foreach (var b in FocusPlan.Day(Data, today))
            {
                long start = At(today, b.Start), end = At(today, b.End);
                if (now < start || now >= end) continue;
                string k = FocusPlan.Key(day, b);
                var s = Data.Session(k);
                if (s != null) continue; // started, done or skipped already
                long until;
                if (snoozed.TryGetValue(k, out until) && now < until) continue;
                if (b.Type == FocusType.Travel) continue; // the laptop stays quiet while travelling
                key = k; return b;
            }
            return null;
        }

        FocusSession StartSession(string key, FocusBlock b, string type, long now, long endsAt, long plannedStart, long plannedEnd)
        {
            var s = Data.Session(key);
            if (s == null)
            {
                s = new FocusSession { Key = key };
                Data.Sessions.Add(s);
            }
            s.Block = b != null ? b.Id : null; s.Type = type; s.Date = Iso.Day(Iso.Local(now));
            s.PlannedStart = plannedStart; s.PlannedEnd = plannedEnd; s.ActualStart = now; s.ActualEnd = 0; s.EndsAt = endsAt;
            s.State = FocusState.Running;
            Touch(s);
            return s;
        }

        /// <summary>Moves things along; call it every second (and after any change). Safe to call at any rate.</summary>
        public void Tick()
        {
            long now = Now();
            // Meeting mode turns itself off.
            if (C.meetingUntil != null && C.MeetingUntilMs <= now) { C.meetingUntil = null; TouchConfig(); }
            var run = Running();
            if (run != null)
            {
                // The plan changed under a running block: a deleted or moved block ends here.
                if (run.Block != null && PlanChanged(run)) { Finish(run, FocusState.EndedEarly, now); run = null; }
            }
            if (run != null)
            {
                if (run.Type == FocusType.Break)
                {
                    if (now >= run.EndsAt) { Finish(run, FocusState.Done, run.EndsAt); }
                }
                else if (FocusType.IsWork(run.Type))
                {
                    // Time's up nobody answered: after 15 minutes it is recorded as done, and the day goes on.
                    if (now >= run.EndsAt + Min(15)) { AddCheckin(run, "time's up: no answer", run.EndsAt); Finish(run, FocusState.Done, run.EndsAt); }
                    else if (checkinShown > 0 && now - checkinShown >= 60000) { AddCheckin(run, "none", checkinShown); checkinShown = 0; }
                    else if (now < run.EndsAt) AutoBreak(run, now);
                }
                else if (now >= run.EndsAt) Finish(run, FocusState.Done, run.EndsAt); // Me-time ends by itself
            }
            // A break warning that ran out starts the break, unless a meeting holds it.
            if (warnKey != null && !MeetingOn && now >= warnUntil) StartBreak(now);
            if (heldBreak != null && !MeetingOn && warnKey == null) { warnKey = heldBreak; heldBreak = null; warnUntil = now + C.warnSec * 1000L; }

            if (Running() == null && warnKey == null)
            {
                string key; var b = Due(now, out key);
                if (b != null)
                {
                    var today = Iso.Local(now).Date;
                    long start = At(today, b.Start);
                    if (b.Type == FocusType.Break) Warn(key, now);
                    else if (b.Type == FocusType.MeTime) StartSession(key, b, b.Type, now, At(today, b.End), start, At(today, b.End));
                    else
                    {
                        long until; snoozed.TryGetValue(key, out until);
                        long graceFrom = Math.Max(start, until);
                        if (now >= graceFrom + Min(C.startGraceMin)) StartSession(key, b, b.Type, now, At(today, b.End), start, At(today, b.End));
                    }
                }
            }
        }

        bool PlanChanged(FocusSession s)
        {
            var today = Iso.Local(s.PlannedStart).Date;
            var b = FocusPlan.Day(Data, today).FirstOrDefault(x => FocusPlan.Key(Iso.Day(today), x) == s.Key);
            if (b == null) return true;
            return At(today, b.End) != s.PlannedEnd;
        }

        void Finish(FocusSession s, string state, long at)
        {
            s.State = state; s.ActualEnd = Math.Min(Math.Max(at, s.ActualStart), Now()); checkinShown = 0;
            Touch(s);
        }

        void AddCheckin(FocusSession s, string answer, long at)
        {
            string e = Hhmm(at) + " " + answer;
            s.Checkins = string.IsNullOrEmpty(s.Checkins) ? e : s.Checkins + " | " + e;
            Touch(s);
        }

        // ---- breaks ----

        void Warn(string key, long now)
        {
            if (MeetingOn) { if (heldBreak == null) { heldBreak = key; heldSince = now; } return; }
            warnKey = key; warnUntil = now + C.warnSec * 1000L;
        }

        void StartBreak(long now)
        {
            string key = warnKey; warnKey = null;
            var today = Iso.Local(now).Date;
            var b = FocusPlan.Day(Data, today).FirstOrDefault(x => FocusPlan.Key(Iso.Day(today), x) == key);
            long length = b != null ? Min(b.Minutes) : Min(C.breakMin);
            long plannedStart = b != null ? At(today, b.Start) : now, plannedEnd = b != null ? At(today, b.End) : now + length;
            // A planned break that started late still ends when planned, but lasts at least half its length.
            long ends = b != null ? Math.Max(plannedEnd, now + length / 2) : now + length;
            var work = Running();
            if (work != null && work.Type != FocusType.Break) { work.EndsAt += ends - now; Touch(work); } // the block waits during an automatic break
            var s = StartSession(key, b, FocusType.Break, now, ends, plannedStart, plannedEnd);
            if (s.MeetingMin == 0 && heldSince > 0) { s.MeetingMin = (int)((now - heldSince) / 60000); heldSince = 0; }
        }
        long heldSince;

        /// <summary>The 90-minute rule: after that long in work blocks without a break, suggest one.</summary>
        void AutoBreak(FocusSession run, long now)
        {
            if (C.autoBreakAfterMin <= 0 || now < autoBreakSnooze || warnKey != null || heldBreak != null) return;
            if (run.EndsAt - now < Min(10)) return;                  // the block ends soon anyway
            if (WorkedSinceBreak(now) < Min(C.autoBreakAfterMin)) return;
            var today = Iso.Local(now).Date;
            if (FocusPlan.Day(Data, today).Any(b => b.Type == FocusType.Break && At(today, b.Start) > now && At(today, b.Start) - now <= Min(15))) return;
            string key = Iso.Day(today) + "|auto-break|" + Hhmm(now);
            if (Data.Session(key) != null) return;
            Warn(key, now);
        }

        /// <summary>Time spent in work blocks since the last break (or a gap of 10 minutes or more).</summary>
        public long WorkedSinceBreak(long now)
        {
            var today = Iso.Day(Iso.Local(now));
            var list = Data.Sessions.Where(s => s.Date == today && s.ActualStart > 0 && s.ActualStart <= now).OrderByDescending(s => s.ActualStart).ToList();
            long total = 0, cursor = now;
            foreach (var s in list)
            {
                long end = s.ActualEnd > 0 ? s.ActualEnd : now;
                if (cursor - end >= Min(10)) break; // a real gap counts as a break
                if (!FocusType.IsWork(s.Type)) break;
                total += Math.Max(0, Math.Min(end, cursor) - s.ActualStart);
                cursor = s.ActualStart;
            }
            return total;
        }

        // ---- what to show ----

        public FocusView View()
        {
            long now = Now();
            var v = new FocusView { Meeting = MeetingOn };
            if (warnKey != null)
            {
                v.Phase = FocusPhase.Warning; v.Label = "Break"; v.Key = warnKey;
                v.Left = Math.Max(0, warnUntil - now); v.Waiting = MeetingOn;
                return v;
            }
            var run = Running();
            if (heldBreak != null && MeetingOn && (run == null || run.Type != FocusType.Break) && (run == null || now >= run.EndsAt || !FocusType.IsWork(run.Type)))
            {
                // A break is due but a meeting holds it: only the taskbar says so, nothing pops up.
                v.Phase = FocusPhase.Warning; v.Label = "Break"; v.Key = heldBreak; v.Waiting = true;
                return v;
            }
            if (run != null)
            {
                v.Key = run.Key; v.Label = FocusType.Label(run.Type);
                v.Total = Math.Max(1, run.EndsAt - run.ActualStart);
                v.Left = Math.Max(0, run.EndsAt - now);
                v.Progress = Math.Min(1, Math.Max(0, (now - run.ActualStart) / (double)v.Total));
                v.Minutes = (int)Math.Round((run.PlannedEnd - run.PlannedStart) / 60000.0);
                v.CanExtend = run.Extensions < 1;
                if (run.Type == FocusType.Break) { v.Phase = FocusPhase.Break; return v; }
                if (FocusType.IsWork(run.Type) && now >= run.EndsAt) { v.Phase = FocusPhase.TimeUp; return v; }
                v.Phase = FocusPhase.Running;
                v.WrapUp = FocusType.IsWork(run.Type) && v.Left <= Min(C.wrapUpMin);
                v.CheckinDue = FocusType.IsWork(run.Type) && !v.Meeting && CheckinDue(run, now);
                return v;
            }
            string key; var b = Due(now, out key);
            if (b != null && FocusType.IsWork(b.Type))
            {
                v.Phase = FocusPhase.Upcoming; v.Label = "Focus"; v.Key = key; v.Minutes = b.Minutes;
                return v;
            }
            if (DayOver(now)) { v.Phase = FocusPhase.DayDone; return v; }
            v.Phase = FocusPhase.Idle;
            return v;
        }

        bool CheckinDue(FocusSession run, long now)
        {
            int every = CheckinEvery(run);
            if (every <= 0) return false;
            long since = now - run.ActualStart;
            int due = (int)(since / Min(every));
            if (due <= run.CheckinCount) return false;
            if (run.EndsAt - now < Min(2)) return false; // time's up asks anyway
            if (checkinShown == 0) checkinShown = now;
            return true;
        }

        int CheckinEvery(FocusSession run)
        {
            var b = Data.Blocks.FirstOrDefault(x => x.Id == run.Block);
            int every = b != null && b.Checkin >= 0 ? b.Checkin : C.checkinMin;
            if (run.Type == FocusType.Learning && every <= 0) every = C.checkinMin; // learning blocks always check in
            return every;
        }

        /// <summary>The last block has ended and something happened today: "Day done", once.</summary>
        bool DayOver(long now)
        {
            var today = Iso.Local(now).Date;
            string day = Iso.Day(today);
            if (dayDoneShown == day) return false;
            var blocks = FocusPlan.Day(Data, today);
            if (blocks.Count == 0) return false;
            if (now < At(today, blocks.Last().End)) return false;
            if (now > At(today, blocks.Last().End) + Min(60)) return false; // only right after the last block
            return Data.Sessions.Any(s => s.Date == day && s.ActualStart > 0);
        }

        public void DayDoneSeen() { dayDoneShown = Iso.Day(Iso.Local(Now())); }

        // ---- actions (from the taskbar flyout, cards, hotkeys) ----

        public void Start()
        {
            long now = Now(); string key; var b = Due(now, out key);
            if (b == null || Running() != null) return;
            var today = Iso.Local(now).Date;
            StartSession(key, b, b.Type, now, At(today, b.End), At(today, b.Start), At(today, b.End));
        }

        public void Snooze()
        {
            long now = Now(); string key; var b = Due(now, out key);
            if (b != null) snoozed[key] = now + Min(C.snoozeMin);
        }

        /// <summary>−5 / +5 on the running block's end. It never ends before now.</summary>
        public void Adjust(int minutes)
        {
            var run = Running(); if (run == null) return;
            long now = Now();
            run.EndsAt = Math.Max(now + 30000, run.EndsAt + Min(minutes));
            Touch(run);
        }

        /// <summary>✓ in the flyout: the block is over now, so the time's up question comes.</summary>
        public void EndNow()
        {
            var run = Running(); if (run == null) return;
            if (run.Type == FocusType.Break) { Finish(run, FocusState.Done, Now()); return; }
            run.EndsAt = Now(); Touch(run);
        }

        public void ToggleMeeting()
        {
            long now = Now();
            if (MeetingOn) C.meetingUntil = null; else C.MeetingUntilMs = now + Min(C.meetingAutoOffMin);
            if (MeetingOn && warnKey != null) { heldBreak = warnKey; warnKey = null; heldSince = now; }
            TouchConfig();
        }

        public void Checkin(string answer)
        {
            var run = Running(); if (run == null) return;
            AddCheckin(run, answer, checkinShown > 0 ? checkinShown : Now()); checkinShown = 0;
        }

        /// <summary>Time's up: done, parked, or one extension.</summary>
        public bool TimeUp(string answer)
        {
            var run = Running(); if (run == null) return false;
            long now = Now();
            if (answer == "extend")
            {
                if (run.Extensions >= 1) return false; // one per block, on any device
                run.Extensions++; run.EndsAt = Math.Max(now, run.EndsAt) + Min(C.extendMin); Touch(run);
                return true;
            }
            Finish(run, answer == "park" ? FocusState.Parked : FocusState.Done, now);
            NextIsBreakSoon(now);
            return true;
        }

        /// <summary>After a block, a break planned right after it starts with its warning straight away.</summary>
        void NextIsBreakSoon(long now) { Tick(); }

        public void WarnStartNow() { if (warnKey != null) StartBreak(Now()); }

        public void WarnLater()
        {
            if (warnKey == null) return;
            snoozed[warnKey] = Now() + Min(5);
            if (warnKey.Contains("|auto-break|")) autoBreakSnooze = Now() + Min(5);
            warnKey = null;
        }

        /// <summary>Cancel in the warning: the break is skipped and logged.</summary>
        public void WarnCancel()
        {
            if (warnKey == null) return;
            string key = warnKey; warnKey = null; long now = Now();
            var today = Iso.Local(now).Date;
            var b = FocusPlan.Day(Data, today).FirstOrDefault(x => FocusPlan.Key(Iso.Day(today), x) == key);
            var s = StartSession(key, b, FocusType.Break, now, now, b != null ? At(today, b.Start) : now, b != null ? At(today, b.End) : now);
            s.SkipReason = "cancelled in the warning"; Finish(s, FocusState.Skipped, now);
            if (key.Contains("|auto-break|")) autoBreakSnooze = now + Min(C.autoBreakAfterMin / 2);
        }

        /// <summary>Skip on the break screen. A reason is optional; it is kept with the break.</summary>
        public void SkipBreak(string reason)
        {
            var run = Running(); if (run == null || run.Type != FocusType.Break) return;
            run.SkipReason = (reason ?? "").Trim(); Finish(run, FocusState.Skipped, Now());
        }

        /// <summary>The break is over early: "I'm up" or the end of the break screen.</summary>
        public void EndBreak()
        {
            var run = Running(); if (run == null || run.Type != FocusType.Break) return;
            Finish(run, FocusState.Done, Now());
        }

        // ---- sync: merging what Notion has with what this PC has ----

        /// <summary>
        /// Takes the sessions read from Notion. Per session: the higher Rev wins; a final state is never undone by a
        /// running one; the extension count only goes up. Duplicates of one Key (two devices created it at once):
        /// the oldest page is kept, the others are returned to be archived.
        /// </summary>
        public static List<string> Merge(FocusData d, List<FocusSession> remote)
        {
            var archive = new List<string>();
            foreach (var g in remote.GroupBy(r => r.Key))
            {
                var pages = g.OrderBy(r => r.Created).ToList();
                var r0 = pages[0];
                foreach (var dup in pages.Skip(1)) archive.Add(dup.PageId);
                var local = d.Session(g.Key);
                if (local == null) { d.Sessions.Add(r0); continue; }
                if (string.IsNullOrEmpty(local.PageId) || archive.Contains(local.PageId)) local.PageId = r0.PageId;
                bool localFinal = FocusState.Final(local.State), remoteFinal = FocusState.Final(r0.State);
                bool takeRemote = r0.Rev > local.Rev || (r0.Rev == local.Rev && !local.Dirty);
                if (localFinal && !remoteFinal) takeRemote = false;
                if (remoteFinal && !localFinal) takeRemote = true;
                if (takeRemote)
                {
                    int ext = Math.Max(local.Extensions, r0.Extensions);
                    CopyInto(r0, local); local.Extensions = ext; local.PageId = r0.PageId;
                    local.Dirty = ext != r0.Extensions;
                }
                else if (local.Rev < r0.Rev) { local.Rev = r0.Rev + 1; local.Dirty = true; } // ours wins: send it with a higher Rev
                else if (local.Rev == r0.Rev && local.Dirty) { local.Rev = r0.Rev + 1; }
            }
            return archive;
        }

        static void CopyInto(FocusSession from, FocusSession to)
        {
            to.Block = from.Block; to.Type = from.Type; to.Date = from.Date; to.PlannedStart = from.PlannedStart; to.PlannedEnd = from.PlannedEnd;
            to.ActualStart = from.ActualStart; to.ActualEnd = from.ActualEnd; to.EndsAt = from.EndsAt; to.State = from.State;
            to.Checkins = from.Checkins; to.SkipReason = from.SkipReason; to.MeetingMin = from.MeetingMin; to.Device = from.Device;
            to.Rev = from.Rev; to.Created = from.Created;
        }

        /// <summary>The shared settings: the higher rev wins; a tie keeps ours only while it is unsent.</summary>
        public static void MergeConfig(FocusData d, FocusConfig remote)
        {
            if (remote == null) return;
            if (remote.rev > d.Config.rev || (remote.rev == d.Config.rev && !d.ConfigDirty)) { d.Config = remote; d.ConfigDirty = false; }
            else if (d.ConfigDirty && remote.rev >= d.Config.rev) d.Config.rev = remote.rev + 1;
        }

        /// <summary>Drops sessions older than two days that are already in Notion.</summary>
        public void Prune()
        {
            long cutoff = Now() - 2 * 24 * 3600000L;
            Data.Sessions.RemoveAll(s => !s.Dirty && s.PageId != null && s.ActualStart > 0 && s.ActualStart < cutoff);
        }
    }
}
