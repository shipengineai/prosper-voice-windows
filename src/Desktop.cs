// Windows plumbing: the microphone, the clipboard, keystrokes into the app in front, its selection, and screenshots.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Prosper
{
    static class Native
    {
        public const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern int mciSendString(string cmd, StringBuilder ret, int len, IntPtr cb);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern bool mciGetErrorString(int err, StringBuilder buf, int len);

        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

        static INPUT Key(ushort vk, bool up)
        {
            var i = new INPUT { type = 1 };
            i.u.ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u };
            return i;
        }

        /// <summary>Ctrl+key into whatever window is in front (Ctrl+C, Ctrl+V).</summary>
        public static void CtrlKey(char key)
        {
            INPUT[] seq = { Key(0x11, false), Key(key, false), Key(key, true), Key(0x11, true) };
            SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(INPUT)));
        }

        public static bool ModifiersDown()
        {
            foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
            return false;
        }

        /// <summary>Waits until the hotkey's Ctrl/Alt are let go, so a Ctrl+C we send isn't read as Ctrl+Alt+C.</summary>
        public static async Task ModifiersUp()
        {
            for (int i = 0; i < 30 && ModifiersDown(); i++) await Task.Delay(50);
        }
    }

    /// <summary>Records the default microphone with Windows' own MCI audio: a 16 kHz mono WAV, no extra libraries.</summary>
    class Recorder
    {
        readonly string Alias;
        readonly string FileName;
        public bool Recording { get; private set; }
        public Recorder(string alias) { Alias = alias; FileName = alias + ".wav"; } // one per place that records

        static int Mci(string cmd) { return Native.mciSendString(cmd, null, 0, IntPtr.Zero); }
        static string Error(int code)
        {
            var b = new StringBuilder(256); Native.mciGetErrorString(code, b, b.Capacity); return b.ToString();
        }

        /// <summary>Starts recording; returns null when it started, or why it couldn't.</summary>
        public string Start()
        {
            Mci("close " + Alias);
            int r = Mci("open new type waveaudio alias " + Alias);
            if (r != 0) return Error(r);
            Mci("set " + Alias + " alignment 2 bitspersample 16 samplespersec 16000 channels 1 bytespersec 32000");
            r = Mci("record " + Alias);
            if (r != 0) { Mci("close " + Alias); return Error(r); }
            Recording = true;
            return null;
        }

        /// <summary>Stops and writes the clip to a temp file; the file and its loudest sample (0 = silence).</summary>
        public string Stop(out int peak)
        {
            peak = 0;
            if (!Recording) return null;
            Recording = false;
            Directory.CreateDirectory(Paths.Temp);
            string path = Path.Combine(Paths.Temp, FileName);
            try { File.Delete(path); } catch { }
            Mci("stop " + Alias);
            int r = Mci("save " + Alias + " \"" + path + "\" wait");
            Mci("close " + Alias);
            if (r != 0 || !File.Exists(path)) { Log.Write("recorder: save failed " + r); return null; }
            peak = Peak(path);
            return path;
        }

        public void Cancel() { if (Recording) { Recording = false; Mci("stop " + Alias); Mci("close " + Alias); } }

        static int Peak(string wav)
        {
            try
            {
                byte[] b = File.ReadAllBytes(wav);
                int start = 44;
                for (int i = 12; i + 8 < b.Length; i++)
                    if (b[i] == 'd' && b[i + 1] == 'a' && b[i + 2] == 't' && b[i + 3] == 'a') { start = i + 8; break; }
                int max = 0;
                for (int i = start; i + 1 < b.Length; i += 2) { int v = Math.Abs((short)(b[i] | (b[i + 1] << 8))); if (v > max) max = v; }
                return max;
            }
            catch { return 1; }
        }
    }

    /// <summary>The clipboard, borrowed briefly and given back. Text Prosper only pastes is kept out of clipboard history.</summary>
    static class Clip
    {
        public static IDataObject Snapshot()
        {
            try
            {
                var src = Clipboard.GetDataObject();
                if (src == null) return null;
                var copy = new DataObject();
                foreach (var f in src.GetFormats(false))
                {
                    try { var d = src.GetData(f, false); if (d != null) copy.SetData(f, false, d); } catch { }
                }
                return copy.GetFormats().Length > 0 ? copy : null;
            }
            catch { return null; }
        }

        public static void Restore(IDataObject saved)
        {
            try { if (saved != null) Clipboard.SetDataObject(saved, true, 3, 100); else Clipboard.Clear(); } catch { }
        }

        static DataObject Text(string text, bool keepInHistory)
        {
            var d = new DataObject();
            d.SetText(text, TextDataFormat.UnicodeText);
            // Windows' clipboard history (Win+V) and cloud clipboard sync skip data marked like this.
            d.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            if (!keepInHistory)
            {
                d.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
                d.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0 }));
            }
            return d;
        }

        /// <summary>Leaves [text] on the clipboard for the user to paste (never synced to the cloud).</summary>
        public static void Copy(string text)
        {
            try { Clipboard.SetDataObject(Text(text, true), true, 3, 100); } catch { }
        }

        /// <summary>Types [text] into the app in front by pasting it, then puts back what the clipboard held.</summary>
        public static async Task Paste(string text)
        {
            var saved = Snapshot();
            try { Clipboard.SetDataObject(Text(text, false), true, 3, 100); } catch { return; }
            await Native.ModifiersUp();
            Native.CtrlKey('V');
            await Task.Delay(600); // the app reads the clipboard after the keystroke
            Restore(saved);
        }

        /// <summary>The text selected in the app in front, through Ctrl+C; the clipboard is put back after.</summary>
        public static async Task<string> SelectionByCopy()
        {
            var saved = Snapshot();
            uint before = Native.GetClipboardSequenceNumber();
            await Native.ModifiersUp();
            Native.CtrlKey('C');
            string got = null;
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(40);
                if (Native.GetClipboardSequenceNumber() != before)
                {
                    await Task.Delay(30);
                    try { if (Clipboard.ContainsText()) got = Clipboard.GetText(); } catch { }
                    break;
                }
            }
            if (Native.GetClipboardSequenceNumber() != before) Restore(saved);
            return string.IsNullOrWhiteSpace(got) ? null : got;
        }
    }

    /// <summary>What's in front when a recording starts: the window, its app, whether a text field has focus, the selection.</summary>
    class Target
    {
        public IntPtr Window;
        public string App;
        public bool Editable = true;  // unknown counts as editable: the text is pasted (and is in history anyway)
        public string Selected;

        public static async Task<Target> Capture()
        {
            var t = new Target { Window = Native.GetForegroundWindow() };
            t.App = AppName(t.Window);
            bool knows = false;
            // UI Automation reads the focused field and its exact selection; it can hang on a busy app, so it gets a moment.
            var probe = Task.Run(() =>
            {
                try
                {
                    var el = AutomationElement.FocusedElement;
                    if (el == null) return Tuple.Create(false, false, (string)null);
                    var ct = el.Current.ControlType;
                    bool editable = ct == ControlType.Edit || ct == ControlType.Document;
                    object p;
                    if (!editable && el.TryGetCurrentPattern(ValuePattern.Pattern, out p)) editable = !((ValuePattern)p).Current.IsReadOnly;
                    if (el.TryGetCurrentPattern(TextPattern.Pattern, out p))
                    {
                        string sel = string.Concat(((TextPattern)p).GetSelection().Select(r => r.GetText(20000)));
                        return Tuple.Create(editable, true, sel);
                    }
                    return Tuple.Create(editable, false, (string)null);
                }
                catch { return null; }
            });
            if (await Task.WhenAny(probe, Task.Delay(700)) == probe && probe.Result != null)
            {
                t.Editable = probe.Result.Item1;
                knows = probe.Result.Item2;
                if (knows && !string.IsNullOrWhiteSpace(probe.Result.Item3)) t.Selected = probe.Result.Item3;
            }
            // Without a readable selection, try Ctrl+C (an app that copies a whole line when nothing is selected is
            // avoided where UI Automation answered "nothing selected"). Never in a terminal: there Ctrl+C stops the
            // program that is running.
            if (!knows && !IsTerminal(t.Window)) t.Selected = await Clip.SelectionByCopy();
            if (t.Selected != null && t.Selected.Length > 20000) t.Selected = t.Selected.Substring(0, 20000);
            return t;
        }

        static readonly string[] Terminals = {
            "windowsterminal", "openconsole", "conhost", "cmd", "powershell", "pwsh", "wt", "mintty", "putty", "kitty",
            "conemu", "conemu64", "alacritty", "wezterm-gui", "tabby", "hyper", "mobaxterm", "termius", "wsl", "bash",
        };

        public static bool IsTerminal(IntPtr w)
        {
            try
            {
                uint pid; Native.GetWindowThreadProcessId(w, out pid);
                if (pid == 0) return true; // no window: don't risk it
                return Terminals.Contains(Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant());
            }
            catch { return true; } // unknown: don't risk it
        }

        public static string AppName(IntPtr w)
        {
            try
            {
                uint pid; Native.GetWindowThreadProcessId(w, out pid);
                var p = Process.GetProcessById((int)pid);
                try
                {
                    string d = p.MainModule.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
                }
                catch { }
                return p.ProcessName;
            }
            catch { return "an app"; }
        }

        public bool IsOurs
        {
            get { uint pid; Native.GetWindowThreadProcessId(Window, out pid); return pid == (uint)Process.GetCurrentProcess().Id; }
        }
    }

    static class Shot
    {
        /// <summary>The monitor [window] is on, as a JPEG at most 1280 px on its long side.</summary>
        public static byte[] Take(IntPtr window)
        {
            var bounds = (window != IntPtr.Zero ? Screen.FromHandle(window) : Screen.PrimaryScreen).Bounds;
            using (var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(full)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                float scale = Math.Min(1f, 1280f / Math.Max(bounds.Width, bounds.Height));
                int w = (int)(bounds.Width * scale), h = (int)(bounds.Height * scale);
                using (var small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(full, 0, 0, w, h);
                    }
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    var p = new EncoderParameters(1);
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                    using (var ms = new MemoryStream()) { small.Save(ms, codec, p); return ms.ToArray(); }
                }
            }
        }
    }
}
