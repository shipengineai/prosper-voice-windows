// The microphone, through Windows' own waveIn audio (winmm): 16 kHz mono PCM written to a WAV file, plus a live
// level for the waveform. No extra libraries, no admin rights.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Prosper
{
    class Recorder
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

        [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, uint device, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInStop(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveInGetErrorText(int err, StringBuilder text, int size);

        const uint WAVE_MAPPER = 0xFFFFFFFF, WHDR_DONE = 1;
        const int Rate = 16000, BufferMs = 50, Buffers = 8;
        static readonly int HdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
        static readonly int RecordedOffset = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwBytesRecorded");

        readonly string fileName;
        IntPtr device;
        readonly List<IntPtr> headers = new List<IntPtr>();
        MemoryStream pcm;
        Thread pump;
        volatile bool running;
        volatile float level;
        int peak;

        public Recorder(string name) { fileName = name + ".wav"; } // one per place that records
        public bool Recording { get { return running; } }

        /// <summary>The voice level right now, 0 (silence) to 1 (loud), for the waveform.</summary>
        public float Level { get { return running ? level : 0f; } }

        /// <summary>Starts recording; returns null when it started, or why it couldn't.</summary>
        public string Start()
        {
            if (running) return null;
            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * 2, nBlockAlign = 2, wBitsPerSample = 16 };
            int r = waveInOpen(out device, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) return Error(r);
            pcm = new MemoryStream(); peak = 0; level = 0;
            int bytes = Rate * 2 * BufferMs / 1000;
            for (int i = 0; i < Buffers; i++)
            {
                var hdr = Marshal.AllocHGlobal(HdrSize);
                var w = new WAVEHDR { lpData = Marshal.AllocHGlobal(bytes), dwBufferLength = (uint)bytes };
                Marshal.StructureToPtr(w, hdr, false);
                waveInPrepareHeader(device, hdr, HdrSize);
                waveInAddBuffer(device, hdr, HdrSize);
                headers.Add(hdr);
            }
            r = waveInStart(device);
            if (r != 0) { Close(); return Error(r); }
            running = true;
            pump = new Thread(Pump) { IsBackground = true, Name = "Prosper mic" };
            pump.Start();
            return null;
        }

        /// <summary>Hands each filled buffer's audio to the file and the level meter, then gives it back to Windows.</summary>
        void Pump()
        {
            while (running)
            {
                foreach (var hdr in headers.ToArray())
                    if ((Marshal.ReadInt32(hdr, FlagsOffset) & WHDR_DONE) != 0 && running) { Take(hdr); waveInAddBuffer(device, hdr, HdrSize); }
                Thread.Sleep(15);
            }
        }

        void Take(IntPtr hdr)
        {
            var w = (WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR));
            int n = Marshal.ReadInt32(hdr, RecordedOffset);
            if (n <= 0) return;
            var buf = new byte[n];
            Marshal.Copy(w.lpData, buf, 0, n);
            lock (pcm) pcm.Write(buf, 0, n);
            double sum = 0; int max = 0;
            for (int i = 0; i + 1 < n; i += 2)
            {
                int v = (short)(buf[i] | (buf[i + 1] << 8));
                sum += (double)v * v; int a = Math.Abs(v); if (a > max) max = a;
            }
            if (max > peak) peak = max;
            // Loudness in decibels, mapped so a quiet room reads 0 (about -58 dB) and speech fills most of the bar.
            double rms = Math.Sqrt(sum / Math.Max(1, n / 2));
            double db = 20 * Math.Log10(Math.Max(1, rms) / 32768.0);
            level = (float)Math.Max(0, Math.Min(1, (db + 58) / 36));
        }

        /// <summary>Stops and writes the clip to a temp WAV file; the file and its loudest sample (0 = silence).</summary>
        public string Stop(out int loudest)
        {
            loudest = 0;
            if (!running) return null;
            running = false;
            if (pump != null) pump.Join(500);
            waveInStop(device);
            waveInReset(device); // hands back the buffers in flight, marked done
            foreach (var hdr in headers) if ((Marshal.ReadInt32(hdr, FlagsOffset) & WHDR_DONE) != 0) Take(hdr);
            byte[] data; lock (pcm) data = pcm.ToArray();
            Close();
            loudest = peak;
            if (data.Length == 0) return null;
            Directory.CreateDirectory(Paths.Temp);
            string path = Path.Combine(Paths.Temp, fileName);
            using (var f = new FileStream(path, FileMode.Create))
            using (var b = new BinaryWriter(f))
            {
                b.Write(Encoding.ASCII.GetBytes("RIFF")); b.Write(36 + data.Length); b.Write(Encoding.ASCII.GetBytes("WAVE"));
                b.Write(Encoding.ASCII.GetBytes("fmt ")); b.Write(16); b.Write((short)1); b.Write((short)1); b.Write(Rate); b.Write(Rate * 2); b.Write((short)2); b.Write((short)16);
                b.Write(Encoding.ASCII.GetBytes("data")); b.Write(data.Length); b.Write(data);
            }
            return path;
        }

        public void Cancel()
        {
            if (!running) return;
            running = false;
            if (pump != null) pump.Join(500);
            waveInStop(device); waveInReset(device);
            Close();
        }

        void Close()
        {
            foreach (var hdr in headers)
            {
                waveInUnprepareHeader(device, hdr, HdrSize);
                var w = (WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR));
                Marshal.FreeHGlobal(w.lpData); Marshal.FreeHGlobal(hdr);
            }
            headers.Clear();
            if (device != IntPtr.Zero) { waveInClose(device); device = IntPtr.Zero; }
            level = 0;
        }

        static string Error(int code)
        {
            var b = new StringBuilder(256); waveInGetErrorText(code, b, b.Capacity);
            return b.Length > 0 ? b.ToString() : "audio error " + code;
        }
    }
}
