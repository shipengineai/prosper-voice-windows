// Prosper Voice for Windows: the tray icon, global shortcuts and the Prosper bar, and what happens between a
// recording and the text landing in the app: dictation, spoken edits of a selection, and the Assistant.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Prosper
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (var once = new Mutex(true, @"Local\ProsperVoice", out first))
            {
                if (!first) { MessageBox.Show("Prosper Voice is already running: look for its icon in the tray.", "Prosper Voice"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Floating.Dpi = g.DpiX / 96f;
                Application.ThreadException += (s, e) => Log.Write("error: " + e.Exception.GetType().Name);
                Log.Write("start " + Application.ProductVersion);
                App.Preview = Bar.AllowCapture = Array.IndexOf(Environment.GetCommandLineArgs(), "--preview") > 0;
                Application.Run(new App());
            }
        }
    }

    class App : ApplicationContext
    {
        readonly Control ui = new Control();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Bar bar = new Bar();
        readonly AnswerCard card = new AnswerCard();
        readonly Hotkeys keys = new Hotkeys();
        readonly Recorder recorder = new Recorder("prosperrec");
        readonly System.Windows.Forms.Timer autoStop = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 }; // 10 minutes: under OpenAI's 25 MB
        HistoryForm history;
        SettingsForm settings;
        AskBox ask;
        string askConversation, askPrevious; // the typed conversation: its History entry and OpenAI response to continue

        enum Mode { Idle, Recording, Working }
        Mode mode;
        bool assist;
        Target target;
        byte[] screen;
        string lastConversation;

        Settings S { get { return Settings.Current; } }

        public App()
        {
            ui.CreateControl();
            tray.Icon = Logo.Small(SystemInformation.SmallIconSize.Width); tray.Text = "Prosper Voice"; tray.Visible = true;
            tray.ContextMenuStrip = Menu();
            tray.DoubleClick += (s, e) => ShowHistory(null);

            bar.Tap += () => Toggle(false);
            bar.DoubleTap += () => Toggle(true);
            bar.Hold += () => ShowHistory(lastConversation);
            bar.ShowMenu += () => Menu().Show(Cursor.Position);
            bar.Finish += () => { if (mode == Mode.Recording) Stop(); };
            bar.Cancel += CancelRecording;
            bar.ToggleScreen += AddScreen;
            bar.TypeTap += () => TypeToAssistant();
            bar.LevelSource = () => recorder.Level;
            bar.PlaceSaved();
            bar.KeepVisible = S.ShowBubble;

            card.Insert += async () => await Insert(card.Answer);
            card.OpenChat += () => ShowHistory(lastConversation);
            autoStop.Tick += (s, e) => { if (mode == Mode.Recording) Stop(); };

            keys.Pressed += id =>
            {
                if (id == Shortcuts.Dictate) Toggle(false);
                else if (id == Shortcuts.Assist) Toggle(true);
                else if (id == Shortcuts.Type) TypeToAssistant();
                else if (id == Shortcuts.Screen) { if (mode == Mode.Recording && assist) AddScreen(); }
                else if (id == Shortcuts.History) ShowHistory(lastConversation);
            };
            if (Preview) { Demo(); return; } // no shortcuts, no setup: just the bar
            ApplyKeys();

            if (!S.Configured) { Balloon("Add your OpenAI API key to start."); ShowSettings(); }
            else if (!S.ShowBubble) Balloon("Prosper Voice is running. Hover the bar, or use your shortcuts" + Shortcuts.Hint(Shortcuts.Dictate) + ".");
        }

        /// <summary>Registers the shortcuts from settings (again, after they change) and says which another app has.</summary>
        void ApplyKeys()
        {
            var taken = keys.Apply();
            if (taken.Count == 0) return;
            Log.Write("shortcuts taken by another app: " + taken.Count);
            Balloon(string.Join(", ", taken) + (taken.Count == 1 ? " belongs" : " belong") + " to another app. Pick another in Settings → Shortcuts, or use the bar.");
        }

        public static bool Preview;

        /// <summary>
        /// ProsperVoice.exe --preview: plays through every state of the bar with the real microphone level, so the design
        /// and the waveform can be seen without an API key. Nothing is recorded to disk or sent anywhere.
        /// </summary>
        async void Demo()
        {
            bar.KeepVisible = true;
            await Task.Delay(1200);
            if (recorder.Start() != null) Say("No microphone: the waveform stays flat", 2500, true);
            bar.State = BarState.Dictating; await Task.Delay(6000);
            bar.State = BarState.Assisting; await Task.Delay(3500);
            bar.ScreenAdded = true; await Task.Delay(3000);
            int loudest; string clip = recorder.Stop(out loudest);
            if (clip != null) try { File.Delete(clip); } catch { }
            Log.Write("preview: loudest sample " + loudest); // tells whether the mic heard anything
            bar.State = BarState.Working; bar.Step("Transcribing"); await Task.Delay(1500);
            bar.Step("Thinking with your screen"); await Task.Delay(2000);
            Done(); Say("Typed · copied", 2500); await Task.Delay(3500);
            Say("Preview done. Quit from the tray, then start Prosper normally.", 6000);
        }

        ContextMenuStrip Menu()
        {
            var m = Style.NewMenu();
            m.Items.Add("Dictate" + Shortcuts.Hint(Shortcuts.Dictate), null, (s, e) => Toggle(false));
            m.Items.Add("Assistant" + Shortcuts.Hint(Shortcuts.Assist), null, (s, e) => Toggle(true));
            m.Items.Add("Type to the Assistant" + Shortcuts.Hint(Shortcuts.Type), null, (s, e) => TypeToAssistant());
            m.Items.Add("History" + Shortcuts.Hint(Shortcuts.History), null, (s, e) => ShowHistory(lastConversation));
            m.Items.Add("-");
            m.Items.Add(S.ShowBubble ? "Hide the bar when idle" : "Show the bar", null, (s, e) => { S.ShowBubble = !S.ShowBubble; Settings.Save(); ApplyBar(); });
            m.Items.Add("Move the bar back to the bottom", null, (s, e) => { S.BarX = -1; S.BarY = -1; Settings.Save(); bar.PlaceSaved(); });
            m.Items.Add("Settings", null, (s, e) => ShowSettings());
            m.Items.Add("-");
            m.Items.Add("Quit", null, (s, e) => Quit());
            return m;
        }

        void ApplyBar() { tray.ContextMenuStrip = Menu(); bar.KeepVisible = S.ShowBubble; }

        void Say(string message, int ms, bool error = false) { bar.Say(message, ms, error); }

        void Balloon(string message) { tray.ShowBalloonTip(5000, "Prosper Voice", message, ToolTipIcon.Info); }

        // ---- recording ----

        async void Toggle(bool assistant)
        {
            if (mode == Mode.Working) return;
            if (mode == Mode.Recording) { Stop(); return; }
            if (!S.Configured) { ShowSettings(); return; }
            card.Hide();
            if (ask != null && ask.Visible) { ask.Hide(); await Task.Delay(120); } // the app behind it comes back to the front
            assist = assistant; screen = null; mode = Mode.Working; // until the target is read
            target = await Target.Capture();
            string why = recorder.Start();
            if (why != null)
            {
                mode = Mode.Idle;
                Say("Microphone unavailable", 5000, true);
                Balloon("Couldn't start the microphone: " + why + ". Check Windows Settings → Privacy & security → Microphone → Let desktop apps access your microphone.");
                return;
            }
            mode = Mode.Recording;
            bar.State = assist ? BarState.Assisting : BarState.Dictating;
            autoStop.Start();
            Log.Write("recording " + (assist ? "assistant" : target.Selected != null ? "edit" : "dictation"));
        }

        void CancelRecording()
        {
            if (mode != Mode.Recording) return;
            autoStop.Stop();
            recorder.Cancel();
            Done();
            Say("Cancelled", 1200);
        }

        /// <summary>The screen behind the bar, taken when the user asks for it.</summary>
        async void AddScreen()
        {
            if (mode != Mode.Recording || !assist) return;
            if (bar.ScreenAdded) { screen = null; bar.ScreenAdded = false; return; }
            // The bar is kept out of captures where Windows allows it; elsewhere it steps aside for the moment.
            bool hide = !bar.ExcludedFromCapture;
            if (hide) { bar.Hide(); await Task.Delay(150); }
            try { screen = await Task.Run(() => Shot.Take(target.Window)); }
            catch (Exception e) { Log.Write("screenshot failed: " + e.GetType().Name); screen = null; }
            if (hide) bar.Show();
            if (mode == Mode.Recording) bar.ScreenAdded = screen != null;
        }

        async void Stop()
        {
            autoStop.Stop();
            int peak;
            string file = recorder.Stop(out peak);
            mode = Mode.Working;
            bar.State = BarState.Working;
            var t = target; bool a = assist; byte[] image = assist ? screen : null;
            string kind = a ? "assistant" : t.Selected != null ? "rewrite" : "dictation";
            if (file == null || peak < 50)
            {
                Done();
                Say(file == null ? "Nothing was recorded" : "The microphone heard silence", 4000, true);
                return;
            }
            bar.Step("Transcribing");
            string heard = null; AssistResult result = null; string error = null;
            Action<string> step = m => ui.BeginInvoke((Action)(() => bar.Step(m)));
            try
            {
                result = await Task.Run(() =>
                {
                    string said = Ai.Transcribe(file);
                    if (said.Length == 0) throw new Exception("Didn't catch that. Try again.");
                    heard = said;
                    if (a)
                    {
                        step(image != null ? "Thinking with your screen" : "Thinking");
                        bool useMcp = S.McpOn && S.McpServer.Length > 0;
                        string token = useMcp ? Mcp.AccessToken() : null;
                        var ctx = new AssistContext
                        {
                            App = t.Editable ? t.App : (t.App ?? "the computer") + " (no text field is focused: your answer is shown to the user and copied)",
                            Selected = t.Selected, AboutMe = S.AboutMe, CanAsk = true, // a question opens the Ask box
                        };
                        return Ai.RunAssistant(said, ctx, useMcp, token, null, step, Confirm, image);
                    }
                    if (t.Selected != null) { step("Rewriting"); return new AssistResult { Text = Ai.VoiceEdit(said, t.Selected) }; }
                    if (S.Cleanup) { step("Cleaning up"); return new AssistResult { Text = Ai.CleanDictation(said) }; }
                    return new AssistResult { Text = said };
                });
            }
            catch (Exception e) { while (e is AggregateException && e.InnerException != null) e = e.InnerException; error = e.Message; }
            finally { try { File.Delete(file); } catch { } }

            if (heard != null || error == null)
            {
                var c = History.Add(null, kind, t.App, heard ?? "", result, error, image);
                lastConversation = c.Id;
            }
            if (error != null) { Done(); Say(error, 6000, true); return; }
            if (result.IsQuestion)
            {
                // The Assistant needs one more thing: ask it in the Ask box, where the answer can be typed.
                Done();
                askConversation = lastConversation; askPrevious = result.ResponseId;
                Ask().OpenWithQuestion(result.Text, t.App, t.Editable, t.Selected != null, bar.PillOnScreen);
                return;
            }

            bool type = !result.IsStatus && (t.Editable || t.Selected != null);
            if (a) Clip.Copy(result.Text); // every Assistant answer is ready to paste again
            if (type)
            {
                await Insert(result.Text);
                Done();
                Say(a ? "Typed · copied" : "Pasted", 1800);
            }
            else if (a)
            {
                Done();
                card.Show(result.Text, result.IsStatus, bar.PillOnScreen);
            }
            else
            {
                Clip.Copy(result.Text);
                Done();
                Say("Copied · no text field, paste it anywhere", 3000);
            }
        }

        void Done() { mode = Mode.Idle; bar.State = BarState.Rest; }

        // ---- typing to the Assistant ----

        AskBox Ask()
        {
            if (ask != null && !ask.IsDisposed) return ask;
            ask = new AskBox();
            ask.Send += AskSend;
            ask.Accept += async text =>
            {
                ask.Hide();
                await Task.Delay(80);
                await Insert(text);
                Say(target != null && target.Selected != null ? "Replaced" : "Inserted", 1600);
            };
            ask.OpenChat += () => ShowHistory(askConversation ?? lastConversation);
            return ask;
        }

        /// <summary>
        /// The Type button or its shortcut: reads the selection in the app in front (as a voice request does), then
        /// opens the Ask box above the bar. Pressed again while the box is open, it closes it.
        /// </summary>
        async void TypeToAssistant()
        {
            if (ask != null && ask.Visible) { ask.Hide(); return; }
            if (mode != Mode.Idle) return; // recording or working: one thing at a time
            if (!S.Configured) { ShowSettings(); return; }
            card.Hide();
            mode = Mode.Working; // while the selection is read
            try { target = await Target.Capture(); }
            finally { mode = Mode.Idle; }
            if (target.IsOurs) return; // Prosper's own window was in front
            askConversation = null; askPrevious = null;
            Ask().Open(target.Selected, target.App, target.Editable, bar.PillOnScreen);
            Log.Write("typed request " + (target.Selected != null ? "with selection" : "without selection"));
        }

        /// <summary>A typed request, an answer to the Assistant's question, or a refinement of its last answer.</summary>
        async void AskSend(string text)
        {
            var t = target; var box = ask;
            if (t == null || box == null) return;
            bool first = askPrevious == null;
            string previous = askPrevious;
            box.Working("Thinking");
            Action<string> step = m => ui.BeginInvoke((Action)(() => { if (!box.IsDisposed) box.Working(m); }));
            AssistResult result = null; string error = null;
            try
            {
                result = await Task.Run(() =>
                {
                    bool useMcp = S.McpOn && S.McpServer.Length > 0;
                    string token = useMcp ? Mcp.AccessToken() : null;
                    // The first request carries the selection and the app; later ones continue that conversation.
                    var ctx = first
                        ? new AssistContext { App = (t.App ?? "the computer") + " (the answer is shown to the user first; they choose to put it in)", Selected = t.Selected, AboutMe = S.AboutMe, CanAsk = true }
                        : new AssistContext { CanAsk = true };
                    return Ai.RunAssistant(text, ctx, useMcp, token, previous, step, Confirm, null);
                });
            }
            catch (Exception e) { while (e is AggregateException && e.InnerException != null) e = e.InnerException; error = e.Message; }

            var c = History.Add(askConversation, "assistant", t.App, text, result, error);
            askConversation = lastConversation = c.Id;
            if (box.IsDisposed) return;
            if (error != null) { box.Fail(error); return; }
            askPrevious = result.ResponseId;
            if (result.IsQuestion) { box.Question(result.Text); return; }
            Clip.Copy(result.Text); // like every Assistant answer: ready to paste, even if the box is closed
            box.Answer(result.Text, result.IsStatus);
        }

        /// <summary>Pastes into the window that was in front when the recording started.</summary>
        async Task Insert(string text)
        {
            if (target == null) return;
            if (Native.GetForegroundWindow() != target.Window) { Native.SetForegroundWindow(target.Window); await Task.Delay(150); }
            await Clip.Paste(text);
        }

        /// <summary>From the Assistant's worker thread: asks on screen before an MCP tool runs.</summary>
        Approval Confirm(string tool, string args)
        {
            var answer = Approval.Deny;
            ui.Invoke((Action)(() =>
            {
                using (var f = new ApprovalForm(tool, Ai.Describe(tool, args), args)) { f.ShowDialog(); answer = f.Result; }
            }));
            return answer;
        }

        // ---- windows ----

        void ShowHistory(string id)
        {
            if (history == null || history.IsDisposed)
            {
                history = new HistoryForm(Confirm);
                history.FormClosed += (s, e) => history = null;
            }
            history.Show();
            if (id != null) history.Open(id);
            if (history.WindowState == FormWindowState.Minimized) history.WindowState = FormWindowState.Normal;
            history.Activate();
        }

        void ShowSettings()
        {
            if (settings == null || settings.IsDisposed)
            {
                settings = new SettingsForm();
                settings.Saved += () => { ApplyBar(); if (!Preview) ApplyKeys(); };
                settings.FormClosed += (s, e) => settings = null;
            }
            settings.Show(); settings.Activate();
        }

        void Quit()
        {
            recorder.Cancel();
            keys.Dispose();
            tray.Visible = false;
            ExitThread();
        }
    }
}
