// Hardware access for the Clevo/Insyde "DCHU" interface (same DLL and the same calls that the OEM
// Control Center CC40.exe uses). Every call runs on one background thread; every write is logged.
//
// Reads (what CC40 polls itself):
//   GetDCHU_Data_Buffer(12)  live sensors: fan tach raw [2..7],[36..37], duty [16],[19],[22],[38], temp CPU [18] GPU [21]
//   GetDCHU_Data_Buffer(13)  fan/keyboard capabilities: fan count [12], keyboard type [15]
//   ReadAppSettings(page, off, len)  persisted settings ("app buffer"), id = page*256 + off
// Writes:
//   Power mode   -> event-log message "clevocc30^101^<n>" to OutLog/PowerBIOSServer_Out; the OEM FnKey service
//                   applies it (fan table, refresh rate, BIOS) exactly as when CC40 changes it.
//   Keyboard LED -> SetDCHU_Data(103 SetKBLED, sub, value) + app-buffer persistence, same sequence as CC40.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ControlHub
{
    static class Dchu
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string p);
        [DllImport("InsydeDCHU.dll")] static extern int GetDCHU_Data_Integer(int cmd, ref int data);
        [DllImport("InsydeDCHU.dll")] static extern int GetDCHU_Data_Buffer(int cmd, ref byte buf);
        [DllImport("InsydeDCHU.dll")] static extern int SetDCHU_Data(int cmd, byte[] buf, int len);
        [DllImport("InsydeDCHU.dll")] static extern int SetDCHU_DataEx(int cmd, byte[] buf, int len, ref byte outBuf);
        [DllImport("InsydeDCHU.dll")] static extern int ReadAppSettings(int page, int off, int len, ref byte buf);
        [DllImport("InsydeDCHU.dll")] static extern int WriteAppSettings(int page, int off, int len, ref byte buf);

        public static bool Available { get; private set; }
        public static string Error = "";
        public static bool DryRun;   // writes are logged but not sent (testing)

        public static void Init()
        {
            try
            {
                string dir = Path.Combine(AppDir.Path, "lib");
                SetDllDirectory(dir);
                var b = new byte[256];
                GetDCHU_Data_Buffer(13, ref b[0]);
                Available = true;
            }
            catch (Exception ex) { Available = false; Error = ex.Message; Log.Write("DCHU init failed: " + ex.Message); }
        }

        public static byte[] Buffer(int cmd)
        {
            var b = new byte[256];
            if (Available) GetDCHU_Data_Buffer(cmd, ref b[0]);
            return b;
        }

        public static int Int(int cmd)
        {
            int v = 0;
            if (Available) GetDCHU_Data_Integer(cmd, ref v);
            return v;
        }

        public static byte[] ReadApp(int id, int len)
        {
            var b = new byte[Math.Max(1, len)];
            if (Available) ReadAppSettings(id / 256, id % 256, len, ref b[0]);
            return b;
        }

        // ---- writes (all logged)
        public static void WriteApp(int id, byte[] data)
        {
            Log.Write("WRITE app id=" + id + " (page " + id / 256 + " off " + id % 256 + ") data=" + BitConverter.ToString(data) + (DryRun ? " [dry]" : ""));
            if (!Available || DryRun) return;
            var b = (byte[])data.Clone();
            lock (WriteGate) WriteAppSettings(id / 256, id % 256, b.Length, ref b[0]);
        }

        // Writes from different threads (hw worker, keyboard effects) are serialized.
        public static readonly object WriteGate = new object();

        // CC40 "WMI write": 4-byte little-endian value with the sub-command in the top byte.
        public static void Write(int cmd, int sub, uint value)
        {
            Log.Write("WRITE wmi cmd=" + cmd + " sub=" + sub + " value=" + value + (DryRun ? " [dry]" : ""));
            WriteQuiet(cmd, sub, value);
        }

        // Same as Write without logging (keyboard effect frames).
        public static void WriteQuiet(int cmd, int sub, uint value)
        {
            var b = BitConverter.GetBytes(value);
            b[3] = (byte)sub;
            if (!Available || DryRun) return;
            lock (WriteGate) SetDCHU_Data(cmd, b, b.Length);
        }

        // Package query that only reads (e.g. cmd 4 sub 40 = fan table): not logged as a write, runs in dry-run.
        public static byte[] PackageRead(int cmd, byte[] input)
        {
            var buf = new byte[256];
            Array.Copy(input, buf, Math.Min(input.Length, 256));
            var outb = new byte[256];
            if (Available) SetDCHU_DataEx(cmd, buf, 256, ref outb[0]);
            return outb;
        }

        // CC40 "package RW" (cmd 4): input bytes in a 256-byte buffer, 256-byte reply.
        public static byte[] Package(int cmd, byte[] input)
        {
            var buf = new byte[256];
            Array.Copy(input, buf, Math.Min(input.Length, 256));
            var outb = new byte[256];
            Log.Write("WRITE package cmd=" + cmd + " in=" + BitConverter.ToString(input) + (DryRun ? " [dry]" : ""));
            if (!Available || DryRun) return outb;
            lock (WriteGate) SetDCHU_DataEx(cmd, buf, 256, ref outb[0]);
            return outb;
        }
    }

    static class AppDir
    {
        public static readonly string Path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        public static readonly string Data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControlHub");
    }

    static class Log
    {
        static readonly object gate = new object();
        public static void Write(string s)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(AppDir.Data);
                    string f = System.IO.Path.Combine(AppDir.Data, "log.txt");
                    if (File.Exists(f) && new FileInfo(f).Length > 2000000) File.Delete(f);
                    File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    // ===================================================================== model
    enum PowerMode { Quiet = 0, PowerSaving = 1, Performance = 2, Entertainment = 3, QuietPlus = 4 }

    class FanInfo { public int Rpm; public int Duty; public bool Present; }

    class Snapshot
    {
        public DateTime Time;
        public int CpuTemp, GpuTemp;
        public FanInfo[] Fans = { new FanInfo(), new FanInfo(), new FanInfo(), new FanInfo() };
        public int FanCount = 2;
        public int Mode = -1;
        public double CpuLoad, RamUsed, RamTotal;
        public double CpuWatts = -1;     // RAPL package power (Windows "Energy Meter")
        public int Battery = -1; public bool OnAc = true, Charging;
        public bool Ok;
    }

    class KbState
    {
        public int Type;            // CC40 keyboard type (6 = single-zone RGB "15 color")
        public bool On;             // app 592
        public int Level = 1;       // app 547, 1..4
        public double Precise = -1; // fine level 0..4 kept by ControlHub (the BIOS only stores whole levels)
        public byte R = 0, G = 0, B = 255; // app 593
        public int Mode = 8;        // app 544: 8 static, 2 breathe, 3 cycle
        public bool SleepOn;        // app 548
        public int SleepSec = 30;   // app 549 (h, m, s)
        public bool CanBreathe, CanCycle;
    }

    // ===================================================================== service
    // One worker thread owns the DLL: polls sensors and runs queued commands in order.
    static class Hw
    {
        static Thread worker;
        static readonly Queue<Action> queue = new Queue<Action>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static volatile bool running;
        public static int IntervalMs = 1000;
        // GetDCHU_Data_Buffer costs ~80 ms CPU inside the OEM DLL, so poll slowly while nobody is looking.
        public static volatile bool Background;
        public const int BackgroundIntervalMs = 5000;
        public static event Action<Snapshot> Updated;     // raised on the worker thread
        public static event Action<KbState> KbChanged;
        public static Snapshot Last = new Snapshot();
        public static KbState Kb = new KbState();
        public static byte[] Caps = new byte[256], Page7 = new byte[256];

        static PerformanceCounter cpu, rapl;
        static int lastMode = -1;

        public static void Start()
        {
            if (running) return;
            running = true;
            worker = new Thread(Loop) { IsBackground = true, Name = "hw", Priority = ThreadPriority.AboveNormal };
            worker.Start();
        }
        public static void Stop() { running = false; wake.Set(); }

        public static void Run(Action a) { lock (queue) queue.Enqueue(a); wake.Set(); }
        public static void Poke() { wake.Set(); }
        public static void SetBackground(bool bg)
        {
            if (Background == bg) return;
            Background = bg;
            if (!bg) { pollNow = true; wake.Set(); }   // fresh data as soon as the window shows
        }
        static volatile bool pollNow;

        static void Loop()
        {
            Dchu.Init();
            try { rapl = new PerformanceCounter("Energy Meter", "Power", "RAPL_Package0_PKG"); rapl.NextValue(); } catch { rapl = null; }
            try { cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); cpu.NextValue(); }
            catch { try { cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); cpu.NextValue(); } catch { cpu = null; } }
            try
            {
                Caps = Dchu.Buffer(13);
                Page7 = Dchu.ReadApp(7 * 256, 256);
                ReadKb();
                if (Kb.On && Kb.Precise > 0 && Math.Abs(Kb.Precise - Kb.Level) > 0.004) Dchu.Write(103, 244, FineValue(Kb.Type, Kb.Precise));
            }
            catch (Exception ex) { Log.Write("caps: " + ex.Message); }
            var sw = Stopwatch.StartNew();
            long next = 0;
            while (running)
            {
                Action job = null;
                lock (queue) if (queue.Count > 0) job = queue.Dequeue();
                if (job != null)
                {
                    try { job(); } catch (Exception ex) { Log.Write("job: " + ex); }
                    continue;
                }
                if (pollNow) { pollNow = false; next = 0; }
                if (sw.ElapsedMilliseconds >= next)
                {
                    next = sw.ElapsedMilliseconds + (Background ? Math.Max(BackgroundIntervalMs, IntervalMs) : IntervalMs);
                    try { Poll(); } catch (Exception ex) { Log.Write("poll: " + ex.Message); }
                }
                int wait = (int)Math.Max(1, next - sw.ElapsedMilliseconds);
                wake.WaitOne(wait);
            }
        }

        static double Rpm(int raw) { return raw <= 0 ? 0 : Math.Round(60.0 / (5.565217391304348E-05 * raw) * 2.0); }

        static void Poll()
        {
            var s = new Snapshot { Time = DateTime.Now };
            if (Dchu.Available)
            {
                var b = Dchu.Buffer(12);
                // an LED write from the effect thread at the same moment can make the OEM DLL return an
                // empty buffer; a 0 °C CPU is impossible, so retry once and otherwise keep the last values
                if (b[18] == 0) { System.Threading.Thread.Sleep(30); b = Dchu.Buffer(12); }
                if (b[18] == 0 && Last.Ok)
                {
                    s.CpuTemp = Last.CpuTemp; s.GpuTemp = Last.GpuTemp; s.Fans = Last.Fans; s.FanCount = Last.FanCount; s.Mode = Last.Mode; s.Ok = true;
                    goto system;
                }
                s.CpuTemp = b[18]; s.GpuTemp = b[21];
                int[] hi = { 2, 4, 6, 36 }, duty = { 16, 19, 22, 38 };
                for (int i = 0; i < 4; i++)
                {
                    int raw = (b[hi[i]] << 8) + b[hi[i] + 1];
                    s.Fans[i].Rpm = (int)Rpm(raw);
                    s.Fans[i].Duty = (int)Math.Round(b[duty[i]] / 255.0 * 100);
                }
                s.FanCount = Math.Max(1, Math.Min(4, (int)Caps[12]));
                for (int i = 0; i < 4; i++) s.Fans[i].Present = i < s.FanCount;
                s.Mode = Dchu.ReadApp(257, 1)[0];
                s.Ok = true;
            }
        system:
            try { if (cpu != null) s.CpuLoad = Math.Min(100, cpu.NextValue()); } catch { }
            try { if (rapl != null) s.CpuWatts = rapl.NextValue() / 1000.0; } catch { s.CpuWatts = -1; }
            if (s.Ok) ModeStats.Add(s);
            var m = new Win.MEMSTATUS(); m.dwLength = (uint)Marshal.SizeOf(m);
            if (Win.GlobalMemoryStatusEx(ref m)) { s.RamTotal = m.ullTotalPhys; s.RamUsed = m.ullTotalPhys - m.ullAvailPhys; }
            Win.SYSTEM_POWER_STATUS ps;
            if (Win.GetSystemPowerStatus(out ps))
            {
                s.OnAc = ps.ACLineStatus == 1; s.Battery = ps.BatteryLifePercent == 255 ? -1 : ps.BatteryLifePercent;
                s.Charging = (ps.BatteryFlag & 8) != 0;
            }
            Last = s;
            // a power-mode change also changes the fan table: read it again (and cache it for that mode)
            if (s.Ok && s.Mode != lastMode) { if (lastMode >= 0) { System.Threading.Thread.Sleep(1500); ReadFansNow(); } lastMode = s.Mode; }
            var h = Updated; if (h != null) h(s);
        }

        // ---------------------------------------------------------------- keyboard
        static void ReadKb()
        {
            var k = new KbState();
            k.Type = Caps[15];
            if (k.Type == 19 || k.Type == 35 || k.Type == 51 || k.Type == 243) k.Type = 3;
            k.Level = Clamp(Dchu.ReadApp(547, 1)[0], 1, 4);
            k.On = Dchu.ReadApp(592, 1)[0] != 0;
            var c = Dchu.ReadApp(593, 3); k.R = c[0]; k.G = c[1]; k.B = c[2];
            k.Mode = Dchu.ReadApp(544, 1)[0];
            k.SleepOn = Dchu.ReadApp(548, 1)[0] != 0;
            var t = Dchu.ReadApp(549, 3); k.SleepSec = (t[0] * 60 + t[1]) * 60 + t[2]; if (k.SleepSec < 5) k.SleepSec = 30;
            // page-7 feature flags (CC40: buffer[24] bit3 = breathe, buffer[25] bit1 = cycle)
            k.CanBreathe = ((Page7[24] >> 3) & 1) == 1;
            k.CanCycle = ((Page7[25] >> 1) & 1) == 1;
            // fine brightness: only valid while the stored whole level still matches (Fn keys change it)
            double p;
            if (double.TryParse(Settings.Get("kbPrecise", ""), System.Globalization.NumberStyles.Float, K.EN, out p) && p > 0 && Clamp((int)Math.Round(p), 1, 4) == k.Level)
                k.Precise = p;
            Kb = k;
            var h = KbChanged; if (h != null) h(k);
        }

        static int Clamp(int v, int a, int b) { return v < a ? a : v > b ? b : v; }

        static uint LevelValue(int type, int level)
        {
            if (type == 38) { switch (level) { case 1: return 47; case 2: return 102; case 3: return 155; case 4: return 212; default: return 47; } }
            switch (level) { case 1: return 47; case 2: return 95; case 3: return 143; case 4: return 191; default: return 0; }
        }

        // Fine level 0..4 -> LED value, interpolated between the OEM steps so whole levels match CC40 exactly.
        public static uint FineValue(int type, double level)
        {
            if (level <= 0) return 0;
            level = Math.Min(4, level);
            int lo = (int)Math.Floor(level), hi = Math.Min(4, lo + 1);
            double a = lo == 0 ? 0 : LevelValue(type, lo), b = LevelValue(type, hi);
            return (uint)Math.Max(1, Math.Round(a + (b - a) * (level - lo)));
        }

        static double pendingFine = -1; static bool fineQueued;

        // Live while dragging: coalesced so only the newest value is sent. persist = write the app buffer too.
        public static void KbSetFine(double level, bool persist)
        {
            lock (queue) { pendingFine = level; if (fineQueued && !persist) return; fineQueued = true; }
            Run(delegate
            {
                double lv;
                lock (queue) { lv = pendingFine; fineQueued = false; }
                var k = Kb;
                if (lv <= 0.004)
                {
                    Dchu.Write(103, 244, 0);
                    k.On = false; k.Precise = -1;
                }
                else
                {
                    Dchu.Write(103, 244, FineValue(k.Type, lv));
                    k.On = true; k.Level = Clamp((int)Math.Round(lv), 1, 4); k.Precise = lv;
                }
                if (persist)
                {
                    Dchu.WriteApp(547, new[] { (byte)k.Level });
                    Dchu.WriteApp(592, new[] { (byte)(k.On ? 1 : 0) });
                    Settings.Set("kbPrecise", k.Precise > 0 ? k.Precise.ToString("0.00", K.EN) : "");
                    var h = KbChanged; if (h != null) h(k);
                }
            });
        }

        // After start / resume the BIOS restores the whole level; put the fine value back.
        public static void KbReapplyFine()
        {
            Run(delegate
            {
                ReadKb();
                var k = Kb;
                if (k.On && k.Precise > 0 && Math.Abs(k.Precise - k.Level) > 0.004)
                    Dchu.Write(103, 244, FineValue(k.Type, k.Precise));
            });
        }

        public static void KbSetLevel(int level)   // 0 = off
        {
            Settings.Set("kbPrecise", "");
            Run(delegate
            {
                var k = Kb; k.Precise = -1;
                if (level <= 0)
                {
                    Dchu.Write(103, 244, 0);
                    k.On = false;
                }
                else
                {
                    k.Level = Clamp(level, 1, 4); k.On = true;
                    Dchu.Write(103, 244, LevelValue(k.Type, k.Level));
                }
                Dchu.WriteApp(547, new[] { (byte)k.Level });
                Dchu.WriteApp(592, new[] { (byte)(k.On ? 1 : 0) });
                var h = KbChanged; if (h != null) h(k);
            });
        }

        // CC40 colour encoding for SetKBLED zones 240-242.
        public static uint PackColor(int type, byte r, byte g, byte b)
        {
            byte bb = b;
            if (type == 6 && r == 0 && ((g == 254 && b == 129) || (g == 255 && b == 127))) bb = 70;
            if (type == 6) return (uint)(((bb / 2) << 16) | ((r / 2) << 8) | (g / 2));
            return (uint)(((byte)Math.Round(40.0 / 51.0 * bb) << 16) | (r << 8) | g);
        }

        public static void KbSetColor(byte r, byte g, byte b)
        {
            Run(delegate
            {
                var k = Kb;
                if (k.Mode != 8) { Dchu.Package(4, new byte[] { 63, 0 }); k.Mode = 8; }
                uint v = PackColor(k.Type, r, g, b);
                foreach (int zone in new[] { 240, 241, 242 }) Dchu.Write(103, zone, v);
                k.R = r; k.G = g; k.B = b;
                Dchu.WriteApp(593, new[] { r, g, b });
                Dchu.WriteApp(544, new byte[] { 8 });
                var h = KbChanged; if (h != null) h(k);
            });
        }

        public static void KbSetEffect(int mode)   // 2 breathe, 3 cycle
        {
            Run(delegate
            {
                var k = Kb;
                Dchu.Package(4, new byte[] { 63, (byte)(mode == 2 ? 1 : mode == 3 ? 2 : 0) });
                Dchu.WriteApp(544, new[] { (byte)mode });
                Dchu.Write(103, 244, 191);
                k.Mode = mode;
                var h = KbChanged; if (h != null) h(k);
            });
        }

        public static void KbSetSleep(bool on, int seconds)
        {
            Run(delegate
            {
                var k = Kb;
                seconds = Math.Max(5, seconds);
                if (on) Dchu.Write(121, 24, (uint)((seconds << 8) + 255));
                else Dchu.Write(121, 24, 0);
                k.SleepOn = on; k.SleepSec = seconds;
                Dchu.WriteApp(549, new[] { (byte)(seconds / 3600), (byte)(seconds / 60 % 60), (byte)(seconds % 60) });
                Dchu.WriteApp(548, new[] { (byte)(on ? 1 : 0) });
                if (k.On) Dchu.Write(103, 244, FineValue(k.Type, k.Precise > 0 ? k.Precise : k.Level));   // CC40 re-applies the level
                var h = KbChanged; if (h != null) h(k);
            });
        }

        public static void RefreshKb() { Run(ReadKb); }

        // ---------------------------------------------------------------- fans
        public static event Action<FanState> FanChanged;
        public static FanState Fan = new FanState();

        // CC40 "Read_Fan1_Fan2_table": package cmd 4, sub 40 -> CPU block at 0, GPU block at 128.
        static byte[] ReadFanTableRaw() { return Dchu.PackageRead(4, new byte[] { 40 }); }

        static FanCurve Decode(byte[] raw, int b, int max)
        {
            var c = new FanCurve();
            for (int j = 0; j < 10; j++)
            {
                c.Temp[j] = raw[b + 32 + j];
                int rpm = (raw[b + 42 + j * 2] << 8) + raw[b + 43 + j * 2];
                c.Duty[j] = (byte)(max <= 0 ? 0 : Math.Max(0, Math.Min(100, Math.Round(rpm * 100.0 / max))));
            }
            return c;
        }

        static void ReadFansNow()
        {
            var f = new FanState();
            if (Dchu.Available)
            {
                f.Mode = Dchu.ReadApp(1029, 1)[0];
                f.PowerMode = Dchu.ReadApp(257, 1)[0];
                var raw = ReadFanTableRaw();
                for (int i = 0; i < 2; i++)
                {
                    int b = i * 128;
                    f.MaxRpm[i] = (raw[b + 10] << 8) + raw[b + 11];
                    f.Ec[i] = Decode(raw, b, f.MaxRpm[i]);
                }
                f.EcProfileId = raw[0];
                f.CapRpm = (raw[8] << 8) + raw[9];
                f.Ok = f.MaxRpm[0] > 0;
                if (f.Ok) FanCache.Store(f);
            }
            Fan = f;
            var h = FanChanged; if (h != null) h(f);
        }
        public static void RefreshFans() { Run(ReadFansNow); }

        // CC40 fan-mode setter: SetSystemControl(sub 1), FnKey notification, app 1029, then the per-power-mode slot.
        public static void SetFanMode(int mode)
        {
            Run(delegate
            {
                int pm = Dchu.ReadApp(257, 1)[0];
                if (pm == 1) { Log.Write("fan mode ignored in battery saver"); ReadFansNow(); return; }
                Dchu.Write(121, 1, (uint)mode);
                Notify("clevofan^113^" + mode);
                Dchu.WriteApp(1029, new[] { (byte)mode });
                if (mode == 6) WriteCurveToEc(FanProfiles.ForPowerMode(pm));
                // remember it for this power mode (CC40 never stores "max" per mode)
                byte fm = (byte)(mode == 1 ? 0 : mode);
                byte off = Dchu.ReadApp(1031, 1)[0];
                int slot = pm == 0 || pm == 4 ? 0 : pm;
                Dchu.WriteApp(1094 + slot, new[] { fm });
                Dchu.WriteApp(1098 + slot, new[] { off });
                ReadFansNow();
            });
        }

        // Saves the curves to the shared XML; if the edited profile is the running one and the fan
        // is in Custom mode, writes the EC table right away (CC40 algorithm) and verifies it.
        public static void SaveCurves(int profile, FanCurve cpu, FanCurve gpu, Action<string> done)
        {
            Run(delegate
            {
                string result;
                try
                {
                    cpu.Sanitize(); gpu.Sanitize();
                    var all = FanProfiles.Load();
                    all[profile] = new[] { cpu.Clone(), gpu.Clone() };
                    if (!Dchu.DryRun) FanProfiles.Save(all); else Log.Write("fan xml save [dry] profile " + profile + " cpu " + cpu.Describe() + " | gpu " + gpu.Describe());
                    int pm = Dchu.ReadApp(257, 1)[0];
                    int mode = Dchu.ReadApp(1029, 1)[0];
                    if (FanProfiles.ForPowerMode(pm) == profile && mode == 6) result = WriteCurveToEc(profile) ? "applied" : "saved-verify-failed";
                    else result = "saved";
                }
                catch (Exception ex) { result = "error: " + ex.Message; }
                ReadFansNow();
                if (done != null) done(result);
            });
        }

        static bool WriteCurveToEc(int profile)
        {
            if (profile < 0) return false;
            var curves = FanProfiles.Load()[profile];
            var raw = ReadFanTableRaw();
            var w = new byte[256];
            w[0] = 41;
            for (int i = 0; i < 2; i++)
            {
                int b = i * 128;
                var blk = new byte[112];
                Array.Copy(raw, b, blk, 0, 112);
                int max = (blk[10] << 8) + blk[11];
                if (max <= 0) { Log.Write("fan table: no max rpm, abort"); return false; }
                var c = curves[i].Clone(); c.Sanitize();
                blk[0] = (byte)((i == 0 ? 128 : 144) + profile);
                for (int j = 0; j < 10; j++)
                {
                    blk[22 + j] = (byte)(j == 0 ? 0 : c.Temp[j - 1] + 1);
                    blk[32 + j] = c.Temp[j];
                    int rpm = (int)Math.Round((double)(max * c.Duty[j]) / 100.0);
                    blk[42 + j * 2] = (byte)(rpm >> 8); blk[43 + j * 2] = (byte)rpm;
                    blk[62 + j * 2] = (byte)(rpm >> 8); blk[63 + j * 2] = (byte)rpm;
                    blk[92 + j] = 10; blk[82 + j] = 26; blk[102 + j] = 0x61;
                }
                Array.Copy(blk, 0, w, i == 0 ? 16 : 128, 112);
            }
            Log.Write("fan table write profile " + profile + " cpu " + curves[0].Describe() + " | gpu " + curves[1].Describe());
            Dchu.Package(4, w);
            if (Dchu.DryRun) return true;
            // verify by reading the table back
            System.Threading.Thread.Sleep(80);
            var back = ReadFanTableRaw();
            for (int i = 0; i < 2; i++)
            {
                int b = i * 128, max = (back[b + 10] << 8) + back[b + 11];
                var c = curves[i].Clone(); c.Sanitize();
                for (int j = 0; j < 10; j++)
                {
                    int rpm = (back[b + 42 + j * 2] << 8) + back[b + 43 + j * 2];
                    if (back[b + 32 + j] != c.Temp[j] || rpm != (int)Math.Round((double)(max * c.Duty[j]) / 100.0))
                    { Log.Write("fan table verify FAILED fan " + i + " point " + j); return false; }
                }
            }
            Log.Write("fan table verified");
            return true;
        }

        // ---------------------------------------------------------------- FlexiCharger (battery charge limit)
        public static event Action<ChargeState> ChargeChanged;
        public static ChargeState Charge = new ChargeState();

        // package cmd 4 sub 30: [0] status (0 = off), [1] start %, [2] stop %, [16..31] allowed starts, [32..47] allowed stops.
        static void ReadChargeNow()
        {
            var c = new ChargeState();
            if (Dchu.Available)
            {
                var o = Dchu.PackageRead(4, new byte[] { 30 });
                c.Enabled = o[0] != 0; c.Start = o[1]; c.Stop = o[2];
                for (int i = 16; i < 32; i++) if (o[i] != 0) c.Starts.Add(o[i]);
                for (int i = 32; i < 48; i++) if (o[i] != 0) c.Stops.Add(o[i]);
                c.Choice = Dchu.ReadApp(302, 1)[0];
                c.Supported = c.Stops.Count > 0 && c.Starts.Count > 0;
            }
            Charge = c;
            var h = ChargeChanged; if (h != null) h(c);
        }
        public static void RefreshCharge() { Run(ReadChargeNow); }

        // choice: 0 maximum (off), 1 recommended 70-80, 2 custom. Same writes as CC40: package [31, status, start, stop] + app 302.
        public static void SetCharge(int choice, int start, int stop)
        {
            Run(delegate
            {
                var c = Charge;
                if (!c.Supported) { ReadChargeNow(); c = Charge; if (!c.Supported) return; }
                byte status, s, e;
                if (choice == 0) { status = 0; s = (byte)c.Starts[c.Starts.Count - 1]; e = 100; }
                else if (choice == 1) { status = 1; s = 70; e = 80; }
                else
                {
                    status = 1;
                    if (!c.Starts.Contains(start) || !c.Stops.Contains(stop) || start >= stop) { Log.Write("charge: invalid " + start + "-" + stop); ReadChargeNow(); return; }
                    s = (byte)start; e = (byte)stop;
                }
                Dchu.Package(4, new byte[] { 31, status, s, e });
                Dchu.WriteApp(302, new[] { (byte)choice });
                Log.Write("FlexiCharger set status=" + status + " start=" + s + " stop=" + e);
                System.Threading.Thread.Sleep(60);
                ReadChargeNow();
            });
        }

        static void Notify(string msg)
        {
            Log.Write("EVENT " + msg + (Dchu.DryRun ? " [dry]" : ""));
            if (Dchu.DryRun) return;
            try { using (var ev = new EventLog { Source = "PowerBIOSServer_Out", Log = "OutLog" }) ev.WriteEntry(msg); }
            catch (Exception ex) { Log.Write("event failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------- power mode
        public static void SetPowerMode(int mode)
        {
            Run(delegate
            {
                string msg = "clevocc30^101^" + mode;
                Log.Write("POWER " + msg + (Dchu.DryRun ? " [dry]" : ""));
                if (Dchu.DryRun) return;
                try
                {
                    using (var ev = new EventLog { Source = "PowerBIOSServer_Out", Log = "OutLog" }) ev.WriteEntry(msg);
                }
                catch (Exception ex) { Log.Write("power mode event failed: " + ex.Message); }
            });
        }
    }

    // ===================================================================== fans
    // 10-point curve: Temp[i] = upper bound (°C) of step i, Duty[i] = % of the fan's max rpm.
    class FanCurve
    {
        public byte[] Temp = new byte[10], Duty = new byte[10];
        public static readonly byte[] DefTemp = { 35, 40, 45, 50, 60, 65, 70, 80, 90, 100 };
        public static readonly byte[] DefDuty = { 0, 30, 40, 45, 50, 60, 70, 80, 90, 100 };
        public static FanCurve Default() { var c = new FanCurve(); Array.Copy(DefTemp, c.Temp, 10); Array.Copy(DefDuty, c.Duty, 10); return c; }
        public FanCurve Clone() { var c = new FanCurve(); Array.Copy(Temp, c.Temp, 10); Array.Copy(Duty, c.Duty, 10); return c; }
        public bool SameAs(FanCurve o) { for (int i = 0; i < 10; i++) if (Temp[i] != o.Temp[i] || Duty[i] != o.Duty[i]) return false; return true; }

        // Safety rules (enforced before anything is saved or written):
        // temps strictly rising 20..100, duty never falling, last point 100°C/100%, >=85°C needs >=70%.
        public void Sanitize()
        {
            Temp[9] = 100; Duty[9] = 100;
            for (int i = 8; i >= 0; i--) Temp[i] = (byte)Math.Max(20 + i, Math.Min(Temp[i], Temp[i + 1] - 1));
            for (int i = 1; i < 10; i++) if (Temp[i] <= Temp[i - 1]) Temp[i] = (byte)(Temp[i - 1] + 1);
            for (int i = 0; i < 10; i++)
            {
                if (Duty[i] > 100) Duty[i] = 100;
                if (Temp[i] >= 85 && Duty[i] < 70) Duty[i] = 70;
                if (i > 0 && Duty[i] < Duty[i - 1]) Duty[i] = Duty[i - 1];
            }
        }
        public string Describe() { var s = ""; for (int i = 0; i < 10; i++) s += Temp[i] + "°:" + Duty[i] + "% "; return s.Trim(); }
    }

    // The OEM fan-profile store shared with FnKey/CC40: C:\ComboKey\profile_Fan.xml.
    // ProfileIndex 0 = quiet (power modes 0 and 4), 2 = performance, 3 = entertainment.
    static class FanProfiles
    {
        public const string XmlPath = @"C:\ComboKey\profile_Fan.xml";
        static readonly string[] Names = { "quiet", "powersaving", "performance", "entertainment" };

        public static int ForPowerMode(int mode) { return mode == 0 || mode == 4 ? 0 : mode == 2 ? 2 : mode == 3 ? 3 : -1; }

        public static Dictionary<int, FanCurve[]> Load()
        {
            var d = new Dictionary<int, FanCurve[]>();
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(XmlPath);
                foreach (System.Xml.XmlNode p in doc.SelectNodes("/root/Profile"))
                {
                    int idx = int.Parse(p.Attributes["ProfileIndex"].Value);
                    var curves = new[] { FanCurve.Default(), FanCurve.Default() };
                    foreach (System.Xml.XmlNode f in p.SelectNodes("CustomFan"))
                    {
                        int m = int.Parse(f.Attributes["FanModule"].Value), i = int.Parse(f.Attributes["index"].Value);
                        if (m < 0 || m > 1 || i < 0 || i > 9) continue;
                        curves[m].Duty[i] = byte.Parse(f.Attributes["duty"].Value);
                        curves[m].Temp[i] = byte.Parse(f.Attributes["temp"].Value);
                    }
                    d[idx] = curves;
                }
            }
            catch (Exception ex) { Log.Write("fan xml load: " + ex.Message); }
            foreach (int idx in new[] { 0, 2, 3 }) if (!d.ContainsKey(idx)) d[idx] = new[] { FanCurve.Default(), FanCurve.Default() };
            return d;
        }

        // Same layout as the OEM writer. The untouched original is kept once in %LOCALAPPDATA%\ControlHub.
        public static void Save(Dictionary<int, FanCurve[]> d)
        {
            try
            {
                string backup = Path.Combine(AppDir.Data, "profile_Fan.original.xml");
                Directory.CreateDirectory(AppDir.Data);
                if (File.Exists(XmlPath) && !File.Exists(backup)) File.Copy(XmlPath, backup);
                var keys = new List<int>(d.Keys); keys.Sort();
                var sb = new System.Text.StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<root>\r\n");
                foreach (int idx in keys)
                {
                    string name = idx >= 0 && idx < Names.Length ? Names[idx] : "profile" + idx;
                    sb.AppendFormat("  <Profile ProfileIndex=\"{0}\" Name=\"{1}\" PowerModeID=\"{0}\" FanID=\"{0}\">\r\n", idx, name);
                    for (int m = 0; m < 2; m++)
                        for (int i = 0; i < 10; i++)
                            sb.AppendFormat("    <CustomFan FanModule=\"{0}\" index=\"{1}\" duty=\"{2}\" temp=\"{3}\" />\r\n", m, i, d[idx][m].Duty[i], d[idx][m].Temp[i]);
                    sb.Append("  </Profile>\r\n");
                }
                sb.Append("</root>");
                Directory.CreateDirectory(Path.GetDirectoryName(XmlPath));
                string tmp = XmlPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new System.Text.UTF8Encoding(false));
                if (File.Exists(XmlPath)) File.Replace(tmp, XmlPath, null); else File.Move(tmp, XmlPath);
                Log.Write("fan xml saved");
            }
            catch (Exception ex) { Log.Write("fan xml save: " + ex.Message); throw; }
        }
    }

    class ChargeState
    {
        public bool Supported, Enabled;
        public int Start, Stop, Choice;
        public List<int> Starts = new List<int>(), Stops = new List<int>();
    }

    // Live battery details from root\WMI (readable without admin).
    class BatteryInfo
    {
        public double RemainingWh, FullWh, RateW, Volts; public bool Charging, Discharging, Online; public int Cycles; public bool Ok;
        public static BatteryInfo Read()
        {
            var b = new BatteryInfo();
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus"))
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        b.RemainingWh = Convert.ToDouble(o["RemainingCapacity"]) / 1000;
                        b.Charging = (bool)o["Charging"]; b.Discharging = (bool)o["Discharging"]; b.Online = (bool)o["PowerOnline"];
                        b.RateW = (b.Discharging ? Convert.ToDouble(o["DischargeRate"]) : Convert.ToDouble(o["ChargeRate"])) / 1000;
                        b.Volts = Convert.ToDouble(o["Voltage"]) / 1000;
                        b.Ok = true;
                    }
                using (var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryFullChargedCapacity"))
                    foreach (System.Management.ManagementObject o in s.Get()) b.FullWh = Convert.ToDouble(o["FullChargedCapacity"]) / 1000;
                using (var s = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryCycleCount"))
                    foreach (System.Management.ManagementObject o in s.Get()) b.Cycles = Convert.ToInt32(o["CycleCount"]);
            }
            catch (Exception ex) { Log.Write("battery wmi: " + ex.Message); }
            return b;
        }
    }

    class FanState
    {
        public int Mode = -1;          // app 1029: 0 auto, 1 max, 6 custom (others are OEM variants)
        public int[] MaxRpm = { 0, 0 };
        public FanCurve[] Ec = { null, null };   // what the EC is running now (decoded table)
        public int EcProfileId;                  // table id byte (0x80 + profile)
        public int CapRpm;                       // header value at +8 (2300 here)
        public int PowerMode = -1;
        public bool Ok;
    }

    // Last fan table seen in each power mode (only readable while that mode runs), kept in settings.
    static class FanCache
    {
        public class Entry { public int FanMode, Id, Max0, Max1; public FanCurve Cpu, Gpu; public DateTime When; }
        static string Key(int pm) { return "ectab" + (pm == 0 ? 4 : pm); }
        public static void Store(FanState f)
        {
            // only the BIOS automatic table describes the mode; never overwrite it with a custom/max table
            if (f.PowerMode < 0 || f.Ec[0] == null || f.Mode == 6 || f.Mode == 1) return;
            var sb = new System.Text.StringBuilder();
            sb.Append(f.Mode).Append(';').Append(f.EcProfileId).Append(';').Append(f.MaxRpm[0]).Append(';').Append(f.MaxRpm[1]).Append(';');
            for (int i = 0; i < 10; i++) sb.Append(f.Ec[0].Temp[i]).Append(',').Append(f.Ec[0].Duty[i]).Append(i < 9 ? "," : ";");
            for (int i = 0; i < 10; i++) sb.Append(f.Ec[1].Temp[i]).Append(',').Append(f.Ec[1].Duty[i]).Append(i < 9 ? "," : ";");
            sb.Append(DateTime.Now.ToString("s"));
            Settings.Set(Key(f.PowerMode), sb.ToString());
        }
        public static Entry Get(int pm)
        {
            try
            {
                var p = Settings.Get(Key(pm), "").Split(';');
                if (p.Length < 7) return null;
                var e = new Entry { FanMode = int.Parse(p[0]), Id = int.Parse(p[1]), Max0 = int.Parse(p[2]), Max1 = int.Parse(p[3]), Cpu = new FanCurve(), Gpu = new FanCurve() };
                var a = p[4].Split(','); var b = p[5].Split(',');
                for (int i = 0; i < 10; i++) { e.Cpu.Temp[i] = byte.Parse(a[i * 2]); e.Cpu.Duty[i] = byte.Parse(a[i * 2 + 1]); e.Gpu.Temp[i] = byte.Parse(b[i * 2]); e.Gpu.Duty[i] = byte.Parse(b[i * 2 + 1]); }
                DateTime.TryParse(p[6], out e.When);
                return e;
            }
            catch { return null; }
        }
    }

    // What actually happened in each power mode on this laptop (RAPL package power, temps, fans).
    static class ModeStats
    {
        public class S { public long N; public double SumW, PeakW, PeakT, PeakRpm, SumT; }
        static readonly Dictionary<int, S> map = new Dictionary<int, S>();
        static DateTime saved = DateTime.Now;
        static int Norm(int m) { return m == 0 ? 4 : m; }

        public static void Load()
        {
            foreach (int m in new[] { 1, 2, 3, 4 })
            {
                var p = Settings.Get("stats" + m, "").Split(';');
                if (p.Length != 6) continue;
                try { map[m] = new S { N = long.Parse(p[0]), SumW = double.Parse(p[1], K.EN), PeakW = double.Parse(p[2], K.EN), PeakT = double.Parse(p[3], K.EN), PeakRpm = double.Parse(p[4], K.EN), SumT = double.Parse(p[5], K.EN) }; }
                catch { }
            }
        }
        public static void Add(Snapshot s)
        {
            if (s.Mode < 0 || s.CpuWatts < 0) return;
            int m = Norm(s.Mode);
            S st;
            lock (map) { if (!map.TryGetValue(m, out st)) { st = new S(); map[m] = st; } }
            st.N++; st.SumW += s.CpuWatts; st.SumT += s.CpuTemp;
            st.PeakW = Math.Max(st.PeakW, s.CpuWatts); st.PeakT = Math.Max(st.PeakT, s.CpuTemp);
            st.PeakRpm = Math.Max(st.PeakRpm, Math.Max(s.Fans[0].Rpm, s.Fans[1].Rpm));
            if ((DateTime.Now - saved).TotalSeconds > 60) Save();
        }
        public static void Save()
        {
            saved = DateTime.Now;
            lock (map)
                foreach (var kv in map)
                    Settings.Set("stats" + kv.Key, string.Format(K.EN, "{0};{1:0.0};{2:0.0};{3:0};{4:0};{5:0}", kv.Value.N, kv.Value.SumW, kv.Value.PeakW, kv.Value.PeakT, kv.Value.PeakRpm, kv.Value.SumT));
        }
        public static S Get(int mode) { S s; lock (map) return map.TryGetValue(Norm(mode), out s) ? s : null; }
    }

    static class Win
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMSTATUS
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMSTATUS m);
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    }
}
