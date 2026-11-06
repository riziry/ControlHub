// Software keyboard effects (the BIOS on this machine has no breathe/cycle): ControlHub animates
// brightness/colour itself with the same LED commands CC40 uses. Frames are only sent when the
// value changes; nothing is persisted per frame. Idle handling mirrors the auto-off timer.
// Every effect is a pure function of time (plus a few sampled inputs), so the on-screen preview
// and the real keyboard always show the same frame.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;

namespace ControlHub
{
    static class KbFx
    {
        // ids are stored in settings: append only
        public enum Kind { Static = 0, Breathe = 1, Cycle = 2, Temperature = 3, Heartbeat = 4, Candle = 5, Spectrum = 6, Typing = 7, Music = 8, CpuLoad = 9 }
        public const int Count = 10;
        // order of the tiles on the Keyboard page
        public static readonly Kind[] Main = { Kind.Static, Kind.Breathe, Kind.Cycle, Kind.Typing, Kind.Temperature };
        public static readonly Kind[] Experimental = { Kind.Heartbeat, Kind.Candle, Kind.Spectrum, Kind.Music, Kind.CpuLoad };
        public static bool IsExperimental(Kind k) { return Array.IndexOf(Experimental, k) >= 0; }

        public static volatile Kind Current = Kind.Static;
        public static event Action Changed;
        static readonly double[] speeds = new double[Count];
        // speed of the running effect, 0 slow .. 1 fast (each effect remembers its own)
        public static double Speed { get { return speeds[(int)Current]; } set { speeds[(int)Current] = Math.Max(0, Math.Min(1, value)); } }
        // brightness range on the 0..4 scale. Never below 0.50: the LEDs flicker under that.
        public const double MinLow = 0.5, Gap = 0.1;
        public static double Low = 0.5, High = 3.0;
        static Thread th;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static double tempSmooth = -1, loadSmooth = -1;
        static DateTime lastCheck = DateTime.MinValue;

        // ---------------------------------------------------------------- descriptions
        public static string Name(Kind k)
        {
            switch (k)
            {
                case Kind.Breathe: return "Breathe";
                case Kind.Cycle: return "Color cycle";
                case Kind.Temperature: return "Temperature";
                case Kind.Heartbeat: return "Heartbeat";
                case Kind.Candle: return "Candle";
                case Kind.Spectrum: return "Spectrum";
                case Kind.Typing: return "Typing";
                case Kind.Music: return "Music";
                case Kind.CpuLoad: return "CPU load";
                default: return "Static";
            }
        }
        public static string Tag(Kind k)
        {
            switch (k)
            {
                case Kind.Static: return "Solid";
                case Kind.Cycle: case Kind.Temperature: case Kind.CpuLoad: return "Color";
                case Kind.Spectrum: return "Color + light";
                case Kind.Typing: case Kind.Music: return "Reactive";
                default: return "Brightness";
            }
        }
        public static string Describe(Kind k)
        {
            switch (k)
            {
                case Kind.Breathe: return "Brightness rises and falls smoothly in your chosen color, between the lowest and highest levels below.";
                case Kind.Heartbeat: return "A double pulse, like a heartbeat, in your chosen color. Set the beats per minute and how bright the beat gets.";
                case Kind.Candle: return "A restless, flickering glow like a candle flame. Looks best with a warm color such as orange or amber.";
                case Kind.Spectrum: return "Breathes like Breathe, and moves to the next color of the rainbow each time it is at its dimmest.";
                case Kind.Cycle: return "The color slowly travels around the color wheel.";
                case Kind.Typing: return "Lights up to the highest level whenever you press a key, then fades back to the lowest. ControlHub only notices that a key was pressed, never which one, and stores nothing.";
                case Kind.Music: return "Brightness follows whatever is playing on your speakers or headphones, in your chosen color. Raise the sensitivity if quiet audio barely moves it.";
                case Kind.Temperature: return "The color follows the CPU temperature: blue when cool, green, yellow, orange, red when hot.";
                case Kind.CpuLoad: return "The color follows how busy the processor is: green when idle, yellow, orange, red under full load.";
                default: return "No animation: the keyboard keeps the color and brightness set above.";
            }
        }
        public static bool UsesSpeed(Kind k) { return k != Kind.Static && k != Kind.Temperature && k != Kind.CpuLoad; }
        public static bool UsesRange(Kind k) { return k == Kind.Breathe || k == Kind.Heartbeat || k == Kind.Candle || k == Kind.Spectrum || k == Kind.Typing || k == Kind.Music; }
        public static bool OwnsColor(Kind k) { return k == Kind.Cycle || k == Kind.Spectrum || k == Kind.Temperature || k == Kind.CpuLoad; }
        public static string SpeedName(Kind k)
        {
            switch (k)
            {
                case Kind.Heartbeat: return "Heart rate";
                case Kind.Candle: return "Flicker";
                case Kind.Typing: return "Fade";
                case Kind.Music: return "Sensitivity";
                default: return "Speed";
            }
        }
        public static string SpeedText(Kind k)
        {
            var en = System.Globalization.CultureInfo.InvariantCulture;
            switch (k)
            {
                case Kind.Breathe: return F(BreathePeriod) + " s per breath";
                case Kind.Spectrum: return F(SpectrumPeriod) + " s per color";
                case Kind.Cycle: return F(CyclePeriod) + " s per cycle";
                case Kind.Heartbeat: return Math.Round(Bpm).ToString(en) + " BPM";
                case Kind.Candle: return Speed < 0.34 ? "Calm" : Speed < 0.67 ? "Lively" : "Windy";
                case Kind.Typing: return F(TypingFade) + " s fade";
                case Kind.Music: return MusicGain.ToString("0.0", en) + "×";
                default: return "";
            }
        }
        static string F(double s) { return s.ToString(s < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture); }

        // ---------------------------------------------------------------- idle / input
        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
        [DllImport("kernel32.dll")] static extern uint GetTickCount();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        public static double IdleSeconds()
        {
            var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            return GetLastInputInfo(ref i) ? (GetTickCount() - i.dwTime) / 1000.0 : 0;
        }

        // ---------------------------------------------------------------- settings
        public static void Load()
        {
            int k = Settings.Int("kbFx", 0);
            Current = k >= 0 && k < Count ? (Kind)k : Kind.Static;
            int legacy = Settings.Int("kbFxSpeed", 50);
            for (int i = 0; i < Count; i++)
                speeds[i] = Math.Max(0, Math.Min(1, Settings.Int("kbFxSpeed" + i, i == 1 || i == 2 ? legacy : 50) / 100.0));
            Low = Math.Max(MinLow, Settings.Int("kbFxLow", 50) / 100.0);
            High = Math.Max(Low + Gap, Math.Min(4, Settings.Int("kbFxHigh", 300) / 100.0));
            Start();
        }

        public static void Set(Kind k)
        {
            if ((int)k < 0 || (int)k >= Count) k = Kind.Static;
            Current = k;
            Settings.Set("kbFx", (int)k);
            Log.Write("keyboard effect: " + k);
            Start(); wake.Set();
            if (Changed != null) Changed();
        }
        public static void SetSpeed(double s)
        {
            Speed = s;
            Settings.Set("kbFxSpeed" + (int)Current, (int)Math.Round(Speed * 100));
        }
        public static void SaveRange()
        {
            Settings.Set("kbFxLow", (int)Math.Round(Low * 100));
            Settings.Set("kbFxHigh", (int)Math.Round(High * 100));
        }

        static void Start()
        {
            if (th != null) return;
            th = new Thread(Loop) { IsBackground = true, Name = "kbfx", Priority = ThreadPriority.AboveNormal };
            th.Start();
        }

        // ---------------------------------------------------------------- timing per effect
        static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
        static double Sp(Kind k) { return speeds[(int)k]; }
        public static double BreathePeriod { get { return Lerp(7.0, 1.6, Sp(Kind.Breathe)); } }
        public static double SpectrumPeriod { get { return Lerp(8.0, 2.0, Sp(Kind.Spectrum)); } }
        public static double CyclePeriod { get { return Lerp(40.0, 5.0, Sp(Kind.Cycle)); } }
        public static double Bpm { get { return Lerp(45, 140, Sp(Kind.Heartbeat)); } }
        public static double TypingFade { get { return Lerp(2.5, 0.25, Sp(Kind.Typing)); } }
        public static double MusicGain { get { return Lerp(1.0, 8.0, Sp(Kind.Music)); } }
        static double CandleRate { get { return Lerp(1.5, 7.0, Sp(Kind.Candle)); } }

        static Color Gradient(double t, double[] at, Color[] col)
        {
            if (t <= at[0]) return col[0];
            for (int i = 1; i < at.Length; i++)
                if (t <= at[i]) return Theme.Mix(col[i - 1], col[i], (t - at[i - 1]) / (at[i] - at[i - 1]));
            return col[col.Length - 1];
        }
        // 40° blue -> 55° green -> 68° yellow -> 78° orange -> 88° red
        static readonly double[] tempAt = { 40, 55, 68, 78, 88 };
        static readonly Color[] tempCol = { Color.FromRgb(0, 120, 255), Color.FromRgb(0, 220, 90), Color.FromRgb(255, 210, 0), Color.FromRgb(255, 110, 0), Color.FromRgb(255, 0, 0) };
        // 0 % green -> 45 % yellow -> 75 % orange -> 100 % red
        static readonly double[] loadAt = { 0, 45, 75, 100 };
        static readonly Color[] loadCol = { Color.FromRgb(0, 220, 90), Color.FromRgb(255, 210, 0), Color.FromRgb(255, 110, 0), Color.FromRgb(255, 0, 0) };

        // smooth 1-D value noise in 0..1
        static double Hash(double i) { double x = Math.Sin(i * 12.9898 + 4.1414) * 43758.5453; return x - Math.Floor(x); }
        static double Noise(double x)
        {
            double i = Math.Floor(x), f = x - i;
            f = f * f * (3 - 2 * f);
            return Lerp(Hash(i), Hash(i + 1), f);
        }
        // one heartbeat pulse at phase c: quick rise, slower fall
        static double Pulse(double p, double c)
        {
            double d = p - c;
            return d < 0 ? Math.Exp(-(d / 0.04) * (d / 0.04)) : Math.Exp(-d / 0.11);
        }
        static double Breath(double ph)
        {
            double e = 0.5 - 0.5 * Math.Cos(2 * Math.PI * ph);
            return e * e * (3 - 2 * e);                  // softer at the ends
        }

        // inputs sampled by the effect loop (read by the preview too)
        static double lastKey = -100;     // clock seconds of the last key press
        static double musicEnv;           // 0..1

        // What the keyboard should look like right now (shared with the on-screen preview).
        public static void Visual(KbState kb, out Color c, out double level) { Visual(Current, kb, false, out c, out level); }

        // `demo` replaces the live inputs (keys, audio) with a synthetic rhythm for the effect tiles.
        public static void Visual(Kind kind, KbState kb, bool demo, out Color c, out double level)
        {
            double peak = kb.Precise > 0 ? kb.Precise : kb.Level;
            c = Color.FromRgb(kb.R, kb.G, kb.B); level = peak;
            double t = clock.Elapsed.TotalSeconds;
            double lo = Math.Max(MinLow, Low), hi = Math.Max(lo, High);
            switch (kind)
            {
                case Kind.Breathe:
                    level = Lerp(lo, hi, Breath((t % BreathePeriod) / BreathePeriod));
                    break;
                case Kind.Spectrum:
                    {
                        double p = SpectrumPeriod, n = Math.Floor(t / p);
                        level = Lerp(lo, hi, Breath((t - n * p) / p));
                        // the colour only changes at the dimmest point (one colour write per breath)
                        c = Theme.Hsv((n * 45) % 360, 1, 1);
                        break;
                    }
                case Kind.Heartbeat:
                    {
                        double per = 60.0 / Bpm, ph = (t % per) / per;
                        double e = Math.Min(1, Pulse(ph, 0.06) + 0.6 * Pulse(ph, 0.30));
                        level = Lerp(lo, hi, e);
                        break;
                    }
                case Kind.Candle:
                    {
                        double r = CandleRate;
                        double n = 0.55 * Noise(t * r) + 0.30 * Noise(t * r * 2.7 + 31.7) + 0.15 * Noise(t * r * 6.1 + 77.3);
                        // mostly bright with sudden dips
                        double e = Math.Max(0, Math.Min(1, 1.6 * n - 0.25));
                        level = Lerp(lo, hi, Math.Sqrt(e));
                        break;
                    }
                case Kind.Cycle:
                    c = Theme.Hsv(360.0 * (t % CyclePeriod) / CyclePeriod, 1, 1);
                    break;
                case Kind.Typing:
                    {
                        double since = demo ? (t % 1.6 < 0.9 ? t % 0.45 : t % 1.6) : t - lastKey;
                        double e = since < 0 ? 0 : Math.Exp(-since / (demo ? 0.35 : TypingFade));
                        level = Lerp(lo, hi, e);
                        break;
                    }
                case Kind.Music:
                    {
                        double e = demo ? Math.Max(0, Math.Exp(-(t % 0.5) / 0.12) * (0.6 + 0.4 * Noise(t * 2))) : musicEnv;
                        level = Lerp(lo, hi, e);
                        break;
                    }
                case Kind.Temperature:
                    {
                        double temp = Hw.Last.Ok ? Hw.Last.CpuTemp : 50;
                        if (!demo) tempSmooth = tempSmooth < 0 ? temp : tempSmooth + (temp - tempSmooth) * 0.15;
                        c = Gradient(demo ? 40 + 48 * (0.5 - 0.5 * Math.Cos(t * 0.8)) : tempSmooth, tempAt, tempCol);
                        break;
                    }
                case Kind.CpuLoad:
                    {
                        double load = Hw.Last.CpuLoad;
                        if (!demo) loadSmooth = loadSmooth < 0 ? load : loadSmooth + (load - loadSmooth) * 0.25;
                        c = Gradient(demo ? 100 * (0.5 - 0.5 * Math.Cos(t * 0.9)) : loadSmooth, loadAt, loadCol);
                        break;
                    }
            }
        }

        // ---------------------------------------------------------------- live inputs
        static readonly bool[] keyDown = new bool[256];
        static void PollKeys()
        {
            bool any = false;
            for (int vk = 8; vk < 255; vk++)
            {
                bool d = (GetAsyncKeyState(vk) & 0x8000) != 0;
                if (d && !keyDown[vk]) any = true;
                keyDown[vk] = d;
            }
            if (any) lastKey = clock.Elapsed.TotalSeconds;
        }

        static AudioMeter meter;
        static DateTime meterAt = DateTime.MinValue;
        static double lastAudioT;
        static void SampleAudio()
        {
            double t = clock.Elapsed.TotalSeconds, dt = Math.Min(0.2, t - lastAudioT); lastAudioT = t;
            // re-open the default device now and then (headphones plugged in, output switched)
            if (meter == null || (DateTime.Now - meterAt).TotalSeconds > 5)
            {
                if (meter != null) meter.Dispose();
                meter = AudioMeter.Open(); meterAt = DateTime.Now;
            }
            double peak = meter != null ? meter.Peak() : 0;
            if (peak < 0) { meter.Dispose(); meter = null; peak = 0; }
            double x = Math.Min(1, Math.Sqrt(peak) * MusicGain * 0.6);
            if (x < 0.04) x = 0;
            // fast attack, ~0.25 s release
            musicEnv = x > musicEnv ? musicEnv + (x - musicEnv) * 0.7 : musicEnv * Math.Exp(-dt / 0.25);
            if (musicEnv < 0.01) musicEnv = 0;
        }
        static void CloseAudio() { if (meter != null) { meter.Dispose(); meter = null; } musicEnv = 0; }

        // ---------------------------------------------------------------- the loop
        static int FrameMs(Kind k)
        {
            switch (k)
            {
                case Kind.Breathe: case Kind.Spectrum: return 50;
                case Kind.Heartbeat: return 35;
                case Kind.Candle: return 45;
                case Kind.Typing: return 25;      // also the key polling rate
                case Kind.Music: return 40;
                // cycle: ~2° of hue per frame (each colour frame costs three LED writes)
                case Kind.Cycle: return (int)Math.Max(50, Math.Min(150, CyclePeriod * 1000 / 180));
                default: return 400;
            }
        }

        static void Loop()
        {
            uint lastV = uint.MaxValue, lastC = uint.MaxValue;
            bool active = false, dark = false;
            while (true)
            {
                int wait = 500;
                try
                {
                    var kind = Current; var kb = Hw.Kb;
                    if (kind != Kind.Music) CloseAudio();
                    if (kind == Kind.Static || !Dchu.Available)
                    {
                        if (active) { Restore(kb); active = false; dark = false; lastV = lastC = uint.MaxValue; }
                        wake.WaitOne(1000); continue;
                    }
                    // Fn-key changes land in the app buffer (592 on/off, 547 level): watch them so an effect
                    // never switches the light back on behind the user's back
                    if ((DateTime.Now - lastCheck).TotalMilliseconds >= 500)
                    {
                        lastCheck = DateTime.Now;
                        bool hwOn = Dchu.ReadApp(592, 1)[0] != 0;
                        if (hwOn != kb.On) { kb.On = hwOn; Hw.RefreshKb(); if (!hwOn) Log.Write("keyboard light turned off outside ControlHub; effect paused"); }
                    }
                    if (!kb.On) { active = false; lastV = lastC = uint.MaxValue; wake.WaitOne(500); continue; }
                    if (kind == Kind.Typing) PollKeys();
                    // auto-off: the EC timer can't see our frames as "idle", so honour it here
                    if (kb.SleepOn && IdleSeconds() >= kb.SleepSec)
                    {
                        if (!dark) { Dchu.WriteQuiet(103, 244, 0); dark = true; lastV = 0; }
                        wake.WaitOne(150); continue;
                    }
                    if (dark) { dark = false; lastV = uint.MaxValue; }
                    active = true;
                    if (kind == Kind.Music) SampleAudio();

                    Color c; double level;
                    Visual(kind, kb, false, out c, out level);
                    uint v = Hw.FineValue(kb.Type, level);
                    // music moves constantly: skip tiny steps nobody can see (each write costs ~6 ms CPU)
                    bool tiny = kind == Kind.Music && lastV != uint.MaxValue && Math.Abs((int)v - (int)lastV) < 4
                                && v != Hw.FineValue(kb.Type, Low) && v != Hw.FineValue(kb.Type, High);
                    if (v != lastV && !tiny) { Dchu.WriteQuiet(103, 244, v); lastV = v; }
                    uint pc = Hw.PackColor(kb.Type, c.R, c.G, c.B);
                    if (pc != lastC) { foreach (int z in new[] { 240, 241, 242 }) Dchu.WriteQuiet(103, z, pc); lastC = pc; }
                    wait = FrameMs(kind);
                }
                catch (Exception ex) { Log.Write("kbfx: " + ex.Message); wait = 1000; }
                wake.WaitOne(wait);
            }
        }

        // On quit: don't leave the keyboard on a random effect frame.
        public static void RestoreOnExit()
        {
            if (Current == Kind.Static) return;
            try { Restore(Hw.Kb); } catch { }
        }

        // Back to the saved static look (colour + brightness).
        static void Restore(KbState kb)
        {
            if (!kb.On) return;
            uint pc = Hw.PackColor(kb.Type, kb.R, kb.G, kb.B);
            foreach (int z in new[] { 240, 241, 242 }) Dchu.WriteQuiet(103, z, pc);
            Dchu.WriteQuiet(103, 244, Hw.FineValue(kb.Type, kb.Precise > 0 ? kb.Precise : kb.Level));
            Log.Write("keyboard effect stopped, static look restored");
        }
    }

    // Peak level of the default playback device (Windows Core Audio, read-only).
    sealed class AudioMeter : IDisposable
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }
        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
        }

        IAudioMeterInformation m; IMMDevice dev;

        public static AudioMeter Open()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                IMMDevice d;
                if (en.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out d) != 0 || d == null) { Marshal.ReleaseComObject(en); return null; }
                Marshal.ReleaseComObject(en);
                var iid = typeof(IAudioMeterInformation).GUID; object o;
                if (d.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o) != 0 || o == null) { Marshal.ReleaseComObject(d); return null; }
                return new AudioMeter { m = (IAudioMeterInformation)o, dev = d };
            }
            catch (Exception ex) { Log.Write("audio meter: " + ex.Message); return null; }
        }

        // 0..1, or -1 when the device went away
        public double Peak()
        {
            try { float p; return m.GetPeakValue(out p) == 0 ? Math.Max(0, Math.Min(1, p)) : -1; }
            catch { return -1; }
        }

        public void Dispose()
        {
            try { if (m != null) Marshal.ReleaseComObject(m); } catch { }
            try { if (dev != null) Marshal.ReleaseComObject(dev); } catch { }
            m = null; dev = null;
        }
    }
}
