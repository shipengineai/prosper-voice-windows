// The two ordinary windows: Settings, and History (every conversation, with follow-ups by text, voice or an audio file).
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Prosper
{
    static class Startup
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // The current user's own Run key: no admin rights.
        public static bool On
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k != null && k.GetValue("ProsperVoice") != null; }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (value) k.SetValue("ProsperVoice", "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue("ProsperVoice") != null) k.DeleteValue("ProsperVoice");
                }
            }
        }
    }

    class SettingsForm : Form
    {
        public event Action Saved;
        readonly TableLayoutPanel grid = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        readonly TextBox key = new TextBox { UseSystemPasswordChar = true, Width = 360 };
        readonly TextBox endpoint = new TextBox { Width = 360 }, model = new TextBox { Width = 200 }, transcribe = new TextBox { Width = 200 };
        readonly TextBox about = new TextBox { Multiline = true, Height = 64, Width = 360, ScrollBars = ScrollBars.Vertical };
        readonly CheckBox cleanup = new CheckBox { Text = "Clean up dictation (removes ums and false starts, adds punctuation)", AutoSize = true };
        readonly CheckBox shot = new CheckBox { Text = "Show the \"📷 Add screen\" button while the Assistant listens", AutoSize = true };
        readonly CheckBox keep = new CheckBox { Text = "Keep history on this PC (encrypted for your Windows account)", AutoSize = true };
        readonly CheckBox bubble = new CheckBox { Text = "Show the Prosper bar when idle", AutoSize = true };
        readonly CheckBox startup = new CheckBox { Text = "Start Prosper when I sign in to Windows", AutoSize = true };
        readonly TextBox mcpServer = new TextBox { Width = 360 }, mcpToken = new TextBox { UseSystemPasswordChar = true, Width = 360 }, mcpAllowed = new TextBox { Width = 360 };
        readonly CheckBox mcpOn = new CheckBox { Text = "Let the Assistant use this server", AutoSize = true };
        readonly Label status = new Label { AutoSize = true, ForeColor = Color.FromArgb(15, 107, 79), MaximumSize = new Size(560, 0) };
        readonly Label mcpStatus = new Label { AutoSize = true, MaximumSize = new Size(560, 0) };

        public SettingsForm()
        {
            Text = "Prosper Voice settings"; Font = new Font("Segoe UI", 9.5f); StartPosition = FormStartPosition.CenterScreen;
            AutoScroll = true; Size = new Size(Floating.Px(660), Floating.Px(760)); MinimizeBox = false;
            var s = Settings.Current;
            key.Text = ""; endpoint.Text = s.ApiBase; model.Text = s.Model; transcribe.Text = s.TranscribeModel; about.Text = s.AboutMe;
            cleanup.Checked = s.Cleanup; shot.Checked = s.ScreenshotButton; keep.Checked = s.KeepHistory; bubble.Checked = s.ShowBubble;
            try { startup.Checked = Startup.On; } catch { }
            mcpServer.Text = s.McpServer; mcpAllowed.Text = s.McpAllowed; mcpOn.Checked = s.McpOn;

            Section("OpenAI");
            Row("API key", Stack(key, Hint(s.Configured ? "Saved, encrypted. Paste a new one to replace it." : "Paste your key (sk-…). It is encrypted for your Windows account.")));
            Row("Endpoint", Stack(endpoint, Hint("https://api.openai.com/v1, or your company's Azure OpenAI / compatible endpoint (…/openai/v1).")));
            Row("Model", model);
            Row("Speech to text", transcribe);
            var test = new Button { Text = "Test connection", AutoSize = true };
            test.Click += async (o, e) =>
            {
                Apply(); status.ForeColor = Color.DimGray; status.Text = "Testing…";
                try { status.Text = await Task.Run(() => Ai.Test()); status.ForeColor = Color.FromArgb(15, 107, 79); }
                catch (Exception x) { status.Text = Unwrap(x); status.ForeColor = Color.Firebrick; }
            };
            Row("", Stack(test, status));

            Section("Assistant");
            Row("About you", Stack(about, Hint("Name, role, sign-off. Sent only with Assistant requests.")));
            Row("", Stack(cleanup, shot));

            Section("This PC");
            var clear = new Button { Text = "Clear history", AutoSize = true };
            clear.Click += (o, e) =>
            {
                if (MessageBox.Show(this, "Delete every conversation and screenshot kept on this PC?", "Clear history", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                { History.Clear(); status.Text = "History cleared."; }
            };
            var folder = new Button { Text = "Open data folder", AutoSize = true };
            folder.Click += (o, e) => { Directory.CreateDirectory(Paths.Dir); System.Diagnostics.Process.Start("explorer.exe", "\"" + Paths.Dir + "\""); };
            Row("", Stack(keep, bubble, startup, Flow(clear, folder)));

            Section("MCP server (optional, off until you connect one)");
            Row("Server URL", Stack(mcpServer, Hint("https://… of an MCP server you trust. Leave empty to keep the Assistant to writing only.")));
            Row("Access token", Stack(mcpToken, Hint("Only for servers without sign-in. Leave empty and use Sign in for OAuth servers.")));
            Row("Always allowed", Stack(mcpAllowed, Hint("Tool names that may run without asking (comma separated). Every other tool asks first.")));
            var signIn = new Button { Text = "Sign in", AutoSize = true };
            var signOut = new Button { Text = "Sign out", AutoSize = true };
            signIn.Click += async (o, e) =>
            {
                Apply(); mcpStatus.ForeColor = Color.DimGray; mcpStatus.Text = "Finish the sign-in in your browser…"; signIn.Enabled = false;
                try { await Task.Run(() => Mcp.SignIn()); mcpStatus.Text = "Connected."; mcpStatus.ForeColor = Color.FromArgb(15, 107, 79); mcpOn.Checked = true; Apply(); }
                catch (Exception x) { mcpStatus.Text = Unwrap(x); mcpStatus.ForeColor = Color.Firebrick; }
                signIn.Enabled = true;
            };
            signOut.Click += (o, e) => { Mcp.SignOut(); mcpOn.Checked = false; mcpToken.Text = ""; Apply(); mcpStatus.Text = "Signed out."; };
            mcpStatus.Text = Mcp.SignedIn ? "Connected." : "Not connected.";
            Row("", Stack(mcpOn, Flow(signIn, signOut), mcpStatus));

            Section("Shortcuts");
            Row("", Hint("Ctrl+Alt+Space  dictate (again to stop)\nCtrl+Alt+A  Assistant (again to stop)\nCtrl+Alt+S  add the screen while the Assistant listens\nCtrl+Alt+H  history\n" +
                "Bar (bottom of the screen): click to dictate, double-click for the Assistant, hold for history, right-click for the menu, drag to move."));

            var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.None };
            save.Click += (o, e) => { Apply(); Close(); };
            var cancel = new Button { Text = "Cancel", AutoSize = true };
            cancel.Click += (o, e) => Close();
            Row("", Flow(save, cancel));
            Controls.Add(grid);
            AcceptButton = save; CancelButton = cancel;
        }

        static string Unwrap(Exception x) { while (x is AggregateException && x.InnerException != null) x = x.InnerException; return x.Message; }

        void Apply()
        {
            var s = Settings.Current;
            if (key.Text.Trim().Length > 0) { s.SetApiKey(key.Text); key.Text = ""; }
            s.ApiBase = endpoint.Text.Trim().Length > 0 ? endpoint.Text.Trim().TrimEnd('/') : "https://api.openai.com/v1";
            if (!s.ApiBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s.ApiBase = "https://api.openai.com/v1"; // keys only over https
            s.Model = model.Text.Trim().Length > 0 ? model.Text.Trim() : "gpt-6-luna";
            s.TranscribeModel = transcribe.Text.Trim().Length > 0 ? transcribe.Text.Trim() : "gpt-4o-mini-transcribe";
            s.AboutMe = about.Text.Trim();
            s.Cleanup = cleanup.Checked; s.ScreenshotButton = shot.Checked; s.KeepHistory = keep.Checked; s.ShowBubble = bubble.Checked;
            if (mcpServer.Text.Trim() != s.McpServer) { s.ClearMcp(); s.McpServer = mcpServer.Text.Trim(); } // new server, new sign-in
            if (mcpToken.Text.Trim().Length > 0) { s.McpManualSealed = Secret.SealText(mcpToken.Text.Trim()); mcpToken.Text = ""; }
            s.McpAllowed = mcpAllowed.Text.Trim();
            s.McpOn = mcpOn.Checked && s.McpServer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            try { Startup.On = startup.Checked; } catch { }
            Settings.Save();
            if (Saved != null) Saved();
        }

        void Section(string title)
        {
            var l = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(161, 98, 7), Margin = new Padding(0, 14, 0, 4) };
            grid.Controls.Add(l); grid.SetColumnSpan(l, 2);
        }
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 10, 0) });
            grid.Controls.Add(c);
        }
        static Label Hint(string t) { return new Label { Text = t, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(440, 0) }; }
        static Control Stack(params Control[] cs)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            p.Controls.AddRange(cs); return p;
        }
        static Control Flow(params Control[] cs)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
            p.Controls.AddRange(cs); return p;
        }
    }

    /// <summary>Every conversation: what was said, the screenshot, each tool call, the answers. Ask follow-ups here.</summary>
    class HistoryForm : Form
    {
        readonly ListBox list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, BorderStyle = BorderStyle.None };
        readonly WebBrowser web = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, AllowWebBrowserDrop = false };
        readonly TextBox ask = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        readonly Button send = new Button { Text = "Send", Dock = DockStyle.Right, Width = 80 };
        readonly Button mic = new Button { Text = "🎤", Dock = DockStyle.Right, Width = 44 };
        readonly Button attach = new Button { Text = "📎", Dock = DockStyle.Right, Width = 44 };
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Color.FromArgb(161, 98, 7), Padding = new Padding(6, 3, 6, 0) };
        readonly Recorder recorder = new Recorder("prosperchat");
        readonly Func<string, string, Approval> confirm;
        string current; // conversation id; null = a new chat
        string audioName, audioText;
        bool busy;

        public HistoryForm(Func<string, string, Approval> confirm)
        {
            this.confirm = confirm;
            Text = "Prosper Voice · History"; Font = new Font("Segoe UI", 9.5f); StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Floating.Px(1000), Floating.Px(700)); MinimumSize = new Size(640, 420);
            mic.Font = attach.Font = new Font("Segoe UI Emoji", 11f);
            list.ItemHeight = Floating.Px(52);
            list.DrawItem += DrawItem;
            list.SelectedIndexChanged += (s, e) => { var c = list.SelectedItem as Conversation; if (c != null) Open(c.Id); };
            web.Navigating += Browser.OpenLinksOutside;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Floating.Px(38), Padding = new Padding(4) };
            Action<string, EventHandler> btn = (t, h) => { var b = new Button { Text = t, AutoSize = true }; b.Click += h; top.Controls.Add(b); };
            btn("New chat", (s, e) => { list.ClearSelected(); Open(null); });
            btn("Delete", (s, e) =>
            {
                if (current == null) return;
                if (MessageBox.Show(this, "Delete this conversation?", "Delete", MessageBoxButtons.YesNo) == DialogResult.Yes) { History.Delete(current); Open(null); }
            });
            var left = new Panel { Dock = DockStyle.Fill };
            left.Controls.Add(list); left.Controls.Add(top);

            var box = new Panel { Dock = DockStyle.Bottom, Height = Floating.Px(72), Padding = new Padding(6) };
            box.Controls.Add(ask); box.Controls.Add(attach); box.Controls.Add(mic); box.Controls.Add(send);
            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(web); right.Controls.Add(status); right.Controls.Add(box);

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
            Controls.Add(split);
            Load += (s, e) => split.SplitterDistance = Floating.Px(300); // once the window has its size

            send.Click += (s, e) => Ask();
            ask.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; Ask(); } };
            mic.Click += async (s, e) => await Mic();
            attach.Click += async (s, e) => await Attach();
            History.Changed += Reload;
            FormClosed += (s, e) => { History.Changed -= Reload; recorder.Cancel(); };
            Reload();
            Open(null);
        }

        void Reload()
        {
            if (InvokeRequired) { BeginInvoke((Action)Reload); return; }
            var keep = current;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var c in History.All()) list.Items.Add(c);
            list.EndUpdate();
            for (int i = 0; i < list.Items.Count; i++) if (((Conversation)list.Items[i]).Id == keep) { list.SelectedIndex = i; break; }
        }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var c = (Conversation)list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(sel ? Color.FromArgb(240, 237, 228) : Color.White)) e.Graphics.FillRectangle(bg, e.Bounds);
            string mark = c.Kind == "assistant" || c.Kind == "chat" ? "✦ " : c.Kind == "rewrite" ? "✎ " : "🎙 ";
            var r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4, e.Bounds.Width - 16, e.Bounds.Height / 2 - 2);
            TextRenderer.DrawText(e.Graphics, mark + (string.IsNullOrWhiteSpace(c.Title) ? "(nothing heard)" : c.Title), new Font(Font, FontStyle.Bold), r, Color.FromArgb(24, 24, 27), TextFormatFlags.EndEllipsis);
            var when = DateTimeOffset.FromUnixTimeMilliseconds(c.UpdatedAt).ToLocalTime().ToString("d MMM HH:mm");
            r.Y += e.Bounds.Height / 2 - 2;
            TextRenderer.DrawText(e.Graphics, when + " · " + (c.Summary ?? ""), Font, r, Color.DimGray, TextFormatFlags.EndEllipsis);
        }

        public void Open(string id)
        {
            current = id;
            var c = History.Get(id);
            Browser.Load(web, c != null ? Markdown.Conversation(c)
                : Markdown.Page("<p style='color:#71717A'>Ask anything below, by typing or with 🎤. Attach an audio file with 📎 for its transcript, " +
                    "or ask about it (\"list the action items\").<br><br>Everything you do with the bubble shows up on the left.</p>"));
            if (c != null) web.DocumentCompleted += ScrollDown;
        }

        void ScrollDown(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            web.DocumentCompleted -= ScrollDown;
            try { if (web.Document != null && web.Document.Body != null) web.Document.Window.ScrollTo(0, web.Document.Body.ScrollRectangle.Height); } catch { }
        }

        async Task Mic()
        {
            if (recorder.Recording)
            {
                int peak; string file = recorder.Stop(out peak);
                mic.Text = "🎤"; status.Text = "Transcribing…";
                try
                {
                    if (file == null || peak < 50) throw new Exception("The microphone recorded silence. Check Windows Settings → Privacy → Microphone.");
                    string said = await Task.Run(() => Ai.Transcribe(file));
                    ask.Text = (ask.Text.Trim() + " " + said).Trim(); status.Text = "";
                }
                catch (Exception x) { status.Text = Unwrap(x); }
                finally { if (file != null) try { File.Delete(file); } catch { } }
                return;
            }
            string why = recorder.Start();
            if (why != null) { status.Text = "Couldn't start the microphone: " + why; return; }
            mic.Text = "⏹"; status.Text = "Listening… click ⏹ to stop.";
        }

        async Task Attach()
        {
            using (var d = new OpenFileDialog { Filter = "Audio|*.mp3;*.m4a;*.wav;*.webm;*.ogg;*.oga;*.flac;*.mp4;*.mpeg;*.mpga|All files|*.*", Title = "Attach an audio file" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var f = new FileInfo(d.FileName);
                if (f.Length > 25L * 1024 * 1024) { status.Text = "That file is over 25 MB, OpenAI's limit. Trim or compress it first."; return; }
                status.Text = "Transcribing " + f.Name + "…"; attach.Enabled = false;
                try
                {
                    audioText = await Task.Run(() => Ai.Transcribe(d.FileName));
                    audioName = f.Name;
                    if (ask.Text.Trim().Length == 0)
                    {
                        // Sent alone: the transcript is the answer.
                        var c = History.Add(current, "chat", "Prosper chat", "", null, null, null, audioName, audioText);
                        audioName = audioText = null; Open(c.Id); status.Text = "";
                    }
                    else status.Text = "📎 " + f.Name + " transcribed: press Send to ask about it.";
                }
                catch (Exception x) { status.Text = Unwrap(x); audioName = audioText = null; }
                attach.Enabled = true;
            }
        }

        async void Ask()
        {
            string text = ask.Text.Trim();
            if (busy || text.Length == 0) return;
            busy = true; send.Enabled = false; status.Text = "Thinking…";
            var conv = History.Get(current);
            string aName = audioName, aText = audioText; audioName = audioText = null;
            var ctx = new AssistContext
            {
                App = "Prosper chat (your answer is shown in a chat window, not typed into a field)",
                AboutMe = Settings.Current.AboutMe,
                Audio = aName != null ? "File: " + aName + "\n\n" + aText : null,
            };
            var s = Settings.Current;
            Action<string> step = m => BeginInvoke((Action)(() => status.Text = m));
            AssistResult result = null; string error = null;
            try
            {
                result = await Task.Run(() =>
                {
                    bool useMcp = s.McpOn && s.McpServer.Length > 0;
                    string token = useMcp ? Mcp.AccessToken() : null;
                    if (conv != null && conv.ResponseId == null) ctx.Earlier = History.Transcript(conv);
                    try { return Ai.RunAssistant(text, ctx, useMcp, token, conv != null ? conv.ResponseId : null, step, confirm, null); }
                    catch (Exception x)
                    {
                        // OpenAI keeps a conversation for a while only: continue from the text when it has gone.
                        if (conv == null || conv.ResponseId == null || x.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) < 0) throw;
                        ctx.Earlier = History.Transcript(conv);
                        return Ai.RunAssistant(text, ctx, useMcp, token, null, step, confirm, null);
                    }
                });
            }
            catch (Exception x) { error = Unwrap(x); }
            var saved = History.Add(current, "chat", "Prosper chat", text, result, error, null, aName, aText);
            ask.Text = ""; status.Text = error ?? "";
            busy = false; send.Enabled = true;
            Open(saved.Id);
        }

        static string Unwrap(Exception x) { while (x is AggregateException && x.InnerException != null) x = x.InnerException; return x.Message; }
    }
}
