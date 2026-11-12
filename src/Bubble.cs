// The floating bubble and the small windows beside it. None of them take the keyboard focus away from the app the
// user is typing in, so text can be pasted back into it.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Prosper
{
    /// <summary>A topmost window that never becomes the active window, even when clicked.</summary>
    class Floating : Form
    {
        public static float Dpi = 1f; // screen DPI / 96
        public static int Px(float dp) { return (int)Math.Round(dp * Dpi); }

        public Floating()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST; return cp; }
        }
        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath(); int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        /// <summary>Beside [anchor], on whichever side has room, inside the screen's work area.</summary>
        public void PlaceBeside(Rectangle anchor, int gap)
        {
            var work = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Right + gap + Width <= work.Right ? anchor.Right + gap : anchor.Left - gap - Width;
            int y = anchor.Top + (anchor.Height - Height) / 2;
            Location = new Point(Math.Max(work.Left, Math.Min(x, work.Right - Width)), Math.Max(work.Top, Math.Min(y, work.Bottom - Height)));
        }
    }

    enum BubbleState { Idle, Dictating, Assisting, Working }

    class Bubble : Floating
    {
        public static readonly Color Ink = Color.FromArgb(17, 17, 19), Gold = Color.FromArgb(232, 194, 106),
            Emerald = Color.FromArgb(15, 107, 79), Red = Color.FromArgb(220, 38, 38);

        public event Action Tap, DoubleTap, Hold, ShowMenu;
        BubbleState state;
        float spin;
        readonly Timer anim = new Timer { Interval = 40 };
        readonly Timer clickWait = new Timer();
        readonly Timer holdWait = new Timer { Interval = 600 };
        Point down, origin; bool dragging, held, pending;

        public Bubble()
        {
            int size = Px(52);
            Size = new Size(size, size);
            BackColor = Color.FromArgb(1, 2, 1); TransparencyKey = BackColor; // only the disc shows
            clickWait.Interval = SystemInformation.DoubleClickTime;
            clickWait.Tick += (s, e) => { clickWait.Stop(); pending = false; if (Tap != null) Tap(); };
            holdWait.Tick += (s, e) => { holdWait.Stop(); if (!dragging) { held = true; if (Hold != null) Hold(); } };
            anim.Tick += (s, e) => { spin = (spin + 12) % 360; Invalidate(); };
            var tip = new ToolTip();
            tip.SetToolTip(this, "Prosper Voice\nClick: dictate (Ctrl+Alt+Space)\nDouble-click: Assistant (Ctrl+Alt+A)\nHold: history (Ctrl+Alt+H)\nRight-click: menu");
        }

        public BubbleState State
        {
            get { return state; }
            set { state = value; if (value == BubbleState.Working) anim.Start(); else anim.Stop(); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Px(3); var r = new Rectangle(pad, pad, Width - 2 * pad - 1, Height - 2 * pad - 1);
            Color fill = state == BubbleState.Dictating ? Red : state == BubbleState.Assisting ? Emerald : Ink;
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, r);
            using (var p = new Pen(Color.FromArgb(state == BubbleState.Idle ? 110 : 0, Gold), Px(1.5f))) g.DrawEllipse(p, r);
            float cx = Width / 2f, cy = Height / 2f;
            if (state == BubbleState.Working)
            {
                using (var p = new Pen(Gold, Px(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, cx - Px(11), cy - Px(11), Px(22), Px(22), spin, 270);
                return;
            }
            if (state == BubbleState.Assisting) { Sparkle(g, cx, cy, Px(11), Gold); return; }
            // Sound bars: gold when idle, white while dictating.
            float[] heights = { 8, 16, 22, 14, 8 };
            using (var p = new Pen(state == BubbleState.Dictating ? Color.White : Gold, Px(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i < heights.Length; i++)
                {
                    float x = cx + (i - 2) * Px(5.5f), h = Px(heights[i]) / 2f;
                    g.DrawLine(p, x, cy - h, x, cy + h);
                }
        }

        public static void Sparkle(Graphics g, float cx, float cy, float r, Color c)
        {
            using (var path = new GraphicsPath())
            {
                float k = r * 0.28f;
                path.AddPolygon(new[] {
                    new PointF(cx, cy - r), new PointF(cx + k, cy - k), new PointF(cx + r, cy), new PointF(cx + k, cy + k),
                    new PointF(cx, cy + r), new PointF(cx - k, cy + k), new PointF(cx - r, cy), new PointF(cx - k, cy - k) });
                using (var b = new SolidBrush(c)) g.FillPath(b, path);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { if (ShowMenu != null) ShowMenu(); return; }
            if (e.Button != MouseButtons.Left) return;
            down = Cursor.Position; origin = Location; dragging = false; held = false;
            if (state == BubbleState.Idle) holdWait.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var p = Cursor.Position;
            if (!dragging && (Math.Abs(p.X - down.X) > Px(4) || Math.Abs(p.Y - down.Y) > Px(4))) { dragging = true; holdWait.Stop(); }
            if (dragging) Location = new Point(origin.X + p.X - down.X, origin.Y + p.Y - down.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            holdWait.Stop();
            if (dragging)
            {
                var work = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)), Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
                Settings.Current.BubbleX = Left; Settings.Current.BubbleY = Top; Settings.Save();
                return;
            }
            if (held) return;
            if (state != BubbleState.Idle) { if (Tap != null) Tap(); return; } // stop, at once
            if (pending) { clickWait.Stop(); pending = false; if (DoubleTap != null) DoubleTap(); return; }
            pending = true; clickWait.Start(); // a second click within the double-click time is a double-click
        }

        public void PlaceSaved()
        {
            var s = Settings.Current;
            var work = Screen.PrimaryScreen.WorkingArea;
            var p = s.BubbleX >= 0 ? new Point(s.BubbleX, s.BubbleY) : new Point(work.Right - Width - Px(24), work.Top + work.Height / 2);
            bool visible = false;
            foreach (var sc in Screen.AllScreens) if (sc.WorkingArea.Contains(new Rectangle(p, Size))) visible = true;
            Location = visible ? p : new Point(work.Right - Width - Px(24), work.Top + work.Height / 2);
        }
    }

    /// <summary>A small pill beside the bubble: what is happening now ("Transcribing", "✓ Copied"). Clicks go through it.</summary>
    class StatusChip : Floating
    {
        readonly Label text = new Label { AutoSize = true, ForeColor = Color.FromArgb(244, 244, 245), BackColor = Bubble.Ink, Font = new Font("Segoe UI", 9f) };
        readonly Timer hide = new Timer();

        public StatusChip()
        {
            BackColor = Bubble.Ink; Padding = new Padding(Px(10), Px(5), Px(10), Px(5));
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            text.MaximumSize = new Size(Px(300), 0);
            Controls.Add(text);
            hide.Tick += (s, e) => { hide.Stop(); Hide(); };
        }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } // WS_EX_TRANSPARENT: clicks pass through
        }

        public void Say(string message, Rectangle anchor, int hideAfterMs)
        {
            hide.Stop();
            text.Text = message;
            PerformLayout();
            Region = new Region(Round(new Rectangle(0, 0, Width, Height), Px(10)));
            PlaceBeside(anchor, Px(6));
            if (!Visible) Show();
            if (hideAfterMs > 0) { hide.Interval = hideAfterMs; hide.Start(); }
        }
    }

    /// <summary>"📷 Add screen", beside the bubble while the Assistant listens: the screen goes with the request only if clicked.</summary>
    class ShotButton : Floating
    {
        public event Action Toggle;
        bool added;
        string label = "📷 Add screen";

        public ShotButton()
        {
            Size = new Size(Px(124), Px(28));
            Font = new Font("Segoe UI Emoji", 9f);
            Cursor = Cursors.Hand;
            Region = new Region(Round(new Rectangle(0, 0, Width, Height), Px(13)));
        }

        public bool Added { get { return added; } }
        public void Set(bool on, string message)
        {
            added = on; label = message ?? (on ? "✓ Screen added" : "📷 Add screen");
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(added ? Bubble.Gold : Bubble.Ink);
            if (!added) using (var p = new Pen(Color.FromArgb(128, Bubble.Gold), Px(1))) g.DrawPath(p, Round(new Rectangle(0, 0, Width - 1, Height - 1), Px(13)));
            TextRenderer.DrawText(g, label, Font, ClientRectangle, added ? Bubble.Ink : Color.FromArgb(244, 244, 245),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Toggle != null) Toggle(); }
    }

    /// <summary>The Assistant's answer when it wasn't typed into a field: rendered, with Insert / Copy / Open chat.</summary>
    class AnswerCard : Floating
    {
        public event Action Insert, OpenChat;
        readonly WebBrowser web = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, AllowWebBrowserDrop = false, IsWebBrowserContextMenuEnabled = true };
        string answer = "";

        public AnswerCard()
        {
            Size = new Size(Px(460), Px(380));
            BackColor = Color.FromArgb(232, 194, 106); Padding = new Padding(1);
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(250, 250, 249) };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Px(40), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Px(6)), BackColor = Color.White };
            bar.Controls.Add(Btn("Close", (s, e) => Hide()));
            bar.Controls.Add(Btn("Open chat", (s, e) => { Hide(); if (OpenChat != null) OpenChat(); }));
            bar.Controls.Add(Btn("Copy", (s, e) => { Clip.Copy(answer); }));
            bar.Controls.Add(Btn("Insert", (s, e) => { Hide(); if (Insert != null) Insert(); }));
            web.Navigating += Browser.OpenLinksOutside;
            inner.Controls.Add(web); inner.Controls.Add(bar);
            Controls.Add(inner);
        }

        Button Btn(string t, EventHandler click)
        {
            var b = new Button { Text = t, AutoSize = true, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f), Margin = new Padding(Px(3), 0, 0, 0) };
            b.FlatAppearance.BorderColor = Color.FromArgb(212, 212, 216); b.Click += click;
            return b;
        }

        public string Answer { get { return answer; } }

        public void Show(string text, bool status, Rectangle anchor)
        {
            answer = text;
            Browser.Load(web, Markdown.Page((status ? "<div class='lbl'>RESULT</div>" : "") + Markdown.Html(text)));
            PlaceBeside(anchor, Px(8));
            if (!Visible) Show();
        }
    }

    static class Browser
    {
        /// <summary>Shows [html] without remembering it anywhere (no history, no cache file of our own).</summary>
        public static void Load(WebBrowser web, string html)
        {
            web.DocumentText = html; // loads about:blank and writes the page itself; a Navigate first would race it
        }

        /// <summary>Links in answers open in the user's browser, never inside Prosper.</summary>
        public static void OpenLinksOutside(object sender, WebBrowserNavigatingEventArgs e)
        {
            var u = e.Url;
            if (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "mailto")
            {
                e.Cancel = true;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
            }
        }
    }

    /// <summary>Asks before an MCP tool runs: Allow once, always allow this tool, or Cancel.</summary>
    class ApprovalForm : Form
    {
        public Approval Result = Approval.Deny;

        public ApprovalForm(string tool, string what, string args)
        {
            Text = "Allow this action?"; TopMost = true; StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Segoe UI", 9.5f); Size = new Size(Floating.Px(520), Floating.Px(360));
            var head = new Label { Text = "The Assistant wants to run this through your MCP server:\n" + what, Dock = DockStyle.Top, Height = Floating.Px(52), Padding = new Padding(Floating.Px(10)) };
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f),
                Text = args.Length > 4000 ? args.Substring(0, 4000) + "…" : args };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Floating.Px(44), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Floating.Px(6)) };
            Action<string, Approval> add = (t, a) => { var b = new Button { Text = t, AutoSize = true }; b.Click += (s, e) => { Result = a; Close(); }; bar.Controls.Add(b); };
            add("Cancel", Approval.Deny);
            add("Always allow " + tool, Approval.Always);
            add("Allow", Approval.Allow);
            Controls.Add(box); Controls.Add(head); Controls.Add(bar);
            AcceptButton = null; CancelButton = (Button)bar.Controls[0];
        }
    }
}
