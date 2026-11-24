// Global shortcuts the user can change: "Ctrl+Alt+Space" and the like, registered with RegisterHotKey (no keyboard
// hook: Windows tells Prosper when one of its own combinations is pressed, and nothing else is read), and the field
// in Settings that records a new one.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Prosper
{
    /// <summary>A key with its modifiers, as RegisterHotKey wants it.</summary>
    class Combo
    {
        public const uint Alt = 1, Ctrl = 2, Shift = 4, Win = 8;
        public uint Mods;
        public Keys Key = Keys.None;

        public bool None { get { return Key == Keys.None; } }

        /// <summary>Needs Ctrl, Alt or Win, so typing never fires it; F-keys may stand alone.</summary>
        public bool Usable
        {
            get { return !None && ((Mods & (Ctrl | Alt | Win)) != 0 || (Key >= Keys.F1 && Key <= Keys.F24)); }
        }

        public static Combo Parse(string s)
        {
            var c = new Combo();
            if (string.IsNullOrWhiteSpace(s)) return c;
            foreach (var raw in s.Split('+'))
            {
                string p = raw.Trim();
                switch (p.ToLowerInvariant())
                {
                    case "": break;
                    case "ctrl": case "control": c.Mods |= Ctrl; break;
                    case "alt": c.Mods |= Alt; break;
                    case "shift": c.Mods |= Shift; break;
                    case "win": c.Mods |= Win; break;
                    default: c.Key = KeyOf(p); break;
                }
            }
            return c;
        }

        public static Combo Of(uint mods, Keys key) { return new Combo { Mods = mods, Key = key }; }

        public override string ToString()
        {
            if (None) return "";
            return ModsText(Mods) + Name(Key);
        }

        public bool Same(Combo o) { return o != null && o.Mods == Mods && o.Key == Key; }

        public static string ModsText(uint m)
        {
            return ((m & Ctrl) != 0 ? "Ctrl+" : "") + ((m & Alt) != 0 ? "Alt+" : "") + ((m & Shift) != 0 ? "Shift+" : "") + ((m & Win) != 0 ? "Win+" : "");
        }

        public static string Name(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + (int)(k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Return: return "Enter";
                case Keys.Back: return "Backspace";
                case Keys.Prior: return "PageUp";
                case Keys.Next: return "PageDown";
                case Keys.Capital: return "CapsLock";
                case Keys.Snapshot: return "PrintScreen";
                case Keys.OemSemicolon: return ";";
                case Keys.Oemplus: return "=";
                case Keys.Oemcomma: return ",";
                case Keys.OemMinus: return "-";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Oemtilde: return "`";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemPipe: return "\\";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemQuotes: return "'";
                case Keys.Multiply: return "Num *";
                case Keys.Add: return "Num +";
                case Keys.Subtract: return "Num -";
                case Keys.Divide: return "Num /";
                case Keys.Decimal: return "Num .";
                default: return k.ToString();
            }
        }

        static Dictionary<string, Keys> names;
        static Keys KeyOf(string name)
        {
            if (names == null)
            {
                names = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase);
                foreach (Keys k in Enum.GetValues(typeof(Keys)))
                {
                    if (k == Keys.None || (k & Keys.Modifiers) != 0 || IsModifier(k)) continue;
                    string n = Name(k);
                    if (!names.ContainsKey(n)) names[n] = k;
                }
            }
            Keys found;
            return names.TryGetValue(name, out found) ? found : Keys.None;
        }

        public static bool IsModifier(Keys k)
        {
            return k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.ShiftKey || k == Keys.LShiftKey ||
                k == Keys.RShiftKey || k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu || k == Keys.LWin || k == Keys.RWin;
        }
    }

    /// <summary>What each shortcut does, its default, and where it is kept in settings.</summary>
    static class Shortcuts
    {
        public const int Dictate = 1, Assist = 2, Screen = 3, History = 4, Type = 5;
        public static readonly int[] All = { Dictate, Assist, Type, Screen, History };

        public static string Default(int id)
        {
            switch (id)
            {
                case Dictate: return "Ctrl+Alt+Space";
                case Assist: return "Ctrl+Alt+A";
                case Type: return "Ctrl+Alt+T";
                case Screen: return "Ctrl+Alt+S";
                default: return "Ctrl+Alt+H";
            }
        }

        public static string What(int id)
        {
            switch (id)
            {
                case Dictate: return "Dictate";
                case Assist: return "Assistant by voice";
                case Type: return "Type to the Assistant";
                case Screen: return "Add the screen while it listens";
                default: return "History and chat";
            }
        }

        public static string Get(int id)
        {
            var s = Settings.Current;
            string v;
            switch (id)
            {
                case Dictate: v = s.KeyDictate; break;
                case Assist: v = s.KeyAssist; break;
                case Type: v = s.KeyType; break;
                case Screen: v = s.KeyScreen; break;
                default: v = s.KeyHistory; break;
            }
            return v ?? Default(id);
        }

        public static void Set(int id, string v)
        {
            var s = Settings.Current;
            switch (id)
            {
                case Dictate: s.KeyDictate = v; break;
                case Assist: s.KeyAssist = v; break;
                case Type: s.KeyType = v; break;
                case Screen: s.KeyScreen = v; break;
                default: s.KeyHistory = v; break;
            }
        }

        /// <summary>"  (Ctrl+Alt+A)" for a menu item, or nothing when the shortcut is off.</summary>
        public static string Hint(int id)
        {
            var c = Combo.Parse(Get(id));
            return c.None ? "" : "  (" + c + ")";
        }
    }

    /// <summary>Receives the global shortcuts. Settings can pause them while a new one is being recorded.</summary>
    class Hotkeys : NativeWindow, IDisposable
    {
        public static Hotkeys Current;
        public event Action<int> Pressed;
        bool paused;

        public Hotkeys() { CreateHandle(new CreateParams()); Current = this; }

        /// <summary>Registers every shortcut from settings again; returns the ones another app already has.</summary>
        public List<string> Apply()
        {
            Clear();
            var taken = new List<string>();
            if (paused) return taken;
            foreach (int id in Shortcuts.All)
            {
                var c = Combo.Parse(Shortcuts.Get(id));
                if (!c.Usable) continue;
                if (!Native.RegisterHotKey(Handle, id, c.Mods | Native.MOD_NOREPEAT, (uint)c.Key)) taken.Add(c.ToString());
            }
            return taken;
        }

        void Clear() { foreach (int id in Shortcuts.All) Native.UnregisterHotKey(Handle, id); }

        /// <summary>While a shortcut field has the focus, so the keys reach it (and Prosper's own count as free).</summary>
        public static void Pause() { if (Current != null && !Current.paused) { Current.paused = true; Current.Clear(); } }
        public static void Resume() { if (Current != null && Current.paused) { Current.paused = false; Current.Apply(); } }

        /// <summary>Whether no other app holds [c] (tried by registering it for a moment).</summary>
        public static bool Free(Combo c)
        {
            if (!c.Usable) return true;
            var w = new NativeWindow();
            w.CreateHandle(new CreateParams());
            try
            {
                bool ok = Native.RegisterHotKey(w.Handle, 0x7F00, c.Mods | Native.MOD_NOREPEAT, (uint)c.Key);
                if (ok) Native.UnregisterHotKey(w.Handle, 0x7F00);
                return ok;
            }
            finally { w.DestroyHandle(); }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }

        public void Dispose() { Clear(); DestroyHandle(); if (Current == this) Current = null; }
    }

    /// <summary>
    /// A shortcut in Settings: shows its keys; click it and press new ones. Backspace turns it off, Esc keeps the old
    /// one. Says so in red when the keys need a modifier or another app (or another Prosper action) has them.
    /// </summary>
    class KeyField : Control
    {
        public event Action Changed;
        public Func<Combo, string> Check; // why [combo] can't be used, or null
        Combo value, before;
        string live, problem;
        bool over;

        public KeyField(string combo)
        {
            value = Combo.Parse(combo);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Cursor = Cursors.Hand; Font = Style.Caps;
            Size = new Size(Style.Px(290), Style.Px(34));
            Margin = new Padding(0, Style.Px(2), 0, Style.Px(2));
        }

        public Combo Value { get { return value; } }
        public void Reset(string combo) { value = Combo.Parse(combo); problem = null; live = null; Invalidate(); if (Changed != null) Changed(); }
        public bool HasProblem { get { return problem != null; } }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { before = value; live = null; Hotkeys.Pause(); Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { live = null; Hotkeys.Resume(); Invalidate(); base.OnLostFocus(e); }

        static bool WinDown()
        {
            return (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        // Every key, Alt combinations included, comes here first while the field has the focus.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
            if (msg.Msg != WM_KEYDOWN && msg.Msg != WM_SYSKEYDOWN) return base.ProcessCmdKey(ref msg, keyData);
            Keys k = keyData & Keys.KeyCode, m = keyData & Keys.Modifiers;
            uint mods = ((m & Keys.Control) != 0 ? Combo.Ctrl : 0) | ((m & Keys.Alt) != 0 ? Combo.Alt : 0) |
                ((m & Keys.Shift) != 0 ? Combo.Shift : 0) | (WinDown() ? Combo.Win : 0);
            if (k == Keys.Tab && mods == 0) return base.ProcessCmdKey(ref msg, keyData);
            if (k == Keys.Escape && mods == 0) { value = before ?? value; problem = null; live = null; Parent.SelectNextControl(this, true, true, true, true); Invalidate(); return true; }
            if ((k == Keys.Back || k == Keys.Delete) && mods == 0) { value = new Combo(); problem = null; live = null; Done(); return true; }
            if (Combo.IsModifier(k)) { live = Combo.ModsText(mods); Invalidate(); return true; }
            var c = Combo.Of(mods, k);
            live = null;
            if (!c.Usable) { problem = "Add Ctrl, Alt or Win"; Invalidate(); return true; }
            string why = Check != null ? Check(c) : null;
            value = c; problem = why;
            Done();
            return true;
        }

        void Done() { Invalidate(); if (Changed != null) Changed(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Style.Behind(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Floating.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Style.Px(8)))
            {
                using (var b = new SolidBrush(over && !Focused ? Color.FromArgb(36, 36, 41) : Style.Field)) g.FillPath(b, p);
                Color edge = problem != null ? Style.Bad : Focused ? Style.Gold : Style.Line;
                using (var pen = new Pen(edge)) g.DrawPath(pen, p);
            }
            float x = Style.Px(6);
            string shown = live != null ? live : value.ToString();
            if (shown.Length == 0)
            {
                string t = Focused ? "Press the new keys…" : "Off";
                TextRenderer.DrawText(g, t, Style.Small, new Rectangle(Style.Px(12), 0, Width, Height), Style.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                x = TextRenderer.MeasureText(t, Style.Small).Width + Style.Px(18);
            }
            else
            {
                foreach (var k in shown.TrimEnd('+').Split('+'))
                {
                    string key = k.Length == 0 ? "+" : k;
                    float w = TextRenderer.MeasureText(key, Font).Width + Style.Px(12);
                    var r = new RectangleF(x, Style.Px(5), w, Height - Style.Px(11));
                    using (var p = Floating.Round(r, Style.Px(5)))
                    {
                        using (var b = new SolidBrush(Style.Raised)) g.FillPath(b, p);
                        using (var pen = new Pen(Style.Line)) g.DrawPath(pen, p);
                    }
                    TextRenderer.DrawText(g, key, Font, Rectangle.Round(r), live != null ? Style.Muted : Style.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    x += w + Style.Px(5);
                }
                x += Style.Px(4);
            }
            string note = problem ?? (Focused ? (live != null ? "" : "Esc keeps it") : over ? "Click to change" : "");
            if (note.Length > 0)
                TextRenderer.DrawText(g, note, Style.Small, new Rectangle((int)x, 0, Width - (int)x - Style.Px(10), Height),
                    problem != null ? Style.Bad : Style.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }
}
