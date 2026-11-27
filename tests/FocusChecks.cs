// Self-test for Prosper Focus: which blocks make up a day, and the engine at chosen moments (a fake clock), plus
// merging what Notion has with what this PC has. Nothing here touches Notion or the user's own Focus cache.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Prosper
{
    static partial class Tests
    {
        // Monday 30 November 2026 and the days after it.
        static readonly DateTime Mon = new DateTime(2026, 11, 30);

        static FocusData Plan()
        {
            var d = new FocusData();
            d.Routines.Add(new FocusRoutine { Id = "office", Name = "Office day", Days = "Mon,Tue,Wed" });
            d.Routines.Add(new FocusRoutine { Id = "home", Name = "Home day", Days = "Thu,Fri" });
            d.Routines.Add(new FocusRoutine { Id = "travel", Name = "Travel day", Days = "" });
            d.Blocks.Add(new FocusBlock { Id = "b1", Type = FocusType.Learning, Start = 600, End = 660, Checkin = -1, Routine = "office" });
            d.Blocks.Add(new FocusBlock { Id = "b2", Type = FocusType.Break, Start = 660, End = 675, Checkin = 0, Routine = "office" });
            d.Blocks.Add(new FocusBlock { Id = "b3", Type = FocusType.Work, Start = 675, End = 780, Checkin = -1, Routine = "office" });
            d.Blocks.Add(new FocusBlock { Id = "free", Type = FocusType.Free, Start = 780, End = 840, Routine = "office" });
            d.Blocks.Add(new FocusBlock { Id = "h1", Type = FocusType.Work, Start = 540, End = 720, Checkin = 0, Routine = "home" });
            d.Blocks.Add(new FocusBlock { Id = "t1", Type = FocusType.Travel, Start = 330, End = 510, Routine = "travel" });
            d.Overrides.Add(new FocusOverride { Id = "o1", Date = "2026-12-02", Routine = "home" });     // Wednesday from home
            d.Overrides.Add(new FocusOverride { Id = "o2", Date = "2026-12-01", Routine = "travel" });   // Tuesday travelling
            d.Blocks.Add(new FocusBlock { Id = "x", Type = FocusType.Focus, Start = 840, End = 900, Checkin = -1, Date = "2026-12-03" }); // Thursday one-off
            return d;
        }

        static long At(DateTime day, int h, int m, int s = 0) { return Iso.Ms(day.Date.AddHours(h).AddMinutes(m).AddSeconds(s)); }

        static FocusEngine Engine(FocusData d, Func<long> clock) { return new FocusEngine(d) { Now = clock, Device = "Laptop-test" }; }

        static string Ids(List<FocusBlock> l) { return string.Join(",", l.Select(b => b.Id)); }

        static void FocusChecks()
        {
            // ---- which blocks make up a day ----
            var d = Plan();
            Check("focus: routine by weekday (Free left out)", Ids(FocusPlan.Day(d, Mon)) == "b1,b2,b3", Ids(FocusPlan.Day(d, Mon)));
            Check("focus: date override picks another routine", Ids(FocusPlan.Day(d, Mon.AddDays(2))) == "h1", Ids(FocusPlan.Day(d, Mon.AddDays(2))));
            Check("focus: override to a routine with no weekdays", Ids(FocusPlan.Day(d, Mon.AddDays(1))) == "t1");
            Check("focus: one-off blocks replace the day", Ids(FocusPlan.Day(d, Mon.AddDays(3))) == "x");
            Check("focus: a day with no routine is free", FocusPlan.Day(d, Mon.AddDays(5)).Count == 0);
            Check("focus: times", Iso.Minutes("14:15") == 855 && Iso.Minutes("9:05") == 545 && Iso.Minutes("25:00") == -1 && Iso.Minutes("") == -1);
            long instant = At(Mon, 10, 2, 30);
            Check("focus: ISO instants round trip", Iso.Parse(Iso.Write(instant)) == instant);
            Check("focus: Notion id from a link", Notion.IdOf("https://www.notion.so/me/Prosper-Focus-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d?v=99") == "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");

            // ---- a learning block: start card, auto-start, check-ins, wrap up, time's up, one extension ----
            long now = 0;
            var e = Engine(d, () => now);
            now = At(Mon, 9, 59); e.Tick();
            Check("focus: idle before the first block", e.View().Phase == FocusPhase.Idle);
            now = At(Mon, 10, 0, 30); e.Tick();
            var v = e.View();
            Check("focus: start card at the planned start", v.Phase == FocusPhase.Upcoming && v.Minutes == 60 && v.Label == "Focus");
            now = At(Mon, 10, 2, 30); e.Tick();
            var run = e.Running();
            Check("focus: starts by itself after the grace period", run != null && run.ActualStart == now && run.EndsAt == At(Mon, 11, 0) && run.Key == "2026-11-30|b1|10:00");
            Check("focus: the new session is waiting to be sent", run != null && run.Dirty && run.Rev == 1 && run.Device == "Laptop-test");
            now = At(Mon, 10, 20); e.Tick();
            v = e.View();
            Check("focus: running shows only the kind and the time", v.Phase == FocusPhase.Running && v.Label == "Focus" && v.Left == 40 * 60000 && !v.CheckinDue && !v.WrapUp);
            now = At(Mon, 10, 33); e.Tick();
            Check("focus: check-in after 30 minutes", e.View().CheckinDue);
            e.Checkin("on-track");
            Check("focus: check-in answer recorded", run.Checkins == "10:33 on-track" && !e.View().CheckinDue, run.Checkins);
            now = At(Mon, 10, 51); e.Tick();
            Check("focus: wrap up in the last 10 minutes", e.View().WrapUp);
            e.Adjust(5);
            Check("focus: +5 moves the end", run.EndsAt == At(Mon, 11, 5));
            e.Adjust(-5);
            now = At(Mon, 11, 0, 1); e.Tick();
            v = e.View();
            Check("focus: time's up", v.Phase == FocusPhase.TimeUp && v.CanExtend);
            Check("focus: one extension", e.TimeUp("extend") && run.EndsAt == At(Mon, 11, 15, 1) && run.Extensions == 1);
            now = At(Mon, 11, 15, 2); e.Tick();
            Check("focus: no second extension", e.View().Phase == FocusPhase.TimeUp && !e.View().CanExtend && !e.TimeUp("extend"));
            e.TimeUp("done");
            Check("focus: done is recorded", run.State == FocusState.Done && run.ActualEnd == now);

            // ---- snooze, and a check-in nobody answered ----
            d = Plan(); e = Engine(d, () => now);
            now = At(Mon, 10, 0, 30); e.Tick(); e.Snooze();
            Check("focus: snooze hides the start card", e.View().Phase == FocusPhase.Idle);
            now = At(Mon, 10, 5, 31); e.Tick();
            Check("focus: the card comes back after the snooze", e.View().Phase == FocusPhase.Upcoming);
            e.Start(); run = e.Running();
            now = At(Mon, 10, 36); e.Tick(); e.View();
            now = At(Mon, 10, 37, 1); e.Tick();
            Check("focus: an ignored check-in is recorded as none", run.Checkins == "10:36 none" && !e.View().CheckinDue, run.Checkins);

            // ---- a planned break: 10-second warning, then the break ----
            d = Plan(); e = Engine(d, () => now);
            d.Sessions.Add(new FocusSession { Key = "2026-11-30|b1|10:00", Block = "b1", Type = FocusType.Learning, Date = "2026-11-30", State = FocusState.Done, ActualStart = At(Mon, 10, 0), ActualEnd = At(Mon, 11, 0) });
            now = At(Mon, 11, 0); e.Tick();
            v = e.View();
            Check("focus: break warning first", v.Phase == FocusPhase.Warning && v.Left == 10000 && !v.Waiting);
            now = At(Mon, 11, 0, 10); e.Tick();
            v = e.View(); run = e.Running();
            Check("focus: then the break", v.Phase == FocusPhase.Break && run != null && run.Type == FocusType.Break && run.EndsAt == At(Mon, 11, 15));
            e.SkipBreak("client call");
            Check("focus: skipping keeps the reason", run.State == FocusState.Skipped && run.SkipReason == "client call");

            // ---- cancel in the warning ----
            d = Plan(); e = Engine(d, () => now);
            d.Sessions.Add(new FocusSession { Key = "2026-11-30|b1|10:00", Block = "b1", Type = FocusType.Learning, Date = "2026-11-30", State = FocusState.Done, ActualStart = At(Mon, 10, 0), ActualEnd = At(Mon, 11, 0) });
            now = At(Mon, 11, 0); e.Tick(); e.WarnCancel();
            var skipped = d.Session("2026-11-30|b2|11:00");
            Check("focus: cancel skips the break and logs it", skipped != null && skipped.State == FocusState.Skipped && e.View().Phase == FocusPhase.Idle);

            // ---- meeting mode holds the break, then turns itself off ----
            d = Plan(); e = Engine(d, () => now);
            d.Sessions.Add(new FocusSession { Key = "2026-11-30|b1|10:00", Block = "b1", Type = FocusType.Learning, Date = "2026-11-30", State = FocusState.Done, ActualStart = At(Mon, 10, 0), ActualEnd = At(Mon, 11, 0) });
            now = At(Mon, 10, 59); e.ToggleMeeting();
            Check("focus: meeting mode on (shared setting changed)", e.MeetingOn && d.ConfigDirty && d.Config.meetingUntil != null);
            now = At(Mon, 11, 0); e.Tick();
            v = e.View();
            Check("focus: in a meeting the break waits quietly", v.Phase == FocusPhase.Warning && v.Waiting && e.Running() == null);
            now = At(Mon, 11, 20); e.ToggleMeeting(); e.Tick();
            Check("focus: meeting off, the warning comes", e.View().Phase == FocusPhase.Warning && !e.View().Waiting);
            now = At(Mon, 11, 20, 10); e.Tick();
            run = e.Running();
            Check("focus: the late break starts and logs the wait", run != null && run.Type == FocusType.Break && run.MeetingMin == 20 && run.EndsAt == At(Mon, 11, 27, 40), run == null ? "none" : run.MeetingMin + " " + Iso.Local(run.EndsAt));
            now = At(Mon, 12, 0); e.ToggleMeeting();
            now = At(Mon, 14, 0, 1); e.Tick();
            Check("focus: meeting mode turns itself off after 2 hours", !e.MeetingOn && d.Config.meetingUntil == null);

            // ---- the 90-minute rule ----
            d = Plan(); e = Engine(d, () => now);
            var thu = Mon.AddDays(4); // Friday: Home day, h1 is 09:00–12:00 with no planned break
            now = At(thu, 9, 0, 5); e.Tick(); e.Start(); run = e.Running();
            now = At(thu, 10, 29); e.Tick();
            Check("focus: no break before 90 minutes", e.View().Phase == FocusPhase.Running);
            now = At(thu, 10, 31); e.Tick();
            v = e.View();
            Check("focus: a break is suggested after 90 minutes", v.Phase == FocusPhase.Warning && v.Key.Contains("|auto-break|"));
            now = At(thu, 10, 31, 10); e.Tick();
            Check("focus: the block waits during the break", e.Running().Type == FocusType.Break && run.EndsAt == At(thu, 12, 15));

            // ---- the plan changed under a running block ----
            d = Plan(); e = Engine(d, () => now);
            now = At(Mon, 10, 0, 5); e.Tick(); e.Start(); run = e.Running();
            d.Blocks.RemoveAll(b => b.Id == "b1");
            now = At(Mon, 10, 10); e.Tick();
            Check("focus: a deleted block ends early", run.State == FocusState.EndedEarly);

            // ---- merging with Notion ----
            d = new FocusData();
            d.Sessions.Add(new FocusSession { Key = "k1", PageId = "p1", State = FocusState.Running, Rev = 3, Dirty = true, EndsAt = 100 });
            d.Sessions.Add(new FocusSession { Key = "k2", PageId = "p2", State = FocusState.Running, Rev = 4 });
            d.Sessions.Add(new FocusSession { Key = "k3", PageId = "p3", State = FocusState.Done, Rev = 2, Dirty = true });
            d.Sessions.Add(new FocusSession { Key = "k4", PageId = "p4", State = FocusState.Running, Rev = 1, Extensions = 1 });
            var remote = new List<FocusSession>
            {
                new FocusSession { Key = "k1", PageId = "p1", State = FocusState.Running, Rev = 2, EndsAt = 50 },
                new FocusSession { Key = "k2", PageId = "p2", State = FocusState.Done, Rev = 5 },
                new FocusSession { Key = "k3", PageId = "p3", State = FocusState.Running, Rev = 9 },
                new FocusSession { Key = "k4", PageId = "p4", State = FocusState.Running, Rev = 6, Extensions = 0 },
                new FocusSession { Key = "k5", PageId = "p5a", State = FocusState.Running, Rev = 1, Created = 200 },
                new FocusSession { Key = "k5", PageId = "p5b", State = FocusState.Running, Rev = 1, Created = 100 },
            };
            var archive = FocusEngine.Merge(d, remote);
            Check("focus merge: our newer change wins", d.Session("k1").EndsAt == 100 && d.Session("k1").Dirty);
            Check("focus merge: their newer change wins", d.Session("k2").State == FocusState.Done && !d.Session("k2").Dirty);
            Check("focus merge: a final state is never undone", d.Session("k3").State == FocusState.Done && d.Session("k3").Rev > 9 && d.Session("k3").Dirty);
            Check("focus merge: extensions only go up", d.Session("k4").Extensions == 1 && d.Session("k4").Dirty);
            Check("focus merge: duplicates keep the oldest page", d.Session("k5").PageId == "p5b" && archive.Count == 1 && archive[0] == "p5a");
            var cfg = new FocusConfig { rev = 3, breakMin = 20 };
            d.Config = new FocusConfig { rev = 2 }; FocusEngine.MergeConfig(d, cfg);
            Check("focus merge: shared settings follow the higher rev", d.Config.breakMin == 20);

            // ---- Notion rows ----
            var props = new Dictionary<string, object>();
            props["Start"] = Rich("10:00"); props["End"] = Rich("11:30");
            props["Type"] = Json.O("select", Json.O("name", "Learning"));
            props["Routine"] = Json.O("relation", new object[] { Json.O("id", "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d") });
            props["Check-in"] = Json.O("number", 45);
            props["Goal"] = Rich("never read on this PC");
            var blk = FocusMap.Block("b", props);
            Check("focus notion: a block row", blk != null && blk.Start == 600 && blk.End == 690 && blk.Type == "Learning" && blk.Checkin == 45 && blk.Routine == "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");
            props["End"] = Rich("09:00");
            Check("focus notion: a block that ends before it starts is ignored", FocusMap.Block("b", props) == null);
            var s1 = new FocusSession { Key = "2026-11-30|b1|10:00", Block = "b1", Type = FocusType.Learning, Date = "2026-11-30", PlannedStart = At(Mon, 10, 0), PlannedEnd = At(Mon, 11, 0), ActualStart = At(Mon, 10, 2), EndsAt = At(Mon, 11, 0), State = FocusState.Running, Rev = 2 };
            var w = FocusMap.SessionProps(s1);
            string title = Json.S_(Json.D(Json.A(w["Name"], "title")[0], "text"), "content");
            Check("focus notion: a session's title is only the kind and time", title == "Focus 10:00", title);
            Check("focus notion: an open session has no end yet", Json.Write(w["Actual"]).Contains("start") && !Json.Write(w["Actual"]).Contains("end"));
            Check("focus notion: empty values are cleared, not dropped", Json.Write(FocusMap.SessionProps(new FocusSession { Key = "k" })["State"]) == "{\"select\":null}");
        }

        static object Rich(string s) { return Json.O("rich_text", new object[] { Json.O("plain_text", s) }); }
    }
}
