// The two ordinary windows: Settings, and History (every conversation, with follow-ups by text, voice or an audio file).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    /// <summary>A sidebar entry: the selected one is raised, with a gold marker like the menus.</summary>
    class NavItem : Button
    {
        public bool Selected;
        bool over;
        public NavItem(string text)
        {
            Text = text; Font = Style.Strong; Cursor = Cursors.Hand; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = Style.Px(38);
        }
        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Style.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Selected || over)
                using (var p = Floating.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Style.Px(8)))
                using (var b = new SolidBrush(Selected ? Style.Raised : Color.FromArgb(32, 32, 36))) g.FillPath(b, p);
            if (Selected) using (var b = new SolidBrush(Style.Gold)) g.FillRectangle(b, 0, Style.Px(10), Style.Px(3), Height - Style.Px(20));
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Style.Px(16), 0, Width - Style.Px(16), Height), Selected ? Style.Text : Style.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }
    }

    class SettingsForm : Form
    {
        public event Action Saved;
        static readonly int CardW = Style.Px(556);
        readonly TextBox key = new TextBox { UseSystemPasswordChar = true };
        readonly TextBox endpoint = new TextBox(), model = new TextBox(), transcribe = new TextBox();
        readonly TextBox about = new TextBox { Multiline = true, ScrollBars = ScrollBars.None, AcceptsReturn = true };
        readonly TextBox mcpServer = new TextBox(), mcpToken = new TextBox { UseSystemPasswordChar = true }, mcpAllowed = new TextBox();
        readonly Toggle cleanup, shot, keep, bubble, startup, mcpOn;
        readonly Label status = new Label { AutoSize = true, ForeColor = Style.Ok, Font = Style.Small, Margin = new Padding(Style.Px(6), Style.Px(9), 0, 0) };
        readonly Label mcpStatus = new Label { AutoSize = true, Font = Style.Small, Margin = new Padding(0, Style.Px(8), 0, 0) };
        readonly Label note = new Label { AutoSize = true, ForeColor = Style.Muted, Font = Style.Small };
        readonly Label pageTitle = new Label { AutoSize = true, Font = Style.Title, ForeColor = Style.Text };
        readonly Label pageNote = new Label { AutoSize = true, Font = Style.Body, ForeColor = Style.Muted };
        readonly Panel pages = new Panel { Dock = DockStyle.Fill, BackColor = Style.Bg };
        readonly System.Collections.Generic.List<NavItem> nav = new System.Collections.Generic.List<NavItem>();

        public SettingsForm()
        {
            Text = "Prosper Voice · Settings"; StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(Style.Px(860), Style.Px(660));
            int inner = CardW - Style.Px(40);
            cleanup = new Toggle("Clean up dictation: removes ums and false starts, adds punctuation", inner);
            shot = new Toggle("Show the Add screen button while the Assistant listens", inner);
            keep = new Toggle("Keep history on this PC, encrypted for your Windows account", inner);
            bubble = new Toggle("Show the Prosper bar when idle", inner);
            startup = new Toggle("Start Prosper when I sign in to Windows", inner);
            mcpOn = new Toggle("Let the Assistant use this server", inner);

            var s = Settings.Current;
            key.Text = ""; endpoint.Text = s.ApiBase; model.Text = s.Model; transcribe.Text = s.TranscribeModel; about.Text = s.AboutMe;
            cleanup.Checked = s.Cleanup; shot.Checked = s.ScreenshotButton; keep.Checked = s.KeepHistory; bubble.Checked = s.ShowBubble;
            try { startup.Checked = Startup.On; } catch { }
            mcpServer.Text = s.McpServer; mcpAllowed.Text = s.McpAllowed; mcpOn.Checked = s.McpOn;

            // ---- Connection ----
            var test = Style.Btn("Test connection");
            test.Click += async (o, e) =>
            {
                Apply(); status.ForeColor = Style.Muted; status.Text = "Testing…";
                try { status.Text = await Task.Run(() => Ai.Test()); status.ForeColor = Style.Ok; }
                catch (Exception x) { status.Text = Unwrap(x); status.ForeColor = Style.Bad; }
            };
            status.MaximumSize = new Size(inner - test.Width - Style.Px(16), 0);
            var openai = NewCard("OpenAI", "Prosper talks to OpenAI directly from this PC, with your own key.");
            Field(openai, "API key", key, s.Configured ? "Saved and encrypted. Paste a new key to replace it." : "Paste your key (sk-…). It is encrypted for your Windows account.");
            Field(openai, "Endpoint", endpoint, "https://api.openai.com/v1, or your company's Azure OpenAI / compatible endpoint (…/openai/v1).");
            int half = (inner - Style.Px(16)) / 2;
            openai.Controls.Add(Flow(Labelled("Model", new FieldBox(model, half)), Labelled("Speech to text", new FieldBox(transcribe, half))));
            openai.Controls.Add(Gap(6));
            openai.Controls.Add(Flow(test, status));
            Page("Connection", "Your OpenAI key and the models Prosper uses.", openai);

            // ---- Assistant ----
            var you = NewCard("About you", "Name, role, how you sign off. Sent only with Assistant requests.");
            you.Controls.Add(new FieldBox(about, inner, Style.Px(104)));
            var behave = NewCard("Behaviour", null);
            behave.Controls.Add(cleanup); behave.Controls.Add(shot);
            Page("Assistant", "What the Assistant knows about you, and how dictation is tidied.", you, behave);

            // ---- This PC ----
            var pc = NewCard("Privacy and startup", null);
            pc.Controls.Add(keep); pc.Controls.Add(bubble); pc.Controls.Add(startup);
            var clear = Style.Btn("Clear history");
            clear.Click += (o, e) =>
            {
                if (MessageBox.Show(this, "Delete every conversation and screenshot kept on this PC?", "Clear history", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                { History.Clear(); Say("History cleared.", Style.Ok); }
            };
            var folder = Style.Btn("Open data folder");
            folder.Click += (o, e) => { Directory.CreateDirectory(Paths.Dir); System.Diagnostics.Process.Start("explorer.exe", "\"" + Paths.Dir + "\""); };
            var data = NewCard("Your data", "Settings, keys and history live in one folder in your profile, encrypted with Windows DPAPI.");
            data.Controls.Add(Flow(clear, folder));
            Page("This PC", "History, the bar and startup. Nothing leaves this PC except your requests to OpenAI.", pc, data);

            // ---- MCP ----
            var signIn = Style.Btn("Sign in", true);
            var signOut = Style.Btn("Sign out");
            signIn.Click += async (o, e) =>
            {
                Apply(); mcpStatus.ForeColor = Style.Muted; mcpStatus.Text = "Finish the sign-in in your browser…"; signIn.Enabled = false;
                try { await Task.Run(() => Mcp.SignIn()); mcpStatus.Text = "Connected."; mcpStatus.ForeColor = Style.Ok; mcpOn.Checked = true; Apply(); }
                catch (Exception x) { mcpStatus.Text = Unwrap(x); mcpStatus.ForeColor = Style.Bad; }
                signIn.Enabled = true;
            };
            signOut.Click += (o, e) => { Mcp.SignOut(); mcpOn.Checked = false; mcpToken.Text = ""; Apply(); mcpStatus.Text = "Signed out."; mcpStatus.ForeColor = Style.Muted; };
            mcpStatus.Text = Mcp.SignedIn ? "Connected." : "Not connected."; mcpStatus.ForeColor = Mcp.SignedIn ? Style.Ok : Style.Muted;
            mcpStatus.MaximumSize = new Size(inner, 0);
            var mcp = NewCard("Server", "Optional. The Assistant only writes text until you connect a server you trust.");
            Field(mcp, "Server URL", mcpServer, "https://… of the MCP server. Leave empty to keep the Assistant to writing only.");
            Field(mcp, "Access token", mcpToken, "Only for servers without sign-in. Leave empty and use Sign in for OAuth servers.");
            Field(mcp, "Always allowed", mcpAllowed, "Tool names that may run without asking, comma separated. Every other tool asks first.");
            mcp.Controls.Add(Gap(4)); mcp.Controls.Add(mcpOn);
            mcp.Controls.Add(Gap(4)); mcp.Controls.Add(Flow(signIn, signOut)); mcp.Controls.Add(mcpStatus);
            Page("MCP server", "Connect tools the Assistant can use. Off until you set one up.", mcp);

            // ---- Shortcuts ----
            var keys = NewCard("Keyboard", "These work in any app.");
            Shortcut(keys, "Ctrl+Alt+Space", "Dictate. Press again to stop.");
            Shortcut(keys, "Ctrl+Alt+A", "Assistant. Press again to stop.");
            Shortcut(keys, "Ctrl+Alt+S", "Add the screen while the Assistant listens");
            Shortcut(keys, "Ctrl+Alt+H", "History and chat");
            var mouse = NewCard("The Prosper bar", "At the bottom of the screen; drag it anywhere.");
            Shortcut(mouse, "Click", "Dictate");
            Shortcut(mouse, "Double-click", "Assistant");
            Shortcut(mouse, "Hold", "History");
            Shortcut(mouse, "Right-click", "Menu: settings, hide the bar, quit");
            Page("Shortcuts", "Everything Prosper does is one key or one click away.", keys, mouse);

            // ---- frame: sidebar, header, footer ----
            var side = new Panel { Dock = DockStyle.Left, Width = Style.Px(220), BackColor = Style.Surface, Padding = new Padding(Style.Px(12)) };
            side.Paint += (o, e) => { using (var p = new Pen(Style.Line)) e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height); };
            var logo = new LogoView(Style.Px(42)) { Location = new Point(Style.Px(18), Style.Px(22)) };
            var name = new Label { Text = "Prosper", Font = Style.Heading, ForeColor = Style.Text, AutoSize = true, Location = new Point(Style.Px(70), Style.Px(22)) };
            var sub = new Label { Text = "Voice for Windows", Font = Style.Small, ForeColor = Style.Gold, AutoSize = true, Location = new Point(Style.Px(71), Style.Px(45)) };
            side.Controls.Add(logo); side.Controls.Add(name); side.Controls.Add(sub);
            for (int i = 0; i < nav.Count; i++)
            {
                nav[i].Location = new Point(Style.Px(12), Style.Px(96) + i * Style.Px(42)); nav[i].Width = Style.Px(220 - 24);
                side.Controls.Add(nav[i]);
            }
            var privacy = new Label
            {
                Text = "Keys and history are encrypted for your Windows account. No admin rights, no telemetry.",
                Font = Style.Small, ForeColor = Style.Faint, AutoSize = true, MaximumSize = new Size(Style.Px(190), 0), Dock = DockStyle.Bottom, Padding = new Padding(Style.Px(6), 0, 0, Style.Px(8)),
            };
            side.Controls.Add(privacy);

            var head = new Panel { Dock = DockStyle.Top, Height = Style.Px(92), BackColor = Style.Bg };
            pageTitle.Location = new Point(Style.Px(32), Style.Px(24)); pageNote.Location = new Point(Style.Px(33), Style.Px(60));
            head.Controls.Add(pageTitle); head.Controls.Add(pageNote);

            var save = Style.Btn("Save", true);
            save.Click += (o, e) => { Apply(); Close(); };
            var cancel = Style.Btn("Cancel");
            cancel.Click += (o, e) => Close();
            var foot = new Panel { Dock = DockStyle.Bottom, Height = Style.Px(64), BackColor = Style.Bg, Padding = new Padding(Style.Px(32), Style.Px(16), Style.Px(24), Style.Px(16)) };
            foot.Paint += (o, e) => { using (var p = new Pen(Style.Line)) e.Graphics.DrawLine(p, 0, 0, foot.Width, 0); };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = Style.Bg };
            buttons.Controls.Add(cancel); buttons.Controls.Add(save);
            cancel.Margin = new Padding(0);
            note.Dock = DockStyle.Fill; note.TextAlign = ContentAlignment.MiddleLeft; note.AutoSize = false;
            foot.Controls.Add(note); foot.Controls.Add(buttons);

            var main = new Panel { Dock = DockStyle.Fill, BackColor = Style.Bg };
            main.Controls.Add(pages); main.Controls.Add(foot); main.Controls.Add(head);
            Controls.Add(main); Controls.Add(side);
            Style.Window(this);
            AcceptButton = save; CancelButton = cancel;
            ShowPage(0);
        }

        // ---- building blocks ----

        Card NewCard(string title, string description)
        {
            var c = new Card(CardW);
            c.Controls.Add(new Label { Text = title, Font = Style.Heading, ForeColor = Style.Text, AutoSize = true, Margin = new Padding(0, 0, 0, Style.Px(2)) });
            if (description != null) c.Controls.Add(Style.Note(description, c.Inner, Style.Muted, Style.Body));
            c.Controls.Add(Gap(6));
            return c;
        }

        void Page(string title, string description, params Card[] cards)
        {
            var p = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Visible = false,
                BackColor = Style.Bg, Padding = new Padding(Style.Px(32), Style.Px(4), Style.Px(16), Style.Px(16)), Tag = new[] { title, description },
            };
            p.Controls.AddRange(cards);
            pages.Controls.Add(p);
            var item = new NavItem(title);
            int index = nav.Count;
            item.Click += (o, e) => ShowPage(index);
            nav.Add(item);
        }

        void ShowPage(int index)
        {
            for (int i = 0; i < nav.Count; i++)
            {
                var p = pages.Controls[i];
                p.Visible = i == index;
                nav[i].Selected = i == index; nav[i].Invalidate();
                if (i == index) { var t = (string[])p.Tag; pageTitle.Text = t[0]; pageNote.Text = t[1]; }
            }
        }

        void Say(string text, Color color) { note.Text = text; note.ForeColor = color; }

        /// <summary>A label, a field and a hint, one under the other.</summary>
        static void Field(Card c, string label, TextBox box, string hint)
        {
            c.Controls.Add(new Label { Text = label, Font = Style.Strong, ForeColor = Style.Text, AutoSize = true, Margin = new Padding(0, Style.Px(8), 0, Style.Px(2)) });
            c.Controls.Add(new FieldBox(box, c.Inner));
            if (hint != null) c.Controls.Add(Style.Note(hint, c.Inner, Style.Faint));
        }

        static Control Labelled(string label, Control field)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, Style.Px(16), 0), BackColor = Style.Surface };
            p.Controls.Add(new Label { Text = label, Font = Style.Strong, ForeColor = Style.Text, AutoSize = true, Margin = new Padding(0, Style.Px(8), 0, Style.Px(2)) });
            p.Controls.Add(field);
            return p;
        }

        static void Shortcut(Card c, string combo, string what)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, BackColor = Style.Surface, Margin = new Padding(0, Style.Px(2), 0, Style.Px(2)) };
            var caps = new KeyCaps(combo);
            var space = Style.Px(170) - caps.Width;
            caps.Margin = new Padding(0, Style.Px(3), Math.Max(Style.Px(12), space), Style.Px(3));
            row.Controls.Add(caps);
            row.Controls.Add(new Label { Text = what, AutoSize = true, ForeColor = Style.Text, Margin = new Padding(0, Style.Px(7), 0, 0) });
            c.Controls.Add(row);
        }

        static Control Gap(int dp) { return new Panel { Size = new Size(1, Style.Px(dp)), Margin = new Padding(0), BackColor = Style.Surface }; }

        static Control Flow(params Control[] cs)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, BackColor = Style.Surface, Margin = new Padding(0, Style.Px(2), 0, Style.Px(2)) };
            p.Controls.AddRange(cs); return p;
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
    }

    /// <summary>Every conversation: what was said, the screenshot, each tool call, the answers. Ask follow-ups here.</summary>
    class HistoryForm : Form
    {
        readonly ListBox list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, BorderStyle = BorderStyle.None };
        readonly WebBrowser web = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, AllowWebBrowserDrop = false };
        readonly TextBox ask = new TextBox { Multiline = true, ScrollBars = ScrollBars.None };
        readonly GlyphButton send = new GlyphButton(Glyphs.Send, true, "Send (Ctrl+Enter)");
        readonly GlyphButton mic = new GlyphButton(Glyphs.Mic, false, "Speak your question");
        readonly GlyphButton attach = new GlyphButton(Glyphs.Attach, false, "Attach an audio file for its transcript");
        readonly GlyphButton remove = new GlyphButton(Glyphs.Trash, false, "Delete this conversation");
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = Style.Px(24), ForeColor = Style.Gold, Font = Style.Small, Padding = new Padding(Style.Px(24), Style.Px(4), Style.Px(24), 0) };
        readonly Label title = new Label { AutoSize = true, Font = Style.Heading, ForeColor = Style.Text, Location = new Point(Style.Px(24), Style.Px(13)) };
        readonly Label subtitle = new Label { AutoSize = true, Font = Style.Small, ForeColor = Style.Muted, Location = new Point(Style.Px(25), Style.Px(37)) };
        readonly Label count = new Label { AutoSize = true, Font = Style.Small, ForeColor = Style.Muted, Location = new Point(Style.Px(58), Style.Px(40)) };
        readonly Recorder recorder = new Recorder("prosperchat");
        readonly Func<string, string, Approval> confirm;
        string current; // conversation id; null = a new chat
        string audioName, audioText;
        bool busy;

        public HistoryForm(Func<string, string, Approval> confirm)
        {
            this.confirm = confirm;
            Text = "Prosper Voice · History"; StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Style.Px(1040), Style.Px(720)); MinimumSize = new Size(Style.Px(720), Style.Px(460));
            list.ItemHeight = Style.Px(62);
            list.DrawItem += DrawItem;
            list.SelectedIndexChanged += (s, e) => { var c = list.SelectedItem as Conversation; if (c != null) Open(c.Id); };
            web.Navigating += Browser.OpenLinksOutside;

            // Left: the logo, "History" and a new-chat button over the list.
            var newChat = new GlyphButton(Glyphs.Plus, false, "New chat") { Anchor = AnchorStyles.Top | AnchorStyles.Right };
            newChat.Click += (s, e) => { list.ClearSelected(); Open(null); ask.Focus(); };
            var top = new Panel { Dock = DockStyle.Top, Height = Style.Px(76), BackColor = Style.Surface };
            top.Controls.Add(new LogoView(Style.Px(32)) { Location = new Point(Style.Px(16), Style.Px(20)) });
            top.Controls.Add(new Label { Text = "History", Font = Style.Heading, ForeColor = Style.Text, AutoSize = true, Location = new Point(Style.Px(57), Style.Px(17)) });
            top.Controls.Add(count);
            top.Controls.Add(newChat);
            top.Resize += (s, e) => newChat.Location = new Point(top.Width - newChat.Width - Style.Px(14), Style.Px(21));
            var left = new Panel { Dock = DockStyle.Fill, BackColor = Style.Surface, Padding = new Padding(0, 0, 0, Style.Px(8)) };
            left.Controls.Add(list); left.Controls.Add(top);

            // Right: the conversation's title, the conversation, and the composer.
            remove.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            remove.Click += (s, e) =>
            {
                if (current == null) return;
                if (MessageBox.Show(this, "Delete this conversation?", "Delete", MessageBoxButtons.YesNo) == DialogResult.Yes) { History.Delete(current); Open(null); }
            };
            var head = new Panel { Dock = DockStyle.Top, Height = Style.Px(64), BackColor = Style.Bg };
            head.Controls.Add(title); head.Controls.Add(subtitle); head.Controls.Add(remove);
            head.Resize += (s, e) => remove.Location = new Point(head.Width - remove.Width - Style.Px(18), Style.Px(15));
            head.Paint += (s, e) => { using (var p = new Pen(Style.Line)) e.Graphics.DrawLine(p, 0, head.Height - 1, head.Width, head.Height - 1); };

            var tools = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Style.Field, Padding = new Padding(0, Style.Px(8), 0, 0) };
            tools.Controls.Add(attach); tools.Controls.Add(mic); tools.Controls.Add(send);
            var field = new FieldBox(ask, Style.Px(400), Style.Px(64)) { Dock = DockStyle.Fill };
            field.Controls.Add(tools);
            var box = new Panel { Dock = DockStyle.Bottom, Height = Style.Px(88), BackColor = Style.Bg, Padding = new Padding(Style.Px(20), Style.Px(4), Style.Px(20), Style.Px(18)) };
            box.Controls.Add(field);
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Style.Bg };
            right.Controls.Add(web); right.Controls.Add(status); right.Controls.Add(box); right.Controls.Add(head);

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 1 };
            split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
            Controls.Add(split);
            Load += (s, e) => split.SplitterDistance = Style.Px(300); // once the window has its size
            Style.Window(this);

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
            count.Text = list.Items.Count == 1 ? "1 conversation" : list.Items.Count + " conversations";
            for (int i = 0; i < list.Items.Count; i++) if (((Conversation)list.Items[i]).Id == keep) { list.SelectedIndex = i; break; }
        }

        static Action<Graphics, float, float, float, Color> GlyphOf(Conversation c)
        {
            return c.Kind == "assistant" || c.Kind == "chat" ? Glyphs.Sparkle : c.Kind == "rewrite" ? (Action<Graphics, float, float, float, Color>)Glyphs.Pen : Glyphs.Wave;
        }

        void DrawItem(object sender, DrawItemEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Style.Surface)) g.FillRectangle(bg, e.Bounds);
            if (e.Index < 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var c = (Conversation)list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var card = new RectangleF(e.Bounds.X + Style.Px(8), e.Bounds.Y + Style.Px(2), e.Bounds.Width - Style.Px(16), e.Bounds.Height - Style.Px(4));
            if (sel)
            {
                using (var p = Floating.Round(card, Style.Px(10))) using (var b = new SolidBrush(Style.Raised)) g.FillPath(b, p);
                using (var b = new SolidBrush(Style.Gold)) g.FillRectangle(b, card.X, card.Y + Style.Px(14), Style.Px(3), card.Height - Style.Px(28));
            }
            // The kind of conversation, as a small icon in a circle.
            float d = Style.Px(32), ix = card.X + Style.Px(12), iy = card.Y + (card.Height - d) / 2;
            using (var b = new SolidBrush(sel ? Style.Bg : Style.Raised)) g.FillEllipse(b, ix, iy, d, d);
            bool ai = c.Kind == "assistant" || c.Kind == "chat";
            GlyphOf(c)(g, ix + d / 2, iy + d / 2, d * 0.5f, ai ? Style.Gold : Style.Muted);
            int tx = (int)(ix + d + Style.Px(12)), tw = (int)(card.Right - tx - Style.Px(10));
            var when = DateTimeOffset.FromUnixTimeMilliseconds(c.UpdatedAt).ToLocalTime().ToString("d MMM, HH:mm");
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(c.Title) ? "(nothing heard)" : c.Title, Style.Strong,
                new Rectangle(tx, (int)card.Y + Style.Px(10), tw, Style.Px(20)), Style.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, when + "  ·  " + (c.Summary ?? ""), Style.Small,
                new Rectangle(tx, (int)card.Y + Style.Px(31), tw, Style.Px(18)), Style.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        static string empty;
        /// <summary>The new-chat page: the logo and what can be done here.</summary>
        static string Empty()
        {
            return empty ?? (empty = Markdown.Page(
                "<div class='empty'><img src='" + Logo.DataUri(Style.Px(72)) + "' width='72' height='72'>" +
                "<h2>Ask Prosper anything</h2><p>Type below, or speak with the microphone. Attach an audio file to get its transcript, " +
                "then ask about it: \"list the action items\".</p>" +
                "<div class='chips'><span>Draft a reply to this email</span><span>Summarise my last meeting</span><span>Make this sound friendlier</span></div>" +
                "<p class='small'>Everything you do with the Prosper bar shows up on the left.</p></div>"));
        }

        public void Open(string id)
        {
            current = id;
            var c = History.Get(id);
            Browser.Load(web, c != null ? Markdown.Conversation(c) : Empty());
            if (c != null) web.DocumentCompleted += ScrollDown;
            title.Text = c == null ? "New chat" : string.IsNullOrWhiteSpace(c.Title) ? "(nothing heard)" : Short(c.Title, 70);
            subtitle.Text = c == null ? "Ask by typing, speaking or attaching audio" :
                (c.Kind == "dictation" ? "Dictation" : c.Kind == "rewrite" ? "Voice edit" : "Assistant") +
                (string.IsNullOrEmpty(c.App) ? "" : "  ·  " + Short(c.App, 50)) + "  ·  " +
                DateTimeOffset.FromUnixTimeMilliseconds(c.CreatedAt).ToLocalTime().ToString("d MMM yyyy, HH:mm");
            remove.Visible = c != null;
        }

        static string Short(string s, int n) { s = s.Replace("\r", " ").Replace("\n", " "); return s.Length > n ? s.Substring(0, n) + "…" : s; }

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
                mic.Active = false; mic.Glyph = Glyphs.Mic; mic.Invalidate(); status.Text = "Transcribing…";
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
            mic.Active = true; mic.Glyph = Glyphs.Stop; mic.Invalidate(); status.Text = "Listening… click stop when you're done.";
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
                    else status.Text = f.Name + " transcribed: press Send to ask about it.";
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
