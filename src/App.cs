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

    /// <summary>Receives the global shortcuts (RegisterHotKey: no keyboard hook, nothing else is read).</summary>
    class Hotkeys : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        public Hotkeys() { CreateHandle(new CreateParams()); }
        public bool Add(int id, Keys key) { return Native.RegisterHotKey(Handle, id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)key); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
        public void Dispose() { for (int i = 1; i <= 4; i++) Native.UnregisterHotKey(Handle, i); DestroyHandle(); }
    }

    class App : ApplicationContext
    {
        const int HkDictate = 1, HkAssist = 2, HkScreen = 3, HkHistory = 4;
        readonly Control ui = new Control();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Bar bar = new Bar();
        readonly AnswerCard card = new AnswerCard();
        readonly Hotkeys keys = new Hotkeys();
        readonly Recorder recorder = new Recorder("prosperrec");
        readonly System.Windows.Forms.Timer autoStop = new System.Windows.Forms.Timer { Interval = 10 * 60 * 1000 }; // 10 minutes: under OpenAI's 25 MB
        HistoryForm history;
        SettingsForm settings;

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
            tray.Icon = TrayIcon(); tray.Text = "Prosper Voice"; tray.Visible = true;
            tray.ContextMenuStrip = Menu();
            tray.DoubleClick += (s, e) => ShowHistory(null);

            bar.Tap += () => Toggle(false);
            bar.DoubleTap += () => Toggle(true);
            bar.Hold += () => ShowHistory(lastConversation);
            bar.ShowMenu += () => Menu().Show(Cursor.Position);
            bar.Finish += () => { if (mode == Mode.Recording) Stop(); };
            bar.Cancel += CancelRecording;
            bar.ToggleScreen += AddScreen;
            bar.LevelSource = () => recorder.Level;
            bar.PlaceSaved();
            bar.KeepVisible = S.ShowBubble;

            card.Insert += async () => await Insert(card.Answer);
            card.OpenChat += () => ShowHistory(lastConversation);
            autoStop.Tick += (s, e) => { if (mode == Mode.Recording) Stop(); };

            keys.Pressed += id =>
            {
                if (id == HkDictate) Toggle(false);
                else if (id == HkAssist) Toggle(true);
                else if (id == HkScreen) { if (mode == Mode.Recording && assist) AddScreen(); }
                else if (id == HkHistory) ShowHistory(lastConversation);
            };
            if (Preview) { Demo(); return; } // no shortcuts, no setup: just the bar
            var taken = new System.Collections.Generic.List<string>();
            if (!keys.Add(HkDictate, Keys.Space)) taken.Add("Ctrl+Alt+Space");
            if (!keys.Add(HkAssist, Keys.A)) taken.Add("Ctrl+Alt+A");
            if (!keys.Add(HkScreen, Keys.S)) taken.Add("Ctrl+Alt+S");
            if (!keys.Add(HkHistory, Keys.H)) taken.Add("Ctrl+Alt+H");
            if (taken.Count > 0)
            {
                Log.Write("shortcuts taken by another app: " + string.Join(", ", taken));
                Balloon(string.Join(", ", taken) + " already belong to another app: use the bar for those.");
            }

            if (!S.Configured) { Balloon("Add your OpenAI API key to start."); ShowSettings(); }
            else if (!S.ShowBubble) Balloon("Prosper Voice is running. Ctrl+Alt+Space to dictate, Ctrl+Alt+A for the Assistant.");
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
            var m = new ContextMenuStrip();
            m.Items.Add("Dictate  (Ctrl+Alt+Space)", null, (s, e) => Toggle(false));
            m.Items.Add("Assistant  (Ctrl+Alt+A)", null, (s, e) => Toggle(true));
            m.Items.Add("History  (Ctrl+Alt+H)", null, (s, e) => ShowHistory(lastConversation));
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
                            Selected = t.Selected, AboutMe = S.AboutMe,
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
                settings.Saved += ApplyBar;
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

        /// <summary>The tray icon: the glass pill with its waveform and gold centre bar.</summary>
        static Icon TrayIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = Floating.Round(new RectangleF(1, 6, 30, 20), 10))
                using (var b = new SolidBrush(Color.FromArgb(24, 24, 27))) g.FillPath(b, p);
                float[] h = { 4, 9, 13, 9, 4 };
                using (var pen = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < h.Length; i++) { float x = 16 + (i - 2) * 4.4f; if (i != 2) g.DrawLine(pen, x, 16 - h[i] / 2, x, 16 + h[i] / 2); }
                using (var gold = new Pen(Theme.Gold, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(gold, 16, 16 - 6.5f, 16, 16 + 6.5f);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
