// The Prosper bar (a frosted glass pill at the bottom of the screen) and the answer card and Allow dialog. None of
// the floating windows take the keyboard focus away from the app the user is typing in, so text can be pasted back.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Prosper
{
    static class Theme
    {
        public static readonly Color Ink = Color.FromArgb(17, 17, 19), Gold = Color.FromArgb(232, 194, 106),
            Emerald = Color.FromArgb(15, 107, 79), Red = Color.FromArgb(239, 68, 68), Green = Color.FromArgb(74, 222, 128);
    }

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

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        /// <summary>Centred above [anchor] (below it when there's no room), inside the screen's work area.</summary>
        public void PlaceAbove(Rectangle anchor, int gap)
        {
            var work = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Left + (anchor.Width - Width) / 2;
            int y = anchor.Top - gap - Height >= work.Top ? anchor.Top - gap - Height : anchor.Bottom + gap;
            Location = new Point(Math.Max(work.Left, Math.Min(x, work.Right - Width)), Math.Max(work.Top, Math.Min(y, work.Bottom - Height)));
        }
    }

    enum BarState { Rest, Dictating, Assisting, Working }

    /// <summary>
    /// The Prosper bar: a frosted glass pill docked at the bottom centre. At rest a small sliver with a gold dot; it
    /// grows to show a hint on hover, and while recording becomes ✕ · timer · live waveform · ✓ (the Assistant adds
    /// a gold edge and "Add screen"). Drawn with per-pixel alpha, so its edges and shadow are smooth.
    /// </summary>
    class Bar : Form
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

        public event Action Tap, DoubleTap, Hold, ShowMenu, Cancel, Finish, ToggleScreen;
        public Func<float> LevelSource;
        public bool ExcludedFromCapture { get; private set; }
        public static bool AllowCapture; // --preview: so the design can be screenshotted

        BarState state;
        bool hover, screenAdded, keepVisible = true;
        string message, step; Color messageColor; bool messageIsError;
        readonly Stopwatch since = new Stopwatch();
        readonly Timer frame = new Timer { Interval = 33 };
        readonly Timer messageTimer = new Timer();
        readonly Timer clickWait = new Timer();
        readonly Timer holdWait = new Timer { Interval = 600 };
        readonly Random noise = new Random();
        float w, h, level, spin, ceiling = 0.3f; // the pill's current (animated) size, the smoothed voice level, its recent loudest
        RectangleF pill, cancelBtn, finishBtn, camBtn;
        Point down, origin; bool dragging, held, pending;
        readonly Font font = new Font("Segoe UI Semibold", 9f), small = new Font("Segoe UI", 8.5f);

        public Bar()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            Size = new Size(Floating.Px(460), Floating.Px(60));
            w = Floating.Px(50); h = Floating.Px(12);
            frame.Tick += (s, e) => Draw();
            messageTimer.Tick += (s, e) => { messageTimer.Stop(); message = null; Draw(); };
            clickWait.Interval = SystemInformation.DoubleClickTime;
            clickWait.Tick += (s, e) => { clickWait.Stop(); pending = false; if (Tap != null) Tap(); };
            holdWait.Tick += (s, e) => { holdWait.Stop(); if (!dragging && state == BarState.Rest) { held = true; if (Hold != null) Hold(); } };
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST; return cp; } // WS_EX_LAYERED
        }
        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 10 2004+: the bar stays out of screenshots and screen shares (the one Prosper takes too).
            if (!AllowCapture) try { ExcludedFromCapture = SetWindowDisplayAffinity(Handle, 0x11); } catch { ExcludedFromCapture = false; }
            Draw();
        }

        // ---- state ----

        public BarState State
        {
            get { return state; }
            set
            {
                state = value;
                if (value == BarState.Dictating || value == BarState.Assisting) { since.Restart(); screenAdded = false; message = null; }
                if (value != BarState.Working) step = null;
                ApplyVisibility(); Animate();
            }
        }

        /// <summary>Show the resting bar (the user's setting); while recording or working it always shows.</summary>
        public bool KeepVisible { get { return keepVisible; } set { keepVisible = value; ApplyVisibility(); } }

        void ApplyVisibility()
        {
            bool show = keepVisible || state != BarState.Rest || message != null;
            if (show && !Visible) Show(); else if (!show && Visible) Hide();
        }

        public bool ScreenAdded { get { return screenAdded; } set { screenAdded = value; Animate(); } }

        /// <summary>What the Assistant or the transcriber is doing now ("Transcribing", "Calling list secrets").</summary>
        public void Step(string text) { step = text; Animate(); }

        /// <summary>A short note in the pill once it's done ("Typed · copied", an error); it goes after [ms].</summary>
        public void Say(string text, int ms, bool error = false)
        {
            message = text; messageIsError = error; messageColor = error ? Theme.Red : Theme.Green;
            messageTimer.Stop(); messageTimer.Interval = Math.Max(800, ms); messageTimer.Start();
            ApplyVisibility(); Animate();
        }

        /// <summary>The pill's rectangle on screen, to place the answer card above it.</summary>
        public Rectangle PillOnScreen { get { return new Rectangle(Left + (int)pill.X, Top + (int)pill.Y, (int)pill.Width, (int)pill.Height); } }

        public void PlaceSaved()
        {
            var s = Settings.Current;
            var work = Screen.PrimaryScreen.WorkingArea;
            var def = new Point(work.Left + (work.Width - Width) / 2, work.Bottom - Height - Floating.Px(2)); // bottom centre, above the taskbar
            var p = s.BarX >= 0 ? new Point(s.BarX, s.BarY) : def;
            bool onScreen = false;
            foreach (var sc in Screen.AllScreens) if (sc.Bounds.Contains(new Rectangle(p, Size))) onScreen = true;
            Location = onScreen ? p : def;
        }

        void Animate() { if (!frame.Enabled) frame.Start(); Draw(); }

        // ---- drawing ----

        SizeF Target(Graphics g)
        {
            float px = Floating.Dpi;
            if (message != null && state == BarState.Rest) return new SizeF(Math.Min(Width - 20 * px, g.MeasureString(message, font).Width + 44 * px), 32 * px);
            switch (state)
            {
                case BarState.Dictating: return new SizeF(236 * px, 40 * px);
                case BarState.Assisting: return new SizeF((screenAdded ? 318 : 306) * px, 40 * px);
                case BarState.Working:
                    string t = step ?? "Working";
                    return new SizeF(Math.Min(Width - 20 * px, g.MeasureString(t, font).Width + 50 * px), 32 * px);
                default: return hover ? new SizeF(Math.Min(Width - 20 * px, g.MeasureString(Hint, small).Width + 46 * px), 32 * px) : new SizeF(50 * px, 12 * px);
            }
        }

        const string Hint = "Click to dictate · double-click for Assistant · hold for history";

        void Draw()
        {
            if (!IsHandleCreated) return;
            using (var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    var target = Target(g);
                    w += (target.Width - w) * 0.35f; h += (target.Height - h) * 0.35f;
                    bool settled = Math.Abs(target.Width - w) < 1 && Math.Abs(target.Height - h) < 1;
                    if (settled) { w = target.Width; h = target.Height; }
                    float raw = LevelSource != null && (state == BarState.Dictating || state == BarState.Assisting) ? LevelSource() : 0f;
                    // Automatic gain: scaled to the recent loudest, so a quiet mic still moves the bars and silence stays flat.
                    ceiling = Math.Max(0.3f, Math.Max(raw, ceiling * 0.995f));
                    float shown = Math.Min(1f, raw / ceiling);
                    level += (shown - level) * (shown > level ? 0.6f : 0.25f); // quick to rise, gentle to fall
                    spin = (spin + 14) % 360;
                    float cx = Width / 2f, cy = Height - Floating.Px(26);
                    pill = new RectangleF(cx - w / 2, cy - h / 2, w, h);
                    DrawPill(g, pill);
                    float reveal = Math.Max(0, Math.Min(1, (w - 60 * Floating.Dpi) / Math.Max(1, target.Width - 60 * Floating.Dpi)));
                    if (settled || reveal > 0.85f) DrawContent(g, pill);
                    bool moving = !settled || state != BarState.Rest || message != null;
                    if (!moving) frame.Stop();
                }
                Push(bmp);
            }
        }

        void DrawPill(Graphics g, RectangleF r)
        {
            float rad = r.Height / 2;
            for (int i = 6; i >= 1; i--) // soft shadow
                using (var p = Floating.Round(new RectangleF(r.X - i, r.Y - i + 2, r.Width + 2 * i, r.Height + 2 * i), rad + i))
                using (var b = new SolidBrush(Color.FromArgb(state == BarState.Rest && !hover && message == null ? 5 : 9, 0, 0, 0))) g.FillPath(b, p);
            using (var p = Floating.Round(r, rad))
            {
                using (var b = new SolidBrush(Color.FromArgb(state == BarState.Rest && !hover && message == null ? 190 : 214, 24, 24, 27))) g.FillPath(b, p);
                // The glass edge: gold while the Assistant listens.
                Color edge = state == BarState.Assisting ? Color.FromArgb(170, Theme.Gold) : Color.FromArgb(80, 255, 255, 255);
                using (var pen = new Pen(edge, 1f)) g.DrawPath(pen, p);
            }
            // A faint highlight along the top, as glass catches light.
            using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255), 1f))
                g.DrawLine(pen, r.X + rad, r.Y + 1.5f, r.Right - rad, r.Y + 1.5f);
        }

        void DrawContent(Graphics g, RectangleF r)
        {
            float px = Floating.Dpi, cy = r.Y + r.Height / 2;
            cancelBtn = finishBtn = camBtn = RectangleF.Empty;
            if (message != null && state == BarState.Rest)
            {
                float x = r.X + 14 * px;
                if (messageIsError) DrawBang(g, x + 6 * px, cy, 6 * px); else DrawCheck(g, x + 6 * px, cy, 6 * px, messageColor, 2f * px);
                DrawLabel(g, message, font, Color.White, x + 18 * px, cy);
                return;
            }
            switch (state)
            {
                case BarState.Rest:
                    if (hover)
                    {
                        DrawMic(g, r.X + 18 * px, cy, Theme.Gold);
                        DrawLabel(g, Hint, small, Color.FromArgb(235, 255, 255, 255), r.X + 30 * px, cy);
                    }
                    else using (var b = new SolidBrush(Theme.Gold)) g.FillEllipse(b, r.X + r.Width / 2 - 2 * px, cy - 2 * px, 4 * px, 4 * px);
                    break;
                case BarState.Working:
                    using (var track = new Pen(Color.FromArgb(60, Theme.Gold), 2f * px)) g.DrawEllipse(track, r.X + 13 * px, cy - 7 * px, 14 * px, 14 * px);
                    using (var arc = new Pen(Theme.Gold, 2f * px) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(arc, r.X + 13 * px, cy - 7 * px, 14 * px, 14 * px, spin, 100);
                    DrawLabel(g, step ?? "Working", font, Color.White, r.X + 36 * px, cy);
                    break;
                case BarState.Dictating:
                case BarState.Assisting:
                    bool a = state == BarState.Assisting;
                    float x0 = r.X + 6 * px;
                    cancelBtn = new RectangleF(x0, cy - 14 * px, 28 * px, 28 * px);
                    Circle(g, cancelBtn, Color.FromArgb(36, 255, 255, 255));
                    DrawX(g, cancelBtn, Color.White);
                    float x = cancelBtn.Right + 10 * px;
                    int n;
                    Color barColor;
                    if (a)
                    {
                        Sparkle(g, x + 7 * px, cy, 7 * px, Theme.Gold);
                        x += 24 * px; n = 15; barColor = Theme.Gold;
                    }
                    else
                    {
                        float pulse = 0.45f + 0.55f * (float)Math.Abs(Math.Sin(since.ElapsedMilliseconds / 450.0));
                        using (var b = new SolidBrush(Color.FromArgb((int)(255 * pulse), Theme.Red))) g.FillEllipse(b, x, cy - 3.5f * px, 7 * px, 7 * px);
                        long s = since.ElapsedMilliseconds / 1000;
                        DrawLabel(g, (s / 60) + ":" + (s % 60).ToString("00"), font, Color.White, x + 12 * px, cy);
                        x += 50 * px; n = 19; barColor = Color.FromArgb(240, 255, 255, 255);
                    }
                    DrawWave(g, x, cy, n, barColor);
                    x += n * 5 * px + 8 * px;
                    if (a)
                    {
                        string label = screenAdded ? "Screen added" : "Add screen";
                        float bw = (screenAdded ? 108 : 96) * px;
                        camBtn = new RectangleF(x, cy - 14 * px, bw, 28 * px);
                        using (var p = Floating.Round(camBtn, 14 * px))
                        using (var b = new SolidBrush(screenAdded ? Theme.Gold : Color.FromArgb(36, 255, 255, 255))) g.FillPath(b, p);
                        Color fg = screenAdded ? Theme.Ink : Color.White;
                        DrawCamera(g, camBtn.X + 16 * px, cy, fg);
                        DrawLabel(g, label, small, fg, camBtn.X + 28 * px, cy);
                        x = camBtn.Right + 8 * px;
                    }
                    finishBtn = new RectangleF(r.Right - 34 * px, cy - 14 * px, 28 * px, 28 * px);
                    Circle(g, finishBtn, a ? Theme.Gold : Color.White);
                    DrawCheck(g, finishBtn.X + finishBtn.Width / 2, cy, 6 * px, Theme.Ink, 2f * px);
                    break;
            }
        }

        /// <summary>One voice level drawn from the middle out: tallest in the centre, tapering to the edges.</summary>
        void DrawWave(Graphics g, float x, float cy, int n, Color c)
        {
            float px = Floating.Dpi;
            using (var pen = new Pen(c, 3 * px) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i < n; i++)
                {
                    float d = Math.Abs(i - (n - 1) / 2f) / ((n - 1) / 2f);
                    float shape = 1 - d * d * 0.85f;
                    float jitter = 0.7f + (float)noise.NextDouble() * 0.3f;
                    float half = Math.Max(1.2f * px, level * shape * jitter * 12 * px);
                    float bx = x + i * 5 * px + 1.5f * px;
                    g.DrawLine(pen, bx, cy - half, bx, cy + half);
                }
        }

        static void DrawLabel(Graphics g, string s, Font f, Color c, float x, float cy)
        {
            var size = g.MeasureString(s, f);
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, cy - size.Height / 2);
        }
        static void Circle(Graphics g, RectangleF r, Color c) { using (var b = new SolidBrush(c)) g.FillEllipse(b, r); }
        static void DrawX(Graphics g, RectangleF r, Color c)
        {
            float k = r.Width * 0.18f, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (var p = new Pen(c, 1.8f * Floating.Dpi) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            { g.DrawLine(p, cx - k, cy - k, cx + k, cy + k); g.DrawLine(p, cx + k, cy - k, cx - k, cy + k); }
        }
        static void DrawCheck(Graphics g, float cx, float cy, float s, Color c, float width)
        {
            using (var p = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, new[] { new PointF(cx - s * 0.75f, cy), new PointF(cx - s * 0.2f, cy + s * 0.55f), new PointF(cx + s * 0.8f, cy - s * 0.6f) });
        }
        static void DrawBang(Graphics g, float cx, float cy, float s)
        {
            using (var b = new SolidBrush(Theme.Red)) g.FillEllipse(b, cx - s, cy - s, 2 * s, 2 * s);
            using (var p = new Pen(Color.White, 1.6f * Floating.Dpi) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, cx, cy - s * 0.5f, cx, cy + s * 0.1f);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, cx - 0.9f * Floating.Dpi, cy + s * 0.35f, 1.8f * Floating.Dpi, 1.8f * Floating.Dpi);
        }
        public static void Sparkle(Graphics g, float cx, float cy, float r, Color c)
        {
            float k = r * 0.28f;
            using (var b = new SolidBrush(c))
                g.FillPolygon(b, new[] {
                    new PointF(cx, cy - r), new PointF(cx + k, cy - k), new PointF(cx + r, cy), new PointF(cx + k, cy + k),
                    new PointF(cx, cy + r), new PointF(cx - k, cy + k), new PointF(cx - r, cy), new PointF(cx - k, cy - k) });
        }
        static void DrawMic(Graphics g, float cx, float cy, Color c)
        {
            float px = Floating.Dpi;
            using (var p = new Pen(c, 1.4f * px) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                using (var body = Floating.Round(new RectangleF(cx - 2.5f * px, cy - 6.5f * px, 5 * px, 8.5f * px), 2.5f * px)) g.DrawPath(p, body);
                g.DrawArc(p, cx - 4.5f * px, cy - 4.5f * px, 9 * px, 9 * px, 0, 180);
                g.DrawLine(p, cx, cy + 4.5f * px, cx, cy + 6.5f * px);
            }
        }
        static void DrawCamera(Graphics g, float cx, float cy, Color c)
        {
            float px = Floating.Dpi;
            using (var p = new Pen(c, 1.4f * px) { LineJoin = LineJoin.Round })
            {
                g.DrawPolygon(p, new[] {
                    new PointF(cx - 5.5f * px, cy - 2.7f * px), new PointF(cx - 3.3f * px, cy - 2.7f * px), new PointF(cx - 2.1f * px, cy - 4.5f * px),
                    new PointF(cx + 2.1f * px, cy - 4.5f * px), new PointF(cx + 3.3f * px, cy - 2.7f * px), new PointF(cx + 5.5f * px, cy - 2.7f * px),
                    new PointF(cx + 5.5f * px, cy + 4.1f * px), new PointF(cx - 5.5f * px, cy + 4.1f * px) });
                g.DrawEllipse(p, cx - 2 * px, cy - 1.2f * px, 4 * px, 4 * px);
            }
        }

        /// <summary>Hands the drawn frame to Windows with its alpha (UpdateLayeredWindow).</summary>
        void Push(Bitmap bmp)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hbmp);
                var size = new SIZE(bmp.Width, bmp.Height); var src = new POINT(0, 0); var at = new POINT(Left, Top);
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref src, 0, ref blend, 2);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                if (hbmp != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hbmp); }
                DeleteDC(mem);
            }
        }

        // ---- mouse ----

        protected override void OnMouseEnter(EventArgs e) { hover = true; Animate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Animate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { if (state == BarState.Rest && ShowMenu != null) ShowMenu(); return; }
            if (e.Button != MouseButtons.Left) return;
            down = Cursor.Position; origin = Location; dragging = false; held = false;
            if (state == BarState.Rest) holdWait.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var p = Cursor.Position;
            if (!dragging && (Math.Abs(p.X - down.X) > Floating.Px(4) || Math.Abs(p.Y - down.Y) > Floating.Px(4))) { dragging = true; holdWait.Stop(); }
            if (dragging) { Location = new Point(origin.X + p.X - down.X, origin.Y + p.Y - down.Y); Draw(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            holdWait.Stop();
            if (dragging)
            {
                var area = Screen.FromPoint(Cursor.Position).Bounds; // keep the whole bar on the screen it was dropped on
                Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
                Draw();
                Settings.Current.BarX = Left; Settings.Current.BarY = Top; Settings.Save();
                return;
            }
            if (held) return;
            switch (state)
            {
                case BarState.Rest:
                    if (pending) { clickWait.Stop(); pending = false; if (DoubleTap != null) DoubleTap(); return; }
                    pending = true; clickWait.Start(); // a second click within the double-click time is a double-click
                    break;
                case BarState.Dictating:
                case BarState.Assisting:
                    if (cancelBtn.Contains(e.Location)) { if (Cancel != null) Cancel(); }
                    else if (camBtn.Contains(e.Location)) { if (ToggleScreen != null) ToggleScreen(); }
                    else if (Finish != null) Finish(); // the ✓, or anywhere else on the bar
                    break;
            }
        }
    }

    /// <summary>The Assistant's answer when it wasn't typed into a field: rendered, with Insert / Copy / Open chat.</summary>
    class AnswerCard : Floating
    {
        public event Action Insert, OpenChat;
        readonly WebBrowser web = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, AllowWebBrowserDrop = false, IsWebBrowserContextMenuEnabled = true };
        string answer = "";

        public AnswerCard()
        {
            Size = new Size(Px(500), Px(400));
            BackColor = Style.Line; Padding = new Padding(1);
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Style.Bg };
            // Top: the logo and "Prosper"; bottom: Insert · Copy · Open chat · Close.
            var top = new Panel { Dock = DockStyle.Top, Height = Px(44), BackColor = Style.Bg };
            top.Controls.Add(new LogoView(Px(22)) { Location = new Point(Px(16), Px(12)) });
            top.Controls.Add(new Label { Text = "Prosper", Font = Style.Strong, ForeColor = Style.Text, AutoSize = true, Location = new Point(Px(46), Px(13)) });
            top.Controls.Add(new Label { Text = "Assistant", Font = Style.Small, ForeColor = Style.Gold, AutoSize = true, Location = new Point(Px(104), Px(15)) });
            top.Paint += (s, e) => { using (var p = new Pen(Style.Line)) e.Graphics.DrawLine(p, 0, top.Height - 1, top.Width, top.Height - 1); };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Px(52), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Px(12), Px(10), Px(8), Px(10)), BackColor = Style.Surface };
            bar.Paint += (s, e) => { using (var p = new Pen(Style.Line)) e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
            bar.Controls.Add(Btn("Close", false, (s, e) => Hide()));
            bar.Controls.Add(Btn("Open chat", false, (s, e) => { Hide(); if (OpenChat != null) OpenChat(); }));
            bar.Controls.Add(Btn("Copy", false, (s, e) => { Clip.Copy(answer); }));
            bar.Controls.Add(Btn("Insert", true, (s, e) => { Hide(); if (Insert != null) Insert(); }));
            web.Navigating += Browser.OpenLinksOutside;
            inner.Controls.Add(web); inner.Controls.Add(bar); inner.Controls.Add(top);
            Controls.Add(inner);
        }

        Button Btn(string t, bool primary, EventHandler click)
        {
            var b = Style.Btn(t, primary);
            b.Margin = new Padding(Px(6), 0, 0, 0); b.Click += click;
            return b;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Region = new Region(Round(new RectangleF(0, 0, Width, Height), Px(12)));
        }

        public string Answer { get { return answer; } }

        public void Show(string text, bool status, Rectangle anchor)
        {
            answer = text;
            Browser.Load(web, Markdown.Page((status ? "<div class='lbl'>RESULT</div>" : "") + Markdown.Html(text)));
            PlaceAbove(anchor, Px(10));
            if (!Visible) Show();
        }
    }

    static class Browser
    {
        /// <summary>Shows [html] in the built-in browser control.</summary>
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
                try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
            }
        }
    }

    /// <summary>Asks before an MCP tool runs: Allow once, always allow this tool, or Cancel.</summary>
    class ApprovalForm : Form
    {
        public Approval Result = Approval.Deny;

        public ApprovalForm(string tool, string what, string args)
        {
            Text = "Prosper Voice · Allow this action?"; TopMost = true; StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(Style.Px(560), Style.Px(420));
            var head = new Panel { Dock = DockStyle.Top, Height = Style.Px(96), BackColor = Style.Bg };
            head.Controls.Add(new LogoView(Style.Px(40)) { Location = new Point(Style.Px(24), Style.Px(24)) });
            head.Controls.Add(new Label { Text = "Allow this action?", Font = Style.Heading, ForeColor = Style.Text, AutoSize = true, Location = new Point(Style.Px(78), Style.Px(20)) });
            head.Controls.Add(new Label
            {
                Text = "The Assistant wants to run this through your MCP server: " + what, Font = Style.Body, ForeColor = Style.Muted,
                AutoSize = true, MaximumSize = new Size(Style.Px(450), Style.Px(40)), Location = new Point(Style.Px(78), Style.Px(46)),
            });
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Style.Bg, Padding = new Padding(Style.Px(24), Style.Px(26), Style.Px(24), Style.Px(8)) };
            var caption = new Label { Text = "WHAT IT SENDS  ·  " + tool, Font = Style.Caps, ForeColor = Style.Gold, Dock = DockStyle.Top, Height = Style.Px(22) };
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Style.Mono,
                Text = (args.Length > 4000 ? args.Substring(0, 4000) + "…" : args).Replace("\r\n", "\n").Replace("\n", "\r\n") };
            Shown += (s, e) => { box.Select(0, 0); ActiveControl = null; };
            body.Controls.Add(new FieldBox(box, Style.Px(500), Style.Px(200)) { Dock = DockStyle.Fill });
            body.Controls.Add(caption);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Style.Px(64), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(Style.Px(24), Style.Px(14), Style.Px(16), Style.Px(14)), BackColor = Style.Surface };
            bar.Paint += (s, e) => { using (var p = new Pen(Style.Line)) e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
            Action<string, Approval, bool> add = (t, a, primary) => { var b = Style.Btn(t, primary); b.Click += (s, e) => { Result = a; Close(); }; bar.Controls.Add(b); };
            add("Cancel", Approval.Deny, false);
            add("Always allow", Approval.Always, false);
            add("Allow once", Approval.Allow, true);
            Controls.Add(body); Controls.Add(head); Controls.Add(bar);
            Style.Window(this);
            CancelButton = (Button)bar.Controls[0];
            AcceptButton = null; // nothing runs on Enter: a click decides
        }
    }
}
