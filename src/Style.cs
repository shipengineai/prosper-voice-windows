// The app's look, matching the Prosper bar and the logo: dark glass surfaces, soft light text and gold as the one
// accent. Rounded buttons, switches, fields and cards are drawn here so every window looks the same.
// Windows draws the title bars and scrollbars dark too (Windows 10 2004+ / Windows 11).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Prosper
{
    static class Style
    {
        public static readonly Color Bg = Color.FromArgb(17, 17, 19), Surface = Color.FromArgb(24, 24, 27), Raised = Color.FromArgb(39, 39, 42),
            Field = Color.FromArgb(30, 30, 34), Line = Color.FromArgb(46, 46, 51), Text = Color.FromArgb(244, 244, 245),
            Muted = Color.FromArgb(161, 161, 170), Faint = Color.FromArgb(113, 113, 122), Gold = Color.FromArgb(232, 194, 106),
            GoldEdge = Color.FromArgb(114, 97, 58), Ok = Color.FromArgb(74, 222, 128), Bad = Color.FromArgb(248, 113, 113);

        public static readonly Font Body = new Font("Segoe UI", 9.5f), Strong = new Font("Segoe UI Semibold", 9.5f),
            Small = new Font("Segoe UI", 8.5f), Caps = new Font("Segoe UI Semibold", 8f),
            Title = new Font("Segoe UI Semibold", 15f), Heading = new Font("Segoe UI Semibold", 11f), Mono = new Font("Consolas", 9f);

        public static int Px(float dp) { return Floating.Px(dp); }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        /// <summary>Dark title bar in the app's own colour, where Windows allows it.</summary>
        static void DarkTitle(IntPtr h)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4); // older Windows 10 builds
                int caption = Bg.R | (Bg.G << 8) | (Bg.B << 16), border = Line.R | (Line.G << 8) | (Line.B << 16);
                DwmSetWindowAttribute(h, 35, ref caption, 4); // Windows 11 caption colour
                DwmSetWindowAttribute(h, 34, ref border, 4);  // Windows 11 border colour
            }
            catch { }
        }

        public static void DarkScrollbars(Control c)
        {
            c.HandleCreated += (s, e) => { try { SetWindowTheme(((Control)s).Handle, "DarkMode_Explorer", null); } catch { } };
            if (c.IsHandleCreated) try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        /// <summary>A whole window in the Prosper look, with the logo as its icon: call at the end of its constructor.</summary>
        public static void Window(Form f)
        {
            f.BackColor = Bg; f.ForeColor = Text; f.Font = Body;
            f.Icon = Logo.AppIcon; f.ShowIcon = true;
            f.HandleCreated += (s, e) => DarkTitle(f.Handle);
            if (f.IsHandleCreated) DarkTitle(f.Handle);
            Apply(f);
        }

        /// <summary>Styles [root] and everything in it that hasn't been styled by hand.</summary>
        public static void Apply(Control root)
        {
            if (root is ScrollableControl && ((ScrollableControl)root).AutoScroll) DarkScrollbars(root);
            foreach (Control c in root.Controls)
            {
                var tb = c as TextBox;
                if (tb != null && !(tb.Parent is FieldBox)) { tb.BackColor = Field; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.FixedSingle; }
                if (tb != null) DarkScrollbars(tb);
                var lb = c as ListBox;
                if (lb != null) { lb.BackColor = Surface; lb.ForeColor = Text; DarkScrollbars(lb); }
                var sc = c as SplitContainer;
                if (sc != null) { sc.BackColor = Line; sc.Panel1.BackColor = Surface; sc.Panel2.BackColor = Bg; }
                Apply(c);
            }
        }

        /// <summary>A rounded button: gold for the main action, a quiet raised one otherwise.</summary>
        public static Button Btn(string text, bool primary = false) { return new PillButton(text, primary); }

        /// <summary>A short line of text in [color] (muted by default) that wraps at [width].</summary>
        public static Label Note(string text, int width, Color? color = null, Font font = null)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = color ?? Muted, Font = font ?? Small, Margin = new Padding(0, Px(4), 0, Px(2)) };
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>What sits behind a custom-drawn control, for its corners.</summary>
        public static Color Behind(Control c)
        {
            for (var p = c.Parent; p != null; p = p.Parent) if (p.BackColor.A == 255) return p.BackColor;
            return Bg;
        }

        public static readonly ToolStripRenderer Menu = new MenuRenderer();

        /// <summary>A dark menu with the logo as its header.</summary>
        public static ContextMenuStrip NewMenu()
        {
            var m = new ContextMenuStrip { Renderer = Menu, Font = Body, ShowImageMargin = false, Padding = new Padding(0, Px(4), 0, Px(4)) };
            var head = new ToolStripLabel("Prosper Voice", Logo.Image(Px(18))) { Font = Strong, ForeColor = Gold, Padding = new Padding(Px(4), Px(4), 0, Px(4)), ImageAlign = ContentAlignment.MiddleLeft, TextImageRelation = TextImageRelation.ImageBeforeText };
            m.Items.Add(head);
            m.Items.Add(new ToolStripSeparator());
            return m;
        }

        class MenuRenderer : ToolStripProfessionalRenderer
        {
            public MenuRenderer() : base(new MenuColors()) { RoundedEdges = true; }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!(e.Item is ToolStripLabel)) e.TextColor = e.Item.Enabled ? Text : Muted;
                base.OnRenderItemText(e);
            }
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(Px(4), 1, e.Item.Width - Px(8), e.Item.Height - 2);
                using (var p = Floating.Round(r, Px(6))) using (var b = new SolidBrush(Raised)) e.Graphics.FillPath(b, p);
                using (var b = new SolidBrush(Gold)) e.Graphics.FillRectangle(b, r.X, r.Y + Px(6), Px(2.5f), r.Height - Px(12));
            }
            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var p = new Pen(Line)) e.Graphics.DrawLine(p, Px(10), y, e.Item.Width - Px(10), y);
            }
        }

        class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Raised; } }
            public override Color MenuItemSelected { get { return Raised; } }
            public override Color MenuItemSelectedGradientBegin { get { return Raised; } }
            public override Color MenuItemSelectedGradientEnd { get { return Raised; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Line; } }
        }
    }

    /// <summary>A rounded button, drawn by hand so it matches the bar.</summary>
    class PillButton : Button
    {
        public bool Primary;
        protected bool over, pressed;

        public PillButton(string text, bool primary)
        {
            Primary = primary; Text = text; Font = Style.Strong; Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Margin = new Padding(0, 0, Style.Px(8), 0);
            Fit();
        }

        public void Fit() { Size = new Size(TextRenderer.MeasureText(Text, Font).Width + Style.Px(30), Style.Px(32)); }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); }
        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected Color Fill
        {
            get
            {
                if (!Enabled) return Primary ? Style.GoldEdge : Style.Surface;
                if (Primary) return pressed ? Color.FromArgb(214, 176, 90) : over ? Color.FromArgb(242, 210, 138) : Style.Gold;
                return pressed ? Style.Line : over ? Color.FromArgb(52, 52, 58) : Style.Raised;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Style.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = Floating.Round(r, Style.Px(8)))
            {
                using (var b = new SolidBrush(Fill)) g.FillPath(b, p);
                Color edge = Focused && ShowFocusCues ? Style.Gold : Primary ? Color.Transparent : Style.Line;
                if (edge.A > 0) using (var pen = new Pen(edge)) g.DrawPath(pen, p);
            }
            Color fg = !Enabled ? Style.Faint : Primary ? Style.Bg : Style.Text;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>A round or rounded-square button with a drawn icon (mic, send, attach…).</summary>
    class GlyphButton : PillButton
    {
        public Action<Graphics, float, float, float, Color> Glyph;
        public bool Active; // drawn red: recording
        public GlyphButton(Action<Graphics, float, float, float, Color> glyph, bool primary, string tip, int size = 34) : base("", primary)
        {
            Glyph = glyph; Size = new Size(Style.Px(size), Style.Px(size)); Margin = new Padding(Style.Px(4), 0, 0, 0);
            if (tip != null) new ToolTip().SetToolTip(this, tip);
        }
        protected override void OnTextChanged(EventArgs e) { } // no text to fit
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Style.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = Active ? Style.Bad : Primary || over || pressed || !Enabled ? Fill : Color.Transparent;
            if (fill.A > 0) using (var b = new SolidBrush(fill)) g.FillEllipse(b, r);
            Color fg = !Enabled ? Style.Faint : Active || Primary ? Style.Bg : over ? Style.Text : Style.Muted;
            Glyph(g, Width / 2f, Height / 2f, Width * 0.5f, fg);
        }
    }

    /// <summary>An on/off switch with its label: a CheckBox underneath, so Checked works as usual.</summary>
    class Toggle : CheckBox
    {
        readonly int width;
        bool over;
        public Toggle(string text, int width)
        {
            this.width = width; Text = text; Font = Style.Body; Cursor = Cursors.Hand; AutoSize = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            var t = TextRenderer.MeasureText(text, Font, new Size(width - Style.Px(52), 0), TextFormatFlags.WordBreak);
            Size = new Size(width, Math.Max(Style.Px(26), t.Height + Style.Px(6)));
            Margin = new Padding(0, Style.Px(5), 0, Style.Px(5));
        }
        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Style.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float px = Floating.Dpi, w = 36 * px, h = 20 * px, y = 3 * px;
            var track = new RectangleF(0.5f, y, w, h);
            using (var p = Floating.Round(track, h / 2))
            {
                using (var b = new SolidBrush(Checked ? Style.Gold : over ? Color.FromArgb(52, 52, 58) : Style.Raised)) g.FillPath(b, p);
                if (!Checked) using (var pen = new Pen(Style.Line)) g.DrawPath(pen, p);
                if (Focused && ShowFocusCues) using (var pen = new Pen(Style.Text)) g.DrawPath(pen, p);
            }
            float k = 14 * px, kx = Checked ? track.Right - k - 3 * px : track.X + 3 * px;
            using (var b = new SolidBrush(Checked ? Style.Bg : Style.Muted)) g.FillEllipse(b, kx, y + 3 * px, k, k);
            var tr = new Rectangle(Style.Px(50), Style.Px(3), width - Style.Px(52), Height);
            TextRenderer.DrawText(g, Text, Font, tr, Enabled ? Style.Text : Style.Faint, TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
        }
    }

    /// <summary>A text box in a rounded field that lights up gold while you type in it.</summary>
    class FieldBox : Panel
    {
        public readonly TextBox Box;
        public FieldBox(TextBox box, int width, int height = 0)
        {
            Box = box;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            box.BorderStyle = BorderStyle.None; box.BackColor = Style.Field; box.ForeColor = Style.Text;
            if (box.Font.Name != Style.Mono.Name) box.Font = Style.Body;
            BackColor = Style.Field; Cursor = Cursors.IBeam;
            Padding = new Padding(Style.Px(11), Style.Px(8), Style.Px(8), Style.Px(7));
            Size = new Size(width, height > 0 ? height : box.PreferredHeight + Style.Px(16));
            Margin = new Padding(0, Style.Px(2), 0, Style.Px(2));
            box.Dock = DockStyle.Fill;
            Controls.Add(box);
            box.GotFocus += (s, e) => Invalidate();
            box.LostFocus += (s, e) => Invalidate();
            Click += (s, e) => box.Focus();
            Style.DarkScrollbars(box);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Style.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Floating.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Style.Px(8)))
            {
                using (var b = new SolidBrush(Style.Field)) g.FillPath(b, p);
                using (var pen = new Pen(Box.Focused ? Style.GoldEdge : Style.Line)) g.DrawPath(pen, p);
            }
        }
    }

    /// <summary>A rounded panel that stacks its contents top to bottom: one group of settings.</summary>
    class Card : FlowLayoutPanel
    {
        public Card(int width)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlowDirection = FlowDirection.TopDown; WrapContents = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Style.Surface; ForeColor = Style.Text;
            Padding = new Padding(Style.Px(20), Style.Px(16), Style.Px(20), Style.Px(16));
            MinimumSize = new Size(width, 0); MaximumSize = new Size(width, 0);
            Margin = new Padding(0, 0, 0, Style.Px(14));
        }
        public int Inner { get { return MinimumSize.Width - Padding.Horizontal; } }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Style.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Floating.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Style.Px(12)))
            {
                using (var b = new SolidBrush(Style.Surface)) g.FillPath(b, p);
                using (var pen = new Pen(Style.Line)) g.DrawPath(pen, p);
            }
        }
    }

    /// <summary>The logo, drawn at the control's size.</summary>
    class LogoView : Control
    {
        public LogoView(int size)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(size, size); BackColor = Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Style.Behind(this)); Logo.Draw(e.Graphics, new RectangleF(0, 0, Width, Height)); }
    }

    /// <summary>Keyboard keys drawn as small caps: "Ctrl+Alt+Space".</summary>
    class KeyCaps : Control
    {
        readonly string[] keys;
        public KeyCaps(string combo)
        {
            keys = combo.Split('+'); Font = Style.Caps;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            int w = 0; foreach (var k in keys) w += TextRenderer.MeasureText(k, Font).Width + Style.Px(18);
            Size = new Size(w, Style.Px(26)); Margin = new Padding(0, Style.Px(3), Style.Px(12), Style.Px(3));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Style.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = 0;
            foreach (var k in keys)
            {
                float w = TextRenderer.MeasureText(k, Font).Width + Style.Px(12);
                var r = new RectangleF(x + 0.5f, 0.5f, w, Height - Style.Px(3));
                using (var p = Floating.Round(new RectangleF(r.X, r.Y + Style.Px(2), r.Width, r.Height), Style.Px(5))) using (var b = new SolidBrush(Style.Bg)) g.FillPath(b, p);
                using (var p = Floating.Round(r, Style.Px(5)))
                {
                    using (var b = new SolidBrush(Style.Raised)) g.FillPath(b, p);
                    using (var pen = new Pen(Style.Line)) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, k, Font, Rectangle.Round(r), Style.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                x += w + Style.Px(6);
            }
        }
    }

    /// <summary>Small line icons, drawn at (cx, cy) about [s] across.</summary>
    static class Glyphs
    {
        static Pen P(Color c, float s) { return new Pen(c, Math.Max(1.4f, s * 0.11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }; }

        public static void Mic(Graphics g, float cx, float cy, float s, Color c)
        {
            float u = s / 16;
            using (var p = P(c, s))
            {
                using (var body = Floating.Round(new RectangleF(cx - 3 * u, cy - 7 * u, 6 * u, 10 * u), 3 * u)) g.DrawPath(p, body);
                g.DrawArc(p, cx - 5.5f * u, cy - 4.5f * u, 11 * u, 10 * u, 0, 180);
                g.DrawLine(p, cx, cy + 5.5f * u, cx, cy + 7.5f * u);
            }
        }
        public static void Stop(Graphics g, float cx, float cy, float s, Color c)
        {
            float u = s / 16;
            using (var p = Floating.Round(new RectangleF(cx - 4.5f * u, cy - 4.5f * u, 9 * u, 9 * u), 2 * u)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }
        public static void Send(Graphics g, float cx, float cy, float s, Color c)
        {
            float u = s / 16;
            using (var p = P(c, s * 1.15f))
            {
                g.DrawLine(p, cx, cy + 6 * u, cx, cy - 6 * u);
                g.DrawLines(p, new[] { new PointF(cx - 5 * u, cy - 1 * u), new PointF(cx, cy - 6 * u), new PointF(cx + 5 * u, cy - 1 * u) });
            }
        }
        public static void Attach(Graphics g, float cx, float cy, float s, Color c)
        {
            float u = s / 16;
            var state = g.Save();
            g.TranslateTransform(cx, cy); g.RotateTransform(40);
            using (var p = P(c, s))
            using (var path = new GraphicsPath())
            {
                path.AddLine(2.5f * u, -3 * u, 2.5f * u, 4 * u);
                path.AddArc(-3.5f * u, 1 * u, 6 * u, 6 * u, 0, 180);
                path.AddLine(-3.5f * u, 4 * u, -3.5f * u, -5 * u);
                path.AddArc(-3.5f * u, -8.5f * u, 4.5f * u, 4.5f * u, 180, 180);
                path.AddLine(1 * u, -6 * u, 1 * u, 2.5f * u);
                g.DrawPath(p, path);
            }
            g.Restore(state);
        }
        public static void Plus(Graphics g, float cx, float cy, float s, Color c)
        {
            float u = s / 16;
            using (var p = P(c, s * 1.1f)) { g.DrawLine(p, cx - 5 * u, cy, cx + 5 * u, cy); g.DrawLine(p, cx, cy - 5 * u, cx, cy + 5 * u); }
        }
        public static void Trash(Graphics g, float cx, float cy, float s, Color c)
        {
            float u = s / 16;
            using (var p = P(c, s))
            {
                g.DrawLine(p, cx - 6 * u, cy - 4 * u, cx + 6 * u, cy - 4 * u);
                g.DrawLines(p, new[] { new PointF(cx - 2 * u, cy - 4 * u), new PointF(cx - 2 * u, cy - 6 * u), new PointF(cx + 2 * u, cy - 6 * u), new PointF(cx + 2 * u, cy - 4 * u) });
                g.DrawLines(p, new[] { new PointF(cx - 4.5f * u, cy - 4 * u), new PointF(cx - 3.5f * u, cy + 6 * u), new PointF(cx + 3.5f * u, cy + 6 * u), new PointF(cx + 4.5f * u, cy - 4 * u) });
            }
        }
        public static void Sparkle(Graphics g, float cx, float cy, float s, Color c) { Bar.Sparkle(g, cx, cy, s * 0.45f, c); }
        public static void Pen(Graphics g, float cx, float cy, float s, Color c)
        {
            float u = s / 16;
            using (var p = P(c, s))
            {
                g.DrawPolygon(p, new[] { new PointF(cx + 3 * u, cy - 6 * u), new PointF(cx + 6 * u, cy - 3 * u), new PointF(cx - 3 * u, cy + 6 * u), new PointF(cx - 6.5f * u, cy + 6.5f * u), new PointF(cx - 6 * u, cy + 3 * u) });
            }
        }
        public static void Wave(Graphics g, float cx, float cy, float s, Color c)
        {
            float u = s / 16; float[] h = { 3, 7, 11, 7, 3 };
            using (var p = P(c, s * 1.1f)) for (int i = 0; i < h.Length; i++) { float x = cx + (i - 2) * 3.4f * u; g.DrawLine(p, x, cy - h[i] / 2 * u, x, cy + h[i] / 2 * u); }
        }
    }
}
