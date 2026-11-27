// Prosper Focus on screen: the timer on the taskbar (a small "● 38:12" label beside the tray icons, and the tray
// icon itself as a countdown ring), its controls, the cards (start, check-in, break warning), the time's up
// question and the full-screen break. Only "Focus" or "Break" and the time are ever shown here: never a task
// name, a goal or the plan, since colleagues can see this screen. Every window stays out of screen captures.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Prosper
{
    /// <summary>Per-pixel-alpha drawing and capture exclusion for Focus windows.</summary>
    static class Layered
    {
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; public POINT(int x, int y) { this.x = x; this.y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        public static void Push(Form f, Bitmap bmp)
        {
            if (!f.IsHandleCreated) return;
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hbmp);
                var size = new SIZE(bmp.Width, bmp.Height); var src = new POINT(0, 0); var at = new POINT(f.Left, f.Top);
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(f.Handle, screen, ref at, ref size, mem, ref src, 0, ref blend, 2);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                if (hbmp != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hbmp); }
                DeleteDC(mem);
            }
        }

        /// <summary>Windows 10 2004+: kept out of screenshots and screen shares (not in --preview).</summary>
        public static void Hide(Form f) { if (!Bar.AllowCapture) try { SetWindowDisplayAffinity(f.Handle, 0x11); } catch { } }
    }

    /// <summary>Where the taskbars are, and where the label goes on each: just left of the tray icons.</summary>
    static class Taskbars
    {
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        static Rectangle Rect(IntPtr h) { RECT r; return h != IntPtr.Zero && GetWindowRect(h, out r) ? Rectangle.FromLTRB(r.L, r.T, r.R, r.B) : Rectangle.Empty; }

        public class Spot { public Rectangle Bar, Tray; public bool Shown; }

        /// <summary>Every taskbar: the main one and one per extra monitor (they have no tray, but a clock).</summary>
        public static List<Spot> All()
        {
            var list = new List<Spot>();
            var main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) list.Add(Make(main, Rect(FindWindowEx(main, IntPtr.Zero, "TrayNotifyWnd", null))));
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(Make(h, Rectangle.Empty));
            return list;
        }

        static Spot Make(IntPtr h, Rectangle tray)
        {
            var bar = Rect(h);
            var s = new Spot { Bar = bar, Tray = tray };
            // Hidden: auto-hide slid it off the screen, or a full-screen app covers it.
            var screen = Screen.FromRectangle(bar).Bounds;
            var seen = Rectangle.Intersect(bar, screen);
            s.Shown = IsWindowVisible(h) && seen.Height >= bar.Height - 2 && seen.Width >= bar.Width - 2 && !FullScreenOn(screen);
            if (s.Tray.IsEmpty || s.Tray.Width <= 0) s.Tray = new Rectangle(bar.Right - Floating.Px(150), bar.Top, Floating.Px(150), bar.Height); // the clock on extra monitors
            return s;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);

        /// <summary>
        /// The window in front covers the whole monitor (a full-screen video, game or slide show). The desktop and
        /// Prosper's own windows don't count. (SHQueryUserNotificationState says "busy" too often to rely on.)
        /// </summary>
        static bool FullScreenOn(Rectangle screen)
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint pid; GetWindowThreadProcessId(fg, out pid);
            if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return false;
            // click-through overlays and tool windows (screen recorders, assistants) cover the screen but aren't apps
            if ((GetWindowLong(fg, -20) & (0x20 | 0x80)) != 0) return false; // WS_EX_TRANSPARENT, WS_EX_TOOLWINDOW
            var cls = new System.Text.StringBuilder(64); GetClassName(fg, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
            var r = Rect(fg);
            return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
        }
    }

    /// <summary>"● 38:12" on the taskbar, with a thin gold line for progress. Click it for the controls.</summary>
    class TaskbarLabel : Form
    {
        public event Action Clicked;
        public FocusView View;
        public Taskbars.Spot Spot;
        bool hover;
        readonly Font font = new Font("Segoe UI Semibold", 9.5f), chip = new Font("Segoe UI Semibold", 8f);

        public TaskbarLabel()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            Size = new Size(Floating.Px(120), Floating.Px(30));
            OnTop.Keep(this);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST; return cp; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE → MA_NOACTIVATE
            base.WndProc(ref m);
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Layered.Hide(this); Draw(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Draw(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Draw(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left && Clicked != null) Clicked(); base.OnMouseUp(e); }

        public static string Time(long ms)
        {
            long s = (ms + 999) / 1000;
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        string Text_()
        {
            var v = View; if (v == null) return "";
            if (v.Phase == FocusPhase.TimeUp) return "Focus · time's up";
            if (v.Phase == FocusPhase.Warning) return v.Waiting ? "Break waiting" : "Break in " + ((v.Left + 999) / 1000) + "s";
            string t = Time(v.Left);
            return v.WrapUp ? t + " · wrap up" : t;
        }

        /// <summary>Sizes and places the label for [spot], then draws it.</summary>
        public void Place()
        {
            var s = Spot; if (s == null || View == null) return;
            int w, h = Math.Max(Floating.Px(24), Math.Min(Floating.Px(30), s.Bar.Height - Floating.Px(12)));
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) w = (int)g.MeasureString(Text_(), font).Width + Floating.Px(View.Meeting ? 92 : 34);
            Size = new Size(w, h);
            if (!s.Shown)
            {
                // No taskbar to sit on (full screen, auto-hide): the same corner, just above the screen's edge.
                var sc = Screen.FromRectangle(s.Bar).Bounds;
                Location = new Point(sc.Right - w - Floating.Px(16), sc.Bottom - h - Floating.Px(10));
            }
            else if (s.Bar.Height > s.Bar.Width) // a taskbar on the side: above the tray
                Location = new Point(s.Bar.Left + (s.Bar.Width - w) / 2, s.Tray.Top - h - Floating.Px(8));
            else
                Location = new Point(s.Tray.Left - w - Floating.Px(8), s.Bar.Top + (s.Bar.Height - h) / 2);
            Draw();
        }

        public void Draw()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0 || View == null) return;
            using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var v = View;
                var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
                float rad = Height / 2f;
                bool gold = v.WrapUp || v.Phase == FocusPhase.Warning || v.Phase == FocusPhase.TimeUp;
                using (var path = Floating.Round(r, rad))
                {
                    using (var b = new SolidBrush(Color.FromArgb(hover ? 235 : 214, 24, 24, 27))) g.FillPath(b, path);
                    // the progress line along the bottom, clipped to the pill
                    if (v.Phase == FocusPhase.Running || v.Phase == FocusPhase.Break)
                    {
                        g.SetClip(path);
                        using (var b = new SolidBrush(Style.Gold)) g.FillRectangle(b, 0, Height - Floating.Px(2), (float)(Width * v.Progress), Floating.Px(2));
                        g.ResetClip();
                    }
                    using (var p = new Pen(gold ? Style.Gold : Color.FromArgb(77, 255, 255, 255), 1f)) g.DrawPath(p, path);
                }
                float x = Floating.Px(12), cy = Height / 2f;
                using (var b = new SolidBrush(Style.Gold)) g.FillEllipse(b, x, cy - Floating.Px(3), Floating.Px(6), Floating.Px(6));
                x += Floating.Px(12);
                string t = Text_();
                var size = g.MeasureString(t, font);
                using (var b = new SolidBrush(Style.Text)) g.DrawString(t, font, b, x, cy - size.Height / 2);
                x += size.Width + Floating.Px(6);
                if (v.Meeting)
                {
                    var chipR = new RectangleF(x, cy - Floating.Px(9), Floating.Px(58), Floating.Px(18));
                    using (var path = Floating.Round(chipR, Floating.Px(9))) using (var b = new SolidBrush(Style.Gold)) g.FillPath(b, path);
                    var cs = g.MeasureString("Meeting", chip);
                    using (var b = new SolidBrush(Style.Bg)) g.DrawString("Meeting", chip, b, chipR.X + (chipR.Width - cs.Width) / 2, cy - cs.Height / 2);
                }
                Layered.Push(this, bmp);
            }
        }
    }

    /// <summary>The tray icon while a block runs: a gold ring that empties, with the minutes left inside.</summary>
    static class FocusIcon
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public static Icon Ring(int size, FocusView v)
        {
            int s = size * 2; // drawn at twice the size, then scaled down: smoother at 16–24 px
            using (var bmp = new Bitmap(s, s))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float w = s / 9f; var r = new RectangleF(w / 2, w / 2, s - w, s - w);
                    using (var b = new SolidBrush(Color.FromArgb(240, 17, 17, 19))) g.FillEllipse(b, r);
                    using (var p = new Pen(Color.FromArgb(90, 255, 255, 255), w)) g.DrawEllipse(p, r);
                    float left = (float)(1 - v.Progress);
                    if (v.Phase == FocusPhase.TimeUp || v.Phase == FocusPhase.Warning) left = 1;
                    using (var p = new Pen(Style.Gold, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        if (left > 0.01f) g.DrawArc(p, r, -90, 360 * left);
                    string t = v.Phase == FocusPhase.TimeUp ? "✓" : v.Phase == FocusPhase.Warning ? "!" : Math.Max(1, (int)Math.Ceiling(v.Left / 60000.0)).ToString();
                    if (t.Length > 2) t = "99";
                    using (var f = new Font("Segoe UI Semibold", s * (t.Length > 1 ? 0.36f : 0.44f), GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(v.WrapUp ? Style.Gold : Color.White))
                    {
                        var m = g.MeasureString(t, f);
                        g.DrawString(t, f, b, (s - m.Width) / 2 + 1, (s - m.Height) / 2 + 1);
                    }
                }
                using (var small = new Bitmap(bmp, size, size))
                {
                    IntPtr h = small.GetHicon();
                    var icon = (Icon)Icon.FromHandle(h).Clone();
                    DestroyIcon(h);
                    return icon;
                }
            }
        }

        /// <summary>The tray tooltip: only the kind of block and the minutes.</summary>
        public static string Tip(FocusView v)
        {
            switch (v.Phase)
            {
                case FocusPhase.Running: return v.Label + " · " + Math.Max(1, (int)Math.Ceiling(v.Left / 60000.0)) + " min left" + (v.Meeting ? " · meeting" : "");
                case FocusPhase.Break: return "Break · " + Math.Max(1, (int)Math.Ceiling(v.Left / 60000.0)) + " min left";
                case FocusPhase.TimeUp: return "Focus · time's up";
                case FocusPhase.Warning: return "Break starting";
                default: return "Prosper Voice";
            }
        }
    }

    /// <summary>A card above the taskbar, at the bottom right: a title, a note and a row of buttons.</summary>
    class FocusCard : Floating
    {
        readonly Panel inner = new Panel { Dock = DockStyle.Fill, BackColor = Style.Surface };
        readonly Label title = new Label { AutoSize = true, Font = Style.Strong, ForeColor = Style.Text };
        readonly Label big = new Label { AutoSize = true, Font = new Font("Segoe UI Light", 22f), ForeColor = Style.Gold };
        readonly Label note = new Label { AutoSize = true, Font = Style.Small, ForeColor = Style.Muted };
        readonly FlowLayoutPanel buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Style.Surface, Margin = new Padding(0) };
        public string Kind;

        public FocusCard()
        {
            BackColor = Style.Line; Padding = new Padding(1);
            Controls.Add(inner);
            inner.Controls.Add(big); inner.Controls.Add(title); inner.Controls.Add(note); inner.Controls.Add(buttons);
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); Layered.Hide(this);
            Region = new Region(Round(new RectangleF(0, 0, Width, Height), Px(14)));
        }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated) Region = new Region(Round(new RectangleF(0, 0, Width, Height), Px(14)));
        }

        /// <summary>Fills the card. [actions] are label/handler pairs; the first is the main (gold) one when [primary].</summary>
        public void Set(string kind, string heading, string bigText, string noteText, bool goldEdge, bool primary, params object[] actions)
        {
            Kind = kind;
            BackColor = goldEdge ? Style.GoldEdge : Style.Line;
            int pad = Px(16), width = Px(360);
            bool hasBig = !string.IsNullOrEmpty(bigText), hasNote = !string.IsNullOrEmpty(noteText); // Visible reads false until the card is shown
            big.Visible = hasBig; big.Text = bigText ?? "";
            title.Text = heading; title.MaximumSize = new Size(width - 2 * pad - (hasBig ? TextRenderer.MeasureText("88", big.Font).Width + Px(6) : 0), 0);
            note.Text = noteText ?? ""; note.Visible = hasNote; note.MaximumSize = new Size(width - 2 * pad, 0);
            buttons.Controls.Clear();
            for (int i = 0; i + 1 < actions.Length; i += 2)
            {
                var b = Style.Btn((string)actions[i], primary && i == 0);
                var act = (Action)actions[i + 1];
                b.Click += (o, e) => act();
                b.Margin = new Padding(0, 0, Px(8), 0);
                buttons.Controls.Add(b);
            }
            int y = pad;
            // room for two digits whatever the count is, so the title doesn't move as it counts down
            int bigW = TextRenderer.MeasureText("88", big.Font).Width;
            if (hasBig) { big.Location = new Point(pad - Px(4), y - Px(6)); title.Location = new Point(pad + bigW + Px(6), y + Px(10)); y += Math.Max(big.PreferredHeight - Px(6), title.PreferredHeight + Px(10)) + Px(10); }
            else { title.Location = new Point(pad, y); y += title.PreferredHeight + Px(10); }
            if (buttons.Controls.Count > 0) { buttons.Location = new Point(pad, y); y += buttons.PreferredSize.Height + Px(10); }
            if (hasNote) { note.Location = new Point(pad, y); y += note.PreferredHeight + Px(4); }
            Size = new Size(Math.Max(width, buttons.PreferredSize.Width + 2 * pad), y + pad - Px(6));
        }

        public void SetBig(string text) { if (big.Text != text) big.Text = text; }

        /// <summary>Bottom right of the screen with the taskbar label, above the taskbar.</summary>
        public void PlaceCorner(Rectangle near)
        {
            var work = Screen.FromRectangle(near).WorkingArea;
            Location = new Point(work.Right - Width - Px(16), work.Bottom - Height - Px(12));
        }
    }

    /// <summary>The controls: ● Focus 38:12 [−5] [+5] [Meeting] [✓]. It closes itself when the mouse has left it a while.</summary>
    class FocusFlyout : Floating
    {
        public event Action Less, More, Meeting, Done;
        public FocusView View;
        readonly Timer idle = new Timer { Interval = 6000 };
        RectangleF less, more, meet, done;
        readonly Font font = new Font("Segoe UI Semibold", 9.5f), small = new Font("Segoe UI", 9f);
        string hot;

        public FocusFlyout()
        {
            Size = new Size(Px(400), Px(44)); BackColor = Color.FromArgb(24, 24, 27);
            idle.Tick += (s, e) => { if (!Bounds.Contains(Cursor.Position)) { idle.Stop(); Hide(); } };
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); Layered.Hide(this);
            Region = new Region(Round(new RectangleF(0, 0, Width, Height), Height / 2f));
        }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) { idle.Stop(); idle.Start(); } }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            string h = less.Contains(e.Location) ? "less" : more.Contains(e.Location) ? "more" : meet.Contains(e.Location) ? "meet" : done.Contains(e.Location) ? "done" : null;
            if (h != hot) { hot = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
            idle.Stop(); idle.Start();
        }
        protected override void OnMouseLeave(EventArgs e) { hot = null; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (less.Contains(e.Location)) { if (Less != null) Less(); }
            else if (more.Contains(e.Location)) { if (More != null) More(); }
            else if (meet.Contains(e.Location)) { if (Meeting != null) Meeting(); }
            else if (done.Contains(e.Location)) { if (Done != null) Done(); }
            else Hide(); // the label: collapse
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; var v = View; if (v == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var p = new Pen(v.WrapUp ? Style.Gold : Style.Line)) using (var path = Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Height / 2f)) g.DrawPath(p, path);
            float x = Px(16), cy = Height / 2f;
            using (var b = new SolidBrush(Style.Gold)) g.FillEllipse(b, x, cy - Px(3), Px(6), Px(6));
            x += Px(14);
            var ls = g.MeasureString(v.Label, font);
            using (var b = new SolidBrush(Style.Text)) g.DrawString(v.Label, font, b, x, cy - ls.Height / 2);
            x += ls.Width + Px(4);
            string t = TaskbarLabel.Time(v.Left);
            var ts = g.MeasureString(t, font);
            using (var b = new SolidBrush(Style.Gold)) g.DrawString(t, font, b, x, cy - ts.Height / 2);
            // buttons from the right
            float h = Px(30), right = Width - Px(7);
            done = new RectangleF(right - h, cy - h / 2, h, h); right = done.Left - Px(6);
            meet = Pill(g, "Meeting", ref right, h, cy);
            more = Pill(g, "+5", ref right, h, cy);
            less = Pill(g, "−5", ref right, h, cy);
            Chip(g, less, "−5", false); Chip(g, more, "+5", false); Chip(g, meet, "Meeting", v.Meeting);
            using (var b = new SolidBrush(hot == "done" ? Color.FromArgb(242, 210, 138) : Style.Gold)) g.FillEllipse(b, done);
            using (var p = new Pen(Style.Bg, Px(2.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, new[] { new PointF(done.X + h * 0.3f, cy), new PointF(done.X + h * 0.45f, cy + h * 0.15f), new PointF(done.X + h * 0.72f, cy - h * 0.15f) });
        }

        RectangleF Pill(Graphics g, string text, ref float right, float h, float cy)
        {
            float w = g.MeasureString(text, small).Width + Px(18);
            var r = new RectangleF(right - w, cy - h / 2, w, h); right = r.Left - Px(6);
            return r;
        }

        void Chip(Graphics g, RectangleF r, string text, bool on)
        {
            string key = text == "−5" ? "less" : text == "+5" ? "more" : "meet";
            var fill = on ? Style.Gold : hot == key ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(31, 255, 255, 255);
            using (var path = Round(r, r.Height / 2)) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            var s = g.MeasureString(text, small);
            using (var b = new SolidBrush(on ? Style.Bg : Style.Text)) g.DrawString(text, small, b, r.X + (r.Width - s.Width) / 2, r.Y + (r.Height - s.Height) / 2);
        }
    }

    /// <summary>A dark veil over one monitor, behind the time's up card or as the break screen itself.</summary>
    class Veil : Floating
    {
        public bool IsBreak;
        public bool Main;          // the monitor with the Skip link
        public FocusView View;
        public event Action Skip;
        RectangleF skipRect;
        readonly Font caps = new Font("Segoe UI Semibold", 10f), count = new Font("Segoe UI Light", 60f), line = new Font("Segoe UI", 15f), link = new Font("Segoe UI", 10f);
        bool skipHot;

        public Veil(Screen s, bool isBreak)
        {
            IsBreak = isBreak; Bounds = s.Bounds; BackColor = Color.FromArgb(10, 10, 11);
            Opacity = 0; Cursor = Cursors.Default;
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Layered.Hide(this); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = IsBreak && Main && skipRect.Contains(e.Location);
            if (h != skipHot) { skipHot = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (IsBreak && Main && e.Button == MouseButtons.Left && skipRect.Contains(e.Location) && Skip != null) Skip(); // its one button
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!IsBreak || View == null) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float d = Px(300), cx = Width / 2f, cy = Height / 2f - Px(30);
            var ring = new RectangleF(cx - d / 2, cy - d / 2, d, d);
            using (var p = new Pen(Style.Raised, Px(2))) g.DrawEllipse(p, ring);
            float left = (float)(1 - View.Progress);
            using (var p = new Pen(Style.Gold, Px(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) if (left > 0.002f) g.DrawArc(p, ring, -90, 360 * left);
            Center(g, "BREAK", caps, Style.Muted, cy - Px(62));
            Center(g, TaskbarLabel.Time(View.Left), count, Color.FromArgb(250, 250, 250), cy + Px(6));
            Center(g, "Stand up. Walk. Water.", line, Color.FromArgb(228, 228, 231), ring.Bottom + Px(38));
            if (Main)
            {
                string s = "Skip this break";
                var m = g.MeasureString(s, link);
                skipRect = new RectangleF(cx - m.Width / 2 - Px(12), ring.Bottom + Px(98), m.Width + Px(24), m.Height + Px(14));
                using (var b = new SolidBrush(skipHot ? Style.Text : Style.Muted)) g.DrawString(s, link, b, cx - m.Width / 2, skipRect.Y + Px(7));
            }
        }

        static void Center(Graphics g, string s, Font f, Color c, float cy)
        {
            var m = g.MeasureString(s, f);
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, (g.VisibleClipBounds.Width - m.Width) / 2, cy - m.Height / 2);
        }
    }

    /// <summary>The time's up question, centred over the dimmed screen.</summary>
    class TimeUpCard : Floating
    {
        public event Action<string> Answer;
        readonly Panel inner = new Panel { Dock = DockStyle.Fill, BackColor = Style.Surface };
        readonly Label kind = new Label { AutoSize = true, Font = Style.Small, ForeColor = Style.Muted };
        readonly Label q = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), ForeColor = Style.Text, Text = "Time's up. Did you reach your goal?" };
        readonly Button yes = Style.Btn("Yes, done", true), extend = Style.Btn("+15 min (once)"), park = Style.Btn("Park it");

        public TimeUpCard()
        {
            BackColor = Style.Line; Padding = new Padding(1);
            Controls.Add(inner);
            inner.Controls.AddRange(new Control[] { kind, q, yes, extend, park });
            yes.Click += (o, e) => { if (Answer != null) Answer("done"); };
            extend.Click += (o, e) => { if (Answer != null) Answer("extend"); };
            park.Click += (o, e) => { if (Answer != null) Answer("park"); };
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); Layered.Hide(this);
            Region = new Region(Round(new RectangleF(0, 0, Width, Height), Px(18)));
        }

        public void Set(FocusView v, int extendMin, Screen on)
        {
            int pad = Px(26), w = Px(440), inW = w - 2 * pad;
            kind.Text = "Focus · " + v.Minutes + " min"; kind.Location = new Point(pad, pad);
            q.MaximumSize = new Size(inW, 0); q.Location = new Point(pad, kind.Bottom + Px(8));
            yes.Size = new Size(inW, Px(42)); yes.Location = new Point(pad, q.Bottom + Px(16));
            extend.Text = "+" + extendMin + " min (once)";
            extend.Visible = v.CanExtend;
            int half = (inW - Px(8)) / 2;
            if (v.CanExtend) { extend.Size = new Size(half, Px(40)); extend.Location = new Point(pad, yes.Bottom + Px(8)); park.Size = new Size(half, Px(40)); park.Location = new Point(pad + half + Px(8), yes.Bottom + Px(8)); }
            else { park.Size = new Size(inW, Px(40)); park.Location = new Point(pad, yes.Bottom + Px(8)); }
            Size = new Size(w, park.Bottom + pad);
            var b = on.Bounds;
            Location = new Point(b.Left + (b.Width - Width) / 2, b.Top + (b.Height - Height) / 2);
            if (IsHandleCreated) Region = new Region(Round(new RectangleF(0, 0, Width, Height), Px(18)));
        }
    }

    /// <summary>Skip a break: a short reason, optional. A normal window, so it can take typing.</summary>
    class SkipBox : Form
    {
        public string Reason { get { return box.Text.Trim(); } }
        readonly TextBox box = new TextBox();

        public SkipBox()
        {
            Text = "Skip this break"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = false;
            ClientSize = new Size(Style.Px(380), Style.Px(168));
            var t = new Label { Text = "Skip this break?", Font = Style.Heading, ForeColor = Style.Text, AutoSize = true, Location = new Point(Style.Px(20), Style.Px(16)) };
            var n = new Label { Text = "Why? Optional, kept with the break so the review can show it.", Font = Style.Small, ForeColor = Style.Muted, AutoSize = true, Location = new Point(Style.Px(20), Style.Px(46)) };
            var field = new FieldBox(box, Style.Px(340)) { Location = new Point(Style.Px(20), Style.Px(70)) };
            var skip = Style.Btn("Skip break", true); var keep = Style.Btn("Keep resting");
            skip.DialogResult = DialogResult.OK; keep.DialogResult = DialogResult.Cancel;
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Style.Bg, Location = new Point(Style.Px(20), Style.Px(118)) };
            skip.Margin = new Padding(0, 0, Style.Px(8), 0);
            row.Controls.Add(skip); row.Controls.Add(keep);
            Controls.AddRange(new Control[] { t, n, field, row });
            AcceptButton = skip; CancelButton = keep;
            Style.Window(this);
            OnTop.Keep(this); // above the break screen, which is kept on top too
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Layered.Hide(this); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); box.Focus(); }
    }

    /// <summary>
    /// Runs Focus: the engine, the sync with Notion, and every Focus window. One per app. It reads the clock every
    /// second; nothing here keeps time by itself, so sleep, restarts and dropped networks change nothing.
    /// </summary>
    class FocusController : IDisposable
    {
        readonly NotifyIcon tray;
        readonly Icon idleIcon;
        public readonly FocusEngine Engine;
        readonly Timer tick = new Timer { Interval = 1000 };
        readonly List<TaskbarLabel> labels = new List<TaskbarLabel>();
        readonly FocusFlyout flyout = new FocusFlyout();
        readonly FocusCard card = new FocusCard();
        readonly List<Veil> veils = new List<Veil>();
        TimeUpCard timeUp;
        SkipBox skipBox;
        long lastSync, nextSync, lastIcon; int failures; bool syncing, iconIsRing;
        long cardHiddenUntil;
        public string Status = "Not connected";
        public event Action StatusChanged;
        public bool Demo;

        Settings S { get { return Settings.Current; } }
        public bool On { get { return S.FocusOn || Demo; } }

        public FocusController(NotifyIcon tray, FocusData data)
        {
            this.tray = tray; idleIcon = tray.Icon;
            Engine = new FocusEngine(data);
            Engine.Changed += k => { if (!Demo) { data.Save(); SyncSoon(1500); } };
            tick.Tick += (s, e) => Tick();
            flyout.Less += () => { Engine.Adjust(-5); Tick(); };
            flyout.More += () => { Engine.Adjust(5); Tick(); };
            flyout.Meeting += () => { Engine.ToggleMeeting(); Tick(); };
            flyout.Done += () => { flyout.Hide(); Engine.EndNow(); Tick(); };
            SystemEvents.PowerModeChanged += Power;
            SystemEvents.SessionSwitch += Session;
            SystemEvents.DisplaySettingsChanged += Display;
        }

        public void Start() { tick.Start(); SyncSoon(500); Tick(); }

        void Power(object s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) { SyncSoon(3000); Tick(); } }
        void Session(object s, SessionSwitchEventArgs e) { if (e.Reason == SessionSwitchReason.SessionUnlock) { SyncSoon(1000); Tick(); } }
        void Display(object s, EventArgs e) { CloseVeils(); Tick(); }

        public void SyncSoon(int ms) { long t = Iso.Ms(DateTime.UtcNow) + ms; if (nextSync == 0 || t < nextSync) nextSync = t; }

        // ---- every second ----

        void Tick()
        {
            if (!On) { HideAll(); return; }
            Engine.Tick();
            var v = Engine.View();
            ShowLabel(v);
            ShowTray(v);
            ShowCard(v);
            ShowTimeUp(v);
            ShowBreak(v);
            if (flyout.Visible) { if (v.Phase == FocusPhase.Running || v.Phase == FocusPhase.Break) { flyout.View = v; flyout.Invalidate(); } else flyout.Hide(); }
            if (!Demo) MaybeSync(v);
        }

        void HideAll()
        {
            foreach (var l in labels) l.Hide();
            flyout.Hide(); card.Hide(); CloseVeils();
            if (iconIsRing) { tray.Icon = idleIcon; tray.Text = "Prosper Voice"; iconIsRing = false; }
        }

        static bool Timed(FocusView v) { return v.Phase == FocusPhase.Running || v.Phase == FocusPhase.Break || v.Phase == FocusPhase.TimeUp || v.Phase == FocusPhase.Warning; }

        void ShowLabel(FocusView v)
        {
            bool show = Timed(v) && S.TaskbarLabel;
            if (!show) { foreach (var l in labels) l.Hide(); return; }
            var spots = Taskbars.All();
            if (spots.Count == 0) spots.Add(new Taskbars.Spot { Bar = Screen.PrimaryScreen.Bounds, Tray = Screen.PrimaryScreen.Bounds, Shown = false });
            while (labels.Count < spots.Count)
            {
                var l = new TaskbarLabel();
                l.Clicked += ToggleFlyout;
                labels.Add(l);
            }
            for (int i = 0; i < labels.Count; i++)
            {
                if (i >= spots.Count) { labels[i].Hide(); continue; }
                labels[i].View = v; labels[i].Spot = spots[i];
                if (!labels[i].Visible) labels[i].Show();
                labels[i].Place();
            }
        }

        void ShowTray(FocusView v)
        {
            long now = Iso.Ms(DateTime.UtcNow);
            if (!Timed(v)) { if (iconIsRing) { tray.Icon = idleIcon; tray.Text = "Prosper Voice"; iconIsRing = false; } return; }
            // A new ring every 15 s is plenty for minutes; straight away when the state changes.
            string tip = FocusIcon.Tip(v);
            if (iconIsRing && now - lastIcon < 15000 && tray.Text == tip) return;
            var old = iconIsRing ? tray.Icon : null;
            tray.Icon = FocusIcon.Ring(SystemInformation.SmallIconSize.Width, v);
            tray.Text = tip;
            if (old != null) old.Dispose();
            iconIsRing = true; lastIcon = now;
        }

        public void ToggleFlyout()
        {
            if (flyout.Visible) { flyout.Hide(); return; }
            var v = Engine.View();
            if (v.Phase != FocusPhase.Running && v.Phase != FocusPhase.Break) { Tick(); return; }
            flyout.View = v;
            var anchor = labels.FirstOrDefault(l => l.Visible);
            var r = anchor != null ? anchor.Bounds : new Rectangle(Cursor.Position, new Size(1, 1));
            var work = Screen.FromRectangle(r).WorkingArea;
            flyout.Location = new Point(Math.Max(work.Left, Math.Min(r.Right - flyout.Width, work.Right - flyout.Width - Floating.Px(8))), work.Bottom - flyout.Height - Floating.Px(10));
            flyout.Show(); flyout.Invalidate();
        }

        // ---- cards: start, check-in, warning, day done ----

        void ShowCard(FocusView v)
        {
            long now = Iso.Ms(DateTime.UtcNow);
            string want = null;
            if (v.Phase == FocusPhase.Upcoming) want = "start:" + v.Key;
            else if (v.Phase == FocusPhase.Warning && !v.Waiting) want = "warn:" + v.Key; // in a meeting nothing pops up
            else if (v.Phase == FocusPhase.Running && v.CheckinDue) want = "checkin:" + v.Key;
            else if (v.Phase == FocusPhase.DayDone) want = "day";
            if (card.Kind != null && card.Kind.StartsWith("phone") && now < cardHiddenUntil) return; // "Options on your phone" stays a moment
            if (want == null) { if (card.Visible) card.Hide(); card.Kind = null; return; }
            if (card.Kind != want)
            {
                if (want.StartsWith("start:"))
                    card.Set(want, "Focus · " + v.Minutes + " min", null, "Goal set on phone. Starts by itself in a moment.", false, true,
                        "Start", (Action)(() => { Engine.Start(); Tick(); }), "Snooze " + Engine.Data.Config.snoozeMin + " min", (Action)(() => { Engine.Snooze(); Tick(); }));
                else if (want.StartsWith("checkin:"))
                    card.Set(want, "Closer to your goal than " + Engine.Data.Config.checkinMin + " min ago?", null, "Stuck or Detoured: the options are on your phone.", true, false,
                        "On track", (Action)(() => Answer("on-track")), "Stuck", (Action)(() => Answer("stuck")), "Detoured", (Action)(() => Answer("detoured")));
                else if (want.StartsWith("warn:"))
                {
                    if (v.Waiting) card.Set(want, "Break waiting", null, "In a meeting. The break starts when meeting mode is off.", true, false,
                        "Meeting off", (Action)(() => { Engine.ToggleMeeting(); Tick(); }));
                    else card.Set(want, "Break starts in seconds", "10", null, true, true,
                        "Start now", (Action)(() => { Engine.WarnStartNow(); Tick(); }), "+5 min", (Action)(() => { Engine.WarnLater(); Tick(); }), "Cancel", (Action)(() => { Engine.WarnCancel(); Tick(); }));
                }
                else if (want == "day")
                    card.Set(want, "Day done", null, "Your review is on your phone.", false, false, "Close", (Action)(() => { Engine.DayDoneSeen(); Tick(); }));
                card.PlaceCorner(labels.Count > 0 && labels[0].Visible ? labels[0].Bounds : Screen.PrimaryScreen.Bounds);
            }
            if (want.StartsWith("warn:") && !v.Waiting) card.SetBig(((v.Left + 999) / 1000).ToString());
            if (!card.Visible) card.Show();
        }

        void Answer(string a)
        {
            Engine.Checkin(a);
            if (a != "on-track")
            {
                card.Set("phone", "Noted", null, "Options on your phone: ask the Assistant, take a simpler path, or park it.", false, false);
                card.PlaceCorner(Screen.PrimaryScreen.Bounds);
                cardHiddenUntil = Iso.Ms(DateTime.UtcNow) + 4000;
            }
            Tick();
        }

        // ---- time's up and the break: dimmed and full screen ----

        void ShowTimeUp(FocusView v)
        {
            bool want = v.Phase == FocusPhase.TimeUp && !v.Meeting;
            if (!want)
            {
                if (timeUp != null && timeUp.Visible) { timeUp.Hide(); CloseVeils(false); }
                return;
            }
            Veils(false, v);
            if (timeUp == null || timeUp.IsDisposed)
            {
                timeUp = new TimeUpCard();
                timeUp.Answer += async a =>
                {
                    if (a == "extend") await Extend();
                    else Engine.TimeUp(a);
                    timeUp.Hide(); CloseVeils(false); Tick();
                };
            }
            timeUp.Set(v, Engine.Data.Config.extendMin, Screen.FromPoint(Cursor.Position));
            if (!timeUp.Visible) { timeUp.Show(); OnTop.Raise(timeUp); }
        }

        /// <summary>+15: checks Notion first, so a second device can't extend the same block again.</summary>
        async Task Extend()
        {
            var run = Engine.Running();
            if (run != null && !Demo && !string.IsNullOrEmpty(run.PageId) && S.NotionToken.Length > 0)
            {
                try
                {
                    string page = run.PageId;
                    var fresh = await Task.Run(() => Notion.ReadSession(page));
                    if (fresh != null && fresh.Extensions > run.Extensions) run.Extensions = fresh.Extensions;
                }
                catch (Exception e) { Log.Write("focus extension check offline: " + e.GetType().Name); }
            }
            if (!Engine.TimeUp("extend")) Engine.TimeUp("done");
        }

        void ShowBreak(FocusView v)
        {
            bool want = v.Phase == FocusPhase.Break;
            if (!want)
            {
                CloseVeils(true);
                if (skipBox != null && !skipBox.IsDisposed) skipBox.Close(); // the break ended while the box was open
                return;
            }
            Veils(true, v);
            foreach (var w in veils) { w.View = v; w.Invalidate(); }
        }

        void Veils(bool isBreak, FocusView v)
        {
            if (veils.Count > 0 && veils[0].IsBreak == isBreak && veils.Count == Screen.AllScreens.Length) return;
            CloseVeils();
            var main = Screen.FromPoint(Cursor.Position);
            foreach (var s in Screen.AllScreens)
            {
                var w = new Veil(s, isBreak) { View = v, Main = s.DeviceName == main.DeviceName };
                w.Skip += AskSkip;
                veils.Add(w);
                w.Show();
                Fade(w, isBreak ? 1.0 : 0.78, 400);
            }
            if (timeUp != null && timeUp.Visible) OnTop.Raise(timeUp);
        }

        static async void Fade(Form f, double to, int ms)
        {
            double from = f.Opacity; int steps = Math.Max(1, ms / 25);
            for (int i = 1; i <= steps && !f.IsDisposed; i++) { f.Opacity = from + (to - from) * i / steps; await Task.Delay(25); }
        }

        void CloseVeils() { foreach (var w in veils.ToArray()) Close(w); veils.Clear(); }
        void CloseVeils(bool isBreak) { if (veils.Count > 0 && veils[0].IsBreak == isBreak) CloseVeils(); }

        static async void Close(Veil w)
        {
            // the break fades out; the next block's start card follows on the next tick
            for (int i = 0; i < 12 && !w.IsDisposed; i++) { w.Opacity = Math.Max(0, w.Opacity - 0.09); await Task.Delay(25); }
            if (!w.IsDisposed) w.Close();
        }

        void AskSkip()
        {
            if (skipBox != null && !skipBox.IsDisposed) { skipBox.Activate(); return; }
            using (skipBox = new SkipBox())
            {
                if (skipBox.ShowDialog() == DialogResult.OK) { Engine.SkipBreak(skipBox.Reason); Tick(); }
            }
            skipBox = null;
        }

        // ---- sync ----

        void MaybeSync(FocusView v)
        {
            long now = Iso.Ms(DateTime.UtcNow);
            if (syncing || S.NotionToken.Length == 0 || string.IsNullOrEmpty(S.FocusDb)) return;
            bool busy = v.Phase != FocusPhase.Idle && v.Phase != FocusPhase.DayDone;
            long every = busy ? 20000 : 60000;
            if (failures > 0) every = Math.Min(5 * 60000, 20000L << Math.Min(failures, 4));
            if (nextSync > 0 && now >= nextSync) { Sync(); return; }
            if (now - lastSync >= every) Sync();
        }

        /// <summary>Sends what changed here, then reads the plan, the settings and today's sessions.</summary>
        public async void Sync()
        {
            if (syncing || Demo) return;
            string db = S.FocusDb;
            if (S.NotionToken.Length == 0 || string.IsNullOrEmpty(db)) { SetStatus("Not connected"); return; }
            syncing = true; nextSync = 0;
            var data = Engine.Data;
            var outgoing = data.Sessions.Where(s => s.Dirty).Select(s => Json.Read<FocusSession>(Json.Write(s))).ToList();
            bool configOut = data.ConfigDirty; var config = data.Config.Copy(); string settingsPage = data.SettingsPage;
            try
            {
                var today = DateTime.Now.Date;
                List<FocusSession> remote = null; FocusConfig remoteConfig = null; string remoteSettings = null;
                var pushed = new Dictionary<string, string>();
                var pulled = await Task.Run(() =>
                {
                    foreach (var s in outgoing) pushed[s.Key + "#" + s.Rev] = Notion.PushSession(db, s);
                    if (configOut) settingsPage = Notion.PushConfig(db, settingsPage, config);
                    var d = Notion.Pull(db, today, out remote, out remoteConfig, out remoteSettings);
                    if (remoteConfig == null && !configOut && string.IsNullOrEmpty(remoteSettings)) remoteSettings = Notion.PushConfig(db, null, config); // first device: make the settings row
                    return d;
                });
                // Back on the UI thread: what was sent is no longer dirty, unless it changed again meanwhile.
                foreach (var s in data.Sessions)
                {
                    string page;
                    if (pushed.TryGetValue(s.Key + "#" + s.Rev, out page)) { s.PageId = page; s.Dirty = false; }
                    else
                    {
                        var sent = outgoing.FirstOrDefault(o => o.Key == s.Key);
                        if (sent != null && pushed.TryGetValue(sent.Key + "#" + sent.Rev, out page)) s.PageId = page; // changed again: send again
                    }
                }
                if (configOut && data.Config.rev == config.rev) data.ConfigDirty = false;
                data.SettingsPage = remoteSettings ?? settingsPage;
                data.Routines = pulled.Routines; data.Blocks = pulled.Blocks; data.Overrides = pulled.Overrides;
                FocusEngine.MergeConfig(data, remoteConfig);
                var archive = FocusEngine.Merge(data, remote);
                Engine.Prune();
                data.Synced = Iso.Ms(DateTime.UtcNow);
                data.Save();
                failures = 0;
                SetStatus("Synced " + DateTime.Now.ToString("HH:mm"));
                if (archive.Count > 0) { var a = archive.ToList(); await Task.Run(() => { foreach (var p in a) try { Notion.Archive(p); } catch { } }); }
            }
            catch (Exception e)
            {
                while (e is AggregateException && e.InnerException != null) e = e.InnerException;
                failures++;
                Log.Write("focus sync failed: " + e.GetType().Name);
                SetStatus("Offline: " + (e.Message.Length > 120 ? e.Message.Substring(0, 120) : e.Message));
            }
            finally { syncing = false; lastSync = Iso.Ms(DateTime.UtcNow); }
            if (Engine.Data.Sessions.Any(s => s.Dirty) || Engine.Data.ConfigDirty) SyncSoon(failures > 0 ? 30000 : 2000);
            Tick();
        }

        void SetStatus(string s) { Status = s; if (StatusChanged != null) StatusChanged(); }

        /// <summary>Settings → Focus changed the shared settings: they go to Notion for every device.</summary>
        public void SetConfig(FocusConfig c)
        {
            c.meetingUntil = Engine.Data.Config.meetingUntil; c.rev = Engine.Data.Config.rev + 1;
            Engine.Data.Config = c; Engine.Data.ConfigDirty = true; Engine.Data.Save(); SyncSoon(500);
        }

        public void Apply() { if (!On) HideAll(); else { SyncSoon(500); Tick(); } }

        /// <summary>
        /// --preview: a made-up 60-minute Focus block and a break, played fast with a shifted clock, so every state
        /// can be seen without Notion: start card, running, controls, check-in, wrap up, time's up, warning, break.
        /// </summary>
        public async void PlayDemo(Func<Task> before)
        {
            Demo = true;
            if (before != null) await before();
            tick.Start(); // the clock runs as usual, from a shifted start
            long offset = 0;
            Engine.Now = () => Iso.Ms(DateTime.UtcNow) + offset;
            var now = DateTime.Now; int start = now.Hour * 60 + now.Minute;
            if (start > 22 * 60) start = 22 * 60; // keep the demo inside today
            var d = Engine.Data;
            d.Routines.Clear(); d.Blocks.Clear(); d.Overrides.Clear(); d.Sessions.Clear();
            d.Routines.Add(new FocusRoutine { Id = "demo", Name = "Demo day", Days = now.DayOfWeek.ToString().Substring(0, 3) });
            d.Blocks.Add(new FocusBlock { Id = "b1", Type = FocusType.Learning, Start = start, End = start + 60, Checkin = 30, Routine = "demo" });
            d.Blocks.Add(new FocusBlock { Id = "b2", Type = FocusType.Break, Start = start + 60, End = start + 75, Checkin = 0, Routine = "demo" });
            long t0 = Iso.Ms(now.Date.AddMinutes(start));
            Func<long, Task> at = async (ms) => { offset = t0 + ms - Iso.Ms(DateTime.UtcNow); Tick(); await Task.Delay(50); };
            const long M = 60000;
            await at(5000); await Task.Delay(4000);                       // the start card
            Engine.Start(); await at(60 * M - (38 * M + 12000)); await Task.Delay(3500); // running: 38:12
            ToggleFlyout(); await Task.Delay(3500); flyout.Hide();
            await at(30 * M + 8000); await Task.Delay(4000);              // check-in (30 min after it started)
            Answer("on-track"); await Task.Delay(800);
            await at(50 * M); await Task.Delay(4000);                     // wrap up
            await at(60 * M + 1000); await Task.Delay(4500);              // time's up
            Engine.TimeUp("done"); Tick(); await Task.Delay(6000);        // the break warning counts down
            await at(60 * M + 40000); await Task.Delay(1500);
            await at(74 * M + 50000); await Task.Delay(5000);             // the break, near its end
            await at(75 * M + 1000); await Task.Delay(2000);
            Engine.Now = () => Iso.Ms(DateTime.UtcNow);
            d.Blocks.Clear(); d.Routines.Clear(); d.Sessions.Clear();
            tick.Stop(); HideAll(); // stays a demo: a preview never syncs or saves
        }

        public void Dispose()
        {
            SystemEvents.PowerModeChanged -= Power;
            SystemEvents.SessionSwitch -= Session;
            SystemEvents.DisplaySettingsChanged -= Display;
            tick.Stop(); HideAll();
        }
    }
}
