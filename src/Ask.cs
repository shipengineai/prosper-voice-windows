// The typed Assistant, for when talking isn't an option (an open office, a meeting). Select text in any app, hover
// the Prosper bar and click Type (or press the shortcut): a small box opens just above the bar. Say what to do with the
// selection; if the Assistant needs one more thing it asks here, and its answer shows here first, to replace the
// selection or refine ("shorter", "friendlier") before anything in the app changes.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Prosper
{
    class AskBox : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        const int EM_SETCUEBANNER = 0x1501;

        /// <summary>The user sent a request (or an answer, or a refinement).</summary>
        public event Action<string> Send;
        /// <summary>Replace the selection with the answer (or type it at the cursor).</summary>
        public event Action<string> Accept;
        public event Action OpenChat;

        enum Phase { Ready, Working, Question, Answer }
        Phase phase;
        readonly TextBox input = new TextBox();
        readonly FieldBox field;
        readonly WebBrowser web = new WebBrowser { ScriptErrorsSuppressed = true, AllowWebBrowserDrop = false, IsWebBrowserContextMenuEnabled = true };
        readonly Label preview = new Label { AutoSize = false, AutoEllipsis = true, Font = Style.Small, ForeColor = Style.Faint, BackColor = Style.Bg };
        readonly Label foot = new Label { AutoSize = false, AutoEllipsis = true, Font = Style.Small, ForeColor = Style.Faint, BackColor = Style.Bg, TextAlign = ContentAlignment.MiddleLeft };
        readonly FlowLayoutPanel chips = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Style.Bg, Margin = new Padding(0) };
        readonly FlowLayoutPanel actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = Style.Bg };
        readonly PillButton accept = new PillButton("Replace selection", true), chat = new PillButton("Open chat", false);
        readonly Timer spin = new Timer { Interval = 33 };
        float angle;
        string answer = "", selection, step, question, context = "";
        bool canInsert, error, showAccept;
        Rectangle anchor;

        static int W { get { return Style.Px(460); } }
        static int Pad { get { return Style.Px(16); } }

        public AskBox()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Style.Bg; ForeColor = Style.Text; Font = Style.Body; KeyPreview = true; DoubleBuffered = true;
            Text = "Ask Prosper"; Icon = Logo.AppIcon;

            input.Font = Style.Body;
            field = new FieldBox(input, W - 2 * Pad);
            input.KeyDown += OnInputKey;
            input.HandleCreated += (s, e) => Cue();

            foreach (var c in new[] { "Fix grammar", "Shorter", "More formal", "Reply to this" })
            {
                var b = new PillButton(c, false) { Font = Style.Small, Margin = new Padding(0, 0, Style.Px(6), Style.Px(6)) };
                b.Size = new Size(TextRenderer.MeasureText(c, Style.Small).Width + Style.Px(22), Style.Px(26));
                string request = c == "Reply to this" ? "Write a reply to this" : c == "Fix grammar" ? "Fix the grammar and spelling, change nothing else" : "Make it " + c.ToLowerInvariant();
                b.Click += (s, e) => Submit(request);
                b.TabStop = false;
                chips.Controls.Add(b);
            }

            accept.Click += (s, e) => { if (Accept != null) Accept(answer); };
            chat.Click += (s, e) => { Hide(); if (OpenChat != null) OpenChat(); };
            chat.Margin = new Padding(Style.Px(6), 0, 0, 0); accept.Margin = new Padding(Style.Px(6), 0, 0, 0);
            actions.Controls.Add(accept); actions.Controls.Add(chat);

            web.Navigating += Browser.OpenLinksOutside;
            web.PreviewKeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Hide(); };

            Controls.Add(preview); Controls.Add(web); Controls.Add(chips); Controls.Add(field); Controls.Add(foot); Controls.Add(actions);
            spin.Tick += (s, e) => { angle = (angle + 14) % 360; Invalidate(new Rectangle(0, foot.Top, Pad + Style.Px(20), foot.Height)); };
            Deactivate += (s, e) => { if (phase != Phase.Working) Hide(); }; // click away to close; the answer is copied and in History
            OnTop.Keep(this);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Hide(); return true; }
            if (keyData == (Keys.Control | Keys.Enter) && phase == Phase.Answer && canInsert) { if (Accept != null) Accept(answer); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void OnInputKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Control) { e.SuppressKeyPress = true; Submit(input.Text); }
        }

        void Submit(string text)
        {
            if (phase == Phase.Working || string.IsNullOrWhiteSpace(text)) return;
            input.Text = "";
            if (Send != null) Send(text.Trim());
        }

        void Cue()
        {
            string cue = phase == Phase.Question ? "Answer…" : phase == Phase.Answer ? "Refine it: shorter, friendlier, in Hindi…"
                : selection != null ? "What should Prosper do with it?" : "Ask Prosper to write anything…";
            if (input.IsHandleCreated) SendMessage(input.Handle, EM_SETCUEBANNER, (IntPtr)1, cue);
        }

        // ---- what the App calls ----

        /// <summary>Opens fresh for a new request about [selected] (null: nothing selected) in [app].</summary>
        public void Open(string selected, string app, bool editable, Rectangle barPill)
        {
            selection = string.IsNullOrWhiteSpace(selected) ? null : selected;
            canInsert = editable || selection != null;
            anchor = barPill; answer = ""; question = null; error = false; step = null;
            int words = selection == null ? 0 : selection.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
            context = (selection != null ? "Selection · " + words + (words == 1 ? " word" : " words") : "No selection") + (string.IsNullOrEmpty(app) ? "" : " · " + Short(app, 22));
            preview.Text = selection == null ? "" : "“" + Short(System.Text.RegularExpressions.Regex.Replace(selection, "\\s+", " ").Trim(), 120) + "”";
            accept.Text = selection != null ? "Replace selection" : "Insert";
            input.Text = "";
            Set(Phase.Ready);
            Present();
        }

        /// <summary>Opens with a question the voice Assistant asked, to answer by typing.</summary>
        public void OpenWithQuestion(string question, string app, bool editable, bool hadSelection, Rectangle barPill)
        {
            selection = null; canInsert = editable || hadSelection; anchor = barPill; answer = ""; error = false;
            context = "Voice request" + (string.IsNullOrEmpty(app) ? "" : " · " + Short(app, 22));
            accept.Text = hadSelection ? "Replace selection" : "Insert";
            Question(question);
            Present();
        }

        public void Working(string what) { step = what; error = false; Set(Phase.Working); }

        public void Question(string text)
        {
            question = text;
            ShowHtml("<div class='lbl'>PROSPER ASKS</div>" + Markdown.Html(text));
            step = null; Set(Phase.Question);
        }

        public void Answer(string text, bool status)
        {
            answer = text;
            ShowHtml((status ? "<div class='lbl'>RESULT</div>" : "") + Markdown.Html(text));
            showAccept = canInsert && !status; accept.Visible = showAccept;
            step = null; Set(Phase.Answer);
        }

        public void Fail(string message)
        {
            error = true;
            Set(answer.Length > 0 ? Phase.Answer : question != null ? Phase.Question : Phase.Ready);
            foot.Text = message; foot.ForeColor = Style.Bad;
        }

        void Present()
        {
            if (!Visible) Show();
            Activate(); input.Focus();
        }

        void ShowHtml(string inner)
        {
            // Tighter than the History page, the box's own colours, and dark scrollbars.
            string css = "<style>html{scrollbar-face-color:#3F3F46;scrollbar-track-color:#18181B;scrollbar-arrow-color:#A1A1AA;" +
                "scrollbar-shadow-color:#18181B;scrollbar-highlight-color:#18181B;scrollbar-darkshadow-color:#18181B;scrollbar-3dlight-color:#18181B}" +
                "body{margin:10px 14px;background:#18181B;font-size:13px}.lbl{margin-bottom:4px}</style>";
            Browser.Load(web, Markdown.Page(css + inner));
        }

        void Set(Phase p)
        {
            phase = p;
            input.ReadOnly = p == Phase.Working;
            if (p == Phase.Working) spin.Start(); else spin.Stop();
            if (!error)
            {
                foot.ForeColor = p == Phase.Working ? Style.Gold : Style.Faint;
                foot.Text = p == Phase.Working ? (step ?? "Thinking") + "…"
                    : p == Phase.Answer ? (showAccept ? "Copied · Ctrl+Enter to " + (selection != null || accept.Text != "Insert" ? "replace" : "insert") : "Copied")
                    : p == Phase.Question ? "Enter to answer · Esc to close" : "Enter to send · Esc to close";
            }
            Cue();
            Arrange();
        }

        void Arrange()
        {
            SuspendLayout();
            int inner = W - 2 * Pad, y = Style.Px(14);
            y += Style.Px(32);
            // Local flags: Visible reads false until the box is on screen, and this runs before it is.
            bool showPreview = selection != null && phase != Phase.Answer && phase != Phase.Question;
            bool showWeb = phase == Phase.Question || phase == Phase.Answer || (phase == Phase.Working && (answer.Length > 0 || question != null));
            bool showChips = phase == Phase.Ready && selection != null, showActions = phase == Phase.Answer;
            preview.Visible = showPreview; web.Visible = showWeb; chips.Visible = showChips; actions.Visible = showActions;
            if (showPreview) { preview.SetBounds(Pad, y, inner, Style.Px(18)); y += Style.Px(24); }
            if (showWeb)
            {
                int h = phase == Phase.Question ? Style.Px(92) : Style.Px(230);
                web.SetBounds(Pad, y, inner, h); y += h + Style.Px(10);
            }
            if (showChips) { chips.MaximumSize = new Size(inner, 0); chips.Location = new Point(Pad, y); y += chips.PreferredSize.Height + Style.Px(4); }
            field.SetBounds(Pad, y, inner, field.Height); y += field.Height + Style.Px(8);
            int fh = Style.Px(34);
            if (showActions) actions.Location = new Point(W - Pad - actions.PreferredSize.Width + Style.Px(6), y);
            int left = Pad + (phase == Phase.Working ? Style.Px(22) : 0);
            foot.SetBounds(left, y, W - Pad - left - (showActions ? actions.PreferredSize.Width + Style.Px(4) : 0), fh);
            y += fh + Style.Px(12);
            ClientSize = new Size(W, y);
            ResumeLayout();
            using (var path = Floating.Round(new RectangleF(0, 0, Width, Height), Style.Px(14))) Region = new Region(path);
            Place();
            Invalidate();
        }

        /// <summary>Centred just above the bar (below it when there's no room), inside the screen.</summary>
        void Place()
        {
            if (anchor.IsEmpty) return;
            var work = Screen.FromRectangle(anchor).WorkingArea;
            int gap = Style.Px(10);
            int x = anchor.Left + (anchor.Width - Width) / 2;
            int y = anchor.Top - gap - Height >= work.Top ? anchor.Top - gap - Height : anchor.Bottom + gap;
            Location = new Point(Math.Max(work.Left, Math.Min(x, work.Right - Width)), Math.Max(work.Top, Math.Min(y, work.Bottom - Height)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Style.Bg);
            using (var p = Floating.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Style.Px(14)))
            using (var pen = new Pen(Style.GoldEdge)) g.DrawPath(pen, p);
            float px = Floating.Dpi, cy = Style.Px(26);
            Bar.Sparkle(g, Pad + 7 * px, cy, 7 * px, Style.Gold);
            TextRenderer.DrawText(g, "Ask Prosper", Style.Strong, new Point(Pad + Style.Px(20), (int)(cy - Style.Strong.Height / 2f) - 1), Style.Text);
            if (context.Length > 0)
            {
                var size = TextRenderer.MeasureText(context, Style.Small);
                var chip = new RectangleF(Width - Pad - size.Width - Style.Px(16), cy - Style.Px(11), size.Width + Style.Px(16), Style.Px(22));
                using (var p = Floating.Round(chip, chip.Height / 2)) using (var b = new SolidBrush(Style.Raised)) g.FillPath(b, p);
                TextRenderer.DrawText(g, context, Style.Small, Rectangle.Round(chip), Style.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            if (phase == Phase.Working)
            {
                float d = 12 * px, x = Pad + 2 * px, y = foot.Top + foot.Height / 2f - d / 2;
                using (var track = new Pen(Color.FromArgb(60, Style.Gold), 2f * px)) g.DrawEllipse(track, x, y, d, d);
                using (var arc = new Pen(Style.Gold, 2f * px) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arc, x, y, d, d, angle, 100);
            }
        }

        static string Short(string s, int n) { return s.Length > n ? s.Substring(0, n - 1) + "…" : s; }
    }
}
