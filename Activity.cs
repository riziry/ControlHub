// Activity: a small task manager. Which apps use the processor, memory, graphics and disk.
// Sampling runs on its own thread, only while the Activity page is on screen:
//  - one NtQuerySystemInformation call returns CPU time, private memory and I/O for every process
//  - GPU per process comes from the "GPU Engine" performance counters (instance names carry the pid
//    and the adapter LUID); DXGI tells which LUID is the discrete GPU
//  - network is the machine total (per-app network needs an admin ETW session)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ControlHub
{
    // ===================================================================== data
    class ProcGroup
    {
        public string Key, Name, Desc, Path;
        public ImageSource Icon;
        public int Count;
        public double Cpu, Gpu, DGpu, Io;   // %, %, %, bytes/s
        public long Mem;                     // private working set
        public bool OnDgpu;                  // has a context on the discrete GPU
        public readonly List<int> Pids = new List<int>();
        public string DisplayName { get { return string.IsNullOrEmpty(Desc) ? Name : Desc; } }
    }

    class ProcFrame
    {
        public List<ProcGroup> Groups = new List<ProcGroup>();
        public int ProcCount;
        public double NetDown, NetUp, Io;           // bytes/s
        public double Igpu = -1, Dgpu = -1;         // adapter utilisation %, -1 unknown
        public string IgpuName = "", DgpuName = "";
        public double SampleMs;
        public DateTime Time;
    }

    // ===================================================================== sampler
    static class ProcMon
    {
        public static int IntervalMs = 1000;
        public static event Action<ProcFrame> Updated;   // raised on the sampler thread
        public static ProcFrame Last = new ProcFrame();
        static volatile bool active;
        static Thread thread;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);

        public static void SetActive(bool on)
        {
            active = on;
            if (on && thread == null)
            {
                thread = new Thread(Loop) { IsBackground = true, Name = "procmon", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }
            wake.Set();
        }

        static void Loop()
        {
            Perf.Mark("procmon");
            var sw = Stopwatch.StartNew();
            long idleSince = 0; bool wasIdle = true;
            while (true)
            {
                if (!active || Hw.Background)
                {
                    if (!wasIdle) { wasIdle = true; idleSince = sw.ElapsedMilliseconds; }
                    wake.WaitOne(1000);
                    continue;
                }
                // after a long pause the first deltas would be an average over the pause; start fresh
                if (wasIdle) { wasIdle = false; if (sw.ElapsedMilliseconds - idleSince > 3000) ResetDeltas(); }
                long t0 = sw.ElapsedMilliseconds;
                try
                {
                    var f = Sample();
                    f.SampleMs = sw.ElapsedMilliseconds - t0;
                    Last = f;
                    var h = Updated; if (h != null) h(f);
                }
                catch (Exception ex) { Log.Write("procmon: " + ex.Message); }
                int wait = (int)Math.Max(50, IntervalMs - (sw.ElapsedMilliseconds - t0));
                wake.WaitOne(wait);
            }
        }

        // ---------------------------------------------------------------- processes
        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

        class Prev { public long Create, Cpu, Io; }
        class Info { public long Create; public string Path; }
        class FileMeta { public string Desc; public ImageSource Icon; }
        static readonly Dictionary<int, Prev> prev = new Dictionary<int, Prev>();
        static readonly Dictionary<int, Info> info = new Dictionary<int, Info>();
        static readonly Dictionary<string, FileMeta> meta = new Dictionary<string, FileMeta>(StringComparer.OrdinalIgnoreCase);
        static IntPtr buf = IntPtr.Zero; static int bufLen;
        static long lastTicks;
        static long netRx = -1, netTx = -1;

        static void ResetDeltas() { prev.Clear(); lastTicks = 0; netRx = netTx = -1; gpuPrimed = false; }

        static ProcFrame Sample()
        {
            var f = new ProcFrame { Time = DateTime.Now };
            long now = DateTime.UtcNow.Ticks;
            double elapsed = lastTicks == 0 ? 0 : now - lastTicks;   // 100 ns units
            lastTicks = now;
            int cores = Environment.ProcessorCount;

            // one call for all processes
            if (buf == IntPtr.Zero) { bufLen = 1 << 20; buf = Marshal.AllocHGlobal(bufLen); }
            int need, st;
            while ((st = NtQuerySystemInformation(5, buf, bufLen, out need)) == unchecked((int)0xC0000004))
            {
                Marshal.FreeHGlobal(buf); bufLen = Math.Max(bufLen * 2, need + 65536); buf = Marshal.AllocHGlobal(bufLen);
            }
            if (st != 0) throw new Exception("NtQuerySystemInformation 0x" + st.ToString("X8"));

            var gpu = SampleGpu(f);
            var groups = new Dictionary<string, ProcGroup>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<int>();
            int self = Process.GetCurrentProcess().Id;
            long off = 0;
            while (true)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + off);
                int next = Marshal.ReadInt32(p, 0);
                int pid = (int)Marshal.ReadIntPtr(p, 80).ToInt64();
                if (pid != 0)
                {
                    long privWs = Marshal.ReadInt64(p, 8);
                    long create = Marshal.ReadInt64(p, 32);
                    long cpu = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48);
                    long io = Marshal.ReadInt64(p, 232) + Marshal.ReadInt64(p, 240);
                    int nameLen = Marshal.ReadInt16(p, 56) & 0xFFFF;
                    IntPtr namePtr = Marshal.ReadIntPtr(p, 64);
                    string name = nameLen > 0 && namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, nameLen / 2) : (pid == 4 ? "System" : "pid " + pid);
                    seen.Add(pid);

                    double cpuPct = 0, ioRate = 0;
                    Prev pv;
                    if (prev.TryGetValue(pid, out pv) && pv.Create == create && elapsed > 0)
                    {
                        cpuPct = Math.Max(0, (cpu - pv.Cpu) / elapsed / cores * 100);
                        ioRate = Math.Max(0, (io - pv.Io) / (elapsed / 1e7));
                    }
                    if (pv == null || pv.Create != create) { pv = new Prev(); prev[pid] = pv; }
                    pv.Create = create; pv.Cpu = cpu; pv.Io = io;

                    Info inf;
                    if (!info.TryGetValue(pid, out inf) || inf.Create != create) { inf = new Info { Create = create, Path = ImagePath(pid) }; info[pid] = inf; }

                    ProcGroup g;
                    if (!groups.TryGetValue(name, out g))
                    {
                        g = new ProcGroup { Key = name, Name = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name };
                        groups[name] = g;
                    }
                    if (g.Path == null && inf.Path != null)
                    {
                        g.Path = inf.Path;
                        var m = Meta(inf.Path); g.Desc = m.Desc; g.Icon = m.Icon;
                    }
                    g.Count++; g.Pids.Add(pid);
                    g.Cpu += cpuPct; g.Mem += privWs; g.Io += ioRate;
                    f.Io += ioRate;
                    GpuUse u;
                    if (gpu.TryGetValue(pid, out u)) { g.Gpu = Math.Max(g.Gpu, u.Any); g.DGpu = Math.Max(g.DGpu, u.D); g.OnDgpu |= u.OnD; }
                    f.ProcCount++;
                }
                if (next == 0) break;
                off += next;
            }
            // forget processes that ended
            if (prev.Count > seen.Count + 64)
            {
                var gone = new List<int>();
                foreach (var k in prev.Keys) if (!seen.Contains(k)) gone.Add(k);
                foreach (var k in gone) { prev.Remove(k); info.Remove(k); }
            }
            f.Groups.AddRange(groups.Values);
            SampleNet(f, elapsed);
            return f;
        }

        static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int n = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null;
            }
            finally { CloseHandle(h); }
        }

        static FileMeta Meta(string path)
        {
            FileMeta m;
            if (meta.TryGetValue(path, out m)) return m;
            m = new FileMeta();
            try { var v = FileVersionInfo.GetVersionInfo(path); if (!string.IsNullOrEmpty(v.FileDescription)) m.Desc = v.FileDescription.Trim(); } catch { }
            try
            {
                using (var ic = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    src.Freeze(); m.Icon = src;
                }
            }
            catch { }
            meta[path] = m;
            return m;
        }

        // ---------------------------------------------------------------- network (machine total)
        static void SampleNet(ProcFrame f, double elapsed)
        {
            long rx = 0, tx = 0;
            try
            {
                foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    var t = n.NetworkInterfaceType;
                    if (t == System.Net.NetworkInformation.NetworkInterfaceType.Loopback || t == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                    var s = n.GetIPv4Statistics();
                    rx += s.BytesReceived; tx += s.BytesSent;
                }
            }
            catch { return; }
            if (netRx >= 0 && elapsed > 0)
            {
                double sec = elapsed / 1e7;
                f.NetDown = Math.Max(0, (rx - netRx) / sec); f.NetUp = Math.Max(0, (tx - netTx) / sec);
            }
            netRx = rx; netTx = tx;
        }

        // ---------------------------------------------------------------- GPU
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref int size, out int count, IntPtr items);

        class GpuUse { public double Any, D; public bool OnD; }
        static IntPtr pdhQ, pdhC; static bool pdhTried, gpuPrimed;
        static IntPtr gbuf = IntPtr.Zero; static int gbufLen;
        static string dLuid, iLuid;   // "high_low" in the counter's hex spelling

        static Dictionary<int, GpuUse> SampleGpu(ProcFrame f)
        {
            var res = new Dictionary<int, GpuUse>();
            if (!pdhTried)
            {
                pdhTried = true;
                try { FindAdapters(); } catch (Exception ex) { Log.Write("procmon dxgi: " + ex.Message); }
                if (PdhOpenQuery(null, IntPtr.Zero, out pdhQ) != 0 || PdhAddEnglishCounter(pdhQ, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out pdhC) != 0)
                { pdhQ = IntPtr.Zero; Log.Write("procmon: GPU counters unavailable"); }
            }
            if (pdhQ == IntPtr.Zero) return res;
            if (PdhCollectQueryData(pdhQ) != 0) return res;
            if (!gpuPrimed) { gpuPrimed = true; return res; }   // rate counters need two samples
            int size = 0, count;
            const uint FMT = 0x200 | 0x8000;   // double, no cap at 100
            int st = PdhGetFormattedCounterArray(pdhC, FMT, ref size, out count, IntPtr.Zero);
            if (st != unchecked((int)0x800007D2) || size <= 0) return res;
            if (size > gbufLen) { if (gbuf != IntPtr.Zero) Marshal.FreeHGlobal(gbuf); gbufLen = size * 2; gbuf = Marshal.AllocHGlobal(gbufLen); }
            size = gbufLen;
            if (PdhGetFormattedCounterArray(pdhC, FMT, ref size, out count, gbuf) != 0) return res;

            // sum per (pid, adapter, engine type); a process's usage is its busiest engine type (Task Manager's rule)
            var perEng = new Dictionary<string, double>();
            var adapter = new Dictionary<string, double>();
            var dUsers = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                IntPtr item = new IntPtr(gbuf.ToInt64() + i * 24);
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0));
                int cs = Marshal.ReadInt32(item, 8);
                double v = cs == 0 || cs == 1 ? BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16)) : 0;
                // pid_1234_luid_0x00000000_0x0000D1A2_phys_0_eng_3_engtype_3D
                int a = name.IndexOf('_', 4); if (!name.StartsWith("pid_") || a < 0) continue;
                int pid; if (!int.TryParse(name.Substring(4, a - 4), out pid)) continue;
                int l = name.IndexOf("luid_", a, StringComparison.Ordinal), ph = name.IndexOf("_phys_", a, StringComparison.Ordinal), et = name.IndexOf("engtype_", a, StringComparison.Ordinal);
                if (l < 0 || ph < 0 || et < 0) continue;
                string luid = name.Substring(l + 5, ph - l - 5).ToLowerInvariant();
                string eng = name.Substring(et + 8);
                if (luid == dLuid && pid > 4) dUsers.Add(pid);   // the kernel holds every adapter
                string k = pid + "|" + luid + "|" + eng;
                double cur; perEng.TryGetValue(k, out cur); perEng[k] = cur + v;
                string ak = luid + "|" + eng;
                adapter.TryGetValue(ak, out cur); adapter[ak] = cur + v;
            }
            foreach (var kv in perEng)
            {
                var p = kv.Key.Split('|');
                int pid = int.Parse(p[0]);
                GpuUse u; if (!res.TryGetValue(pid, out u)) { u = new GpuUse(); res[pid] = u; }
                u.Any = Math.Max(u.Any, kv.Value);
                if (p[1] == dLuid) u.D = Math.Max(u.D, kv.Value);
            }
            foreach (int pid in dUsers) { GpuUse u; if (!res.TryGetValue(pid, out u)) { u = new GpuUse(); res[pid] = u; } u.OnD = true; }
            double ig = 0, dg = 0; bool seenI = false, seenD = false;
            foreach (var kv in adapter)
            {
                string luid = kv.Key.Substring(0, kv.Key.IndexOf('|'));
                if (luid == dLuid) { seenD = true; dg = Math.Max(dg, kv.Value); }
                else if (luid == iLuid || iLuid == null) { seenI = true; ig = Math.Max(ig, kv.Value); }
            }
            f.Dgpu = dLuid == null ? -1 : seenD ? Math.Min(100, dg) : 0;
            f.Igpu = seenI ? Math.Min(100, ig) : 0;
            f.DgpuName = dName ?? ""; f.IgpuName = iName ?? "";
            return res;
        }

        // ---- DXGI: adapter names and LUIDs (enumeration only; no device is created, nothing wakes)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow; public int LuidHigh; public uint Flags;
        }
        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 d);
        }
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint i, out IDXGIAdapter1 a);
        }
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object f);
        static string dName, iName;

        static void FindAdapters()
        {
            var iid = typeof(IDXGIFactory1).GUID;
            object o;
            if (CreateDXGIFactory1(ref iid, out o) != 0 || o == null) return;
            var fac = (IDXGIFactory1)o;
            try
            {
                for (uint i = 0; i < 8; i++)
                {
                    IDXGIAdapter1 a;
                    if (fac.EnumAdapters1(i, out a) != 0 || a == null) break;
                    DXGI_ADAPTER_DESC1 d;
                    if (a.GetDesc1(out d) == 0 && (d.Flags & 2) == 0 && d.VendorId != 0x1414)   // skip software / Microsoft Basic
                    {
                        string luid = string.Format("0x{0:x8}_0x{1:x8}", d.LuidHigh, d.LuidLow);
                        bool integrated = d.VendorId == 0x8086 || (d.DedicatedVideoMemory.ToUInt64() < 600UL * 1024 * 1024);
                        if (integrated && iLuid == null) { iLuid = luid; iName = d.Description.Trim(); }
                        else if (!integrated && dLuid == null) { dLuid = luid; dName = d.Description.Trim(); }
                        Log.Write("procmon adapter: " + d.Description.Trim() + " vendor=0x" + d.VendorId.ToString("x4") + " luid=" + luid + (integrated ? " (integrated)" : " (discrete)"));
                    }
                    Marshal.ReleaseComObject(a);
                }
            }
            finally { Marshal.ReleaseComObject(fac); }
        }

        // ---------------------------------------------------------------- actions
        static readonly HashSet<string> critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Registry", "Secure System", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe",
            "lsaiso.exe", "winlogon.exe", "svchost.exe", "dwm.exe", "fontdrvhost.exe", "LsaIso.exe", "MsMpEng.exe", "audiodg.exe", "controlhub.exe"
        };
        public static bool CanEnd(ProcGroup g) { return !critical.Contains(g.Key); }

        // Ends every process of the group; returns how many could not be ended.
        public static int End(List<int> pids)
        {
            int failed = 0;
            foreach (int pid in pids)
            {
                try { using (var p = Process.GetProcessById(pid)) { p.Kill(); } }
                catch (ArgumentException) { }          // already gone
                catch (Exception) { failed++; }        // access denied (elevated / protected)
            }
            Log.Write("activity: end " + pids.Count + " process(es), " + failed + " failed");
            return failed;
        }
    }

    // ===================================================================== page
    class ActivityPage : IPage
    {
        enum SortBy { Name, Cpu, Mem, Gpu, Io }
        readonly MainWindow w;
        readonly StackPanel stack = new StackPanel { Margin = new Thickness(36, 0, 36, 36) };
        readonly ScrollViewer view;
        // summary tiles
        class Tile { public Border Card; public TextBlock Value, Sub; public UI.Bar Bar; }
        readonly Tile tCpu, tMem, tGpu, tIo, tNet;
        // GPU card
        readonly Border gIcon; readonly TextBlock gGlyph, gTitle, gSub;
        // table
        readonly StackPanel rows = new StackPanel();
        readonly List<Row> pool = new List<Row>();
        readonly TextBlock countText, moreText; readonly Border moreBtn;
        readonly TextBox filter;
        readonly Dictionary<SortBy, TextBlock> heads = new Dictionary<SortBy, TextBlock>(), arrows = new Dictionary<SortBy, TextBlock>();
        SortBy sort = SortBy.Cpu; bool showAll;
        ProcFrame frame = new ProcFrame();
        static readonly GridLength[] Cols = { new GridLength(1, GridUnitType.Star), new GridLength(92), new GridLength(104), new GridLength(92), new GridLength(104), new GridLength(80) };
        const int Collapsed = 25;

        public string Title { get { return "Activity"; } }
        public string Subtitle { get { return "Which apps are using the processor, memory, graphics and disk"; } }
        public FrameworkElement View { get { return view; } }
        public Panel StaggerRoot { get { return stack; } }

        public ActivityPage(MainWindow w)
        {
            this.w = w;
            view = UI.Scroll(stack);

            // ---- summary
            var r1 = UI.Columns(1, 1, 1, 1, 1);
            tCpu = MakeTile("", "Processor", SortBy.Cpu); UI.Put(r1, tCpu.Card, 0);
            tMem = MakeTile("", "Memory", SortBy.Mem); UI.Put(r1, tMem.Card, 1);
            tGpu = MakeTile("", "Graphics", SortBy.Gpu); UI.Put(r1, tGpu.Card, 2);
            tIo = MakeTile("", "Disk I/O", SortBy.Io); UI.Put(r1, tIo.Card, 3);
            tNet = MakeTile("", "Network", SortBy.Name); UI.Put(r1, tNet.Card, 4);
            tNet.Card.Cursor = null; tNet.Card.ToolTip = "Whole machine. Per-app network traffic needs ControlHub to run as administrator.";
            stack.Children.Add(r1);

            // ---- discrete GPU: who keeps it awake
            var gg = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            gg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); gg.ColumnDefinitions.Add(new ColumnDefinition());
            gIcon = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(14), Background = Theme.GreenSoft, VerticalAlignment = VerticalAlignment.Center };
            gGlyph = K.Glyph("", 19, Theme.Green); gIcon.Child = gGlyph;
            gg.Children.Add(gIcon);
            var gt = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            gTitle = K.TB("Reading graphics activity…", 15, Theme.Text, FontWeights.SemiBold);
            gSub = K.TB("", 12.5, Theme.Text2, FontWeights.Medium); gSub.Margin = new Thickness(0, 3, 0, 0); gSub.TextWrapping = TextWrapping.Wrap; gSub.TextTrimming = TextTrimming.None;
            gt.Children.Add(gTitle); gt.Children.Add(gSub);
            Grid.SetColumn(gt, 1); gg.Children.Add(gt);
            var gcard = K.Card(gg, new Thickness(20, 16, 20, 16)); gcard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(gcard);

            // ---- table
            var tsp = new StackPanel();
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            tl.Children.Add(UI.Header("", "Apps and processes", null));
            countText = K.TB("", 12.5, Theme.Text3, FontWeights.Medium); countText.Margin = new Thickness(12, 2, 0, 0);
            tl.Children.Add(countText);
            top.Children.Add(tl);
            // filter box
            var fb = new Border { Width = 230, Height = 34, CornerRadius = new CornerRadius(11), Background = Theme.Raised, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1) };
            var fg = new Grid();
            var mag = K.Glyph("", 12, Theme.Text3); mag.HorizontalAlignment = HorizontalAlignment.Left; mag.Margin = new Thickness(12, 0, 0, 0);
            fg.Children.Add(mag);
            filter = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Theme.Text, CaretBrush = Theme.Text, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(32, 0, 10, 0), Padding = new Thickness(0) };
            filter.FocusVisualStyle = null;
            var ph = K.TB("Filter apps", 13, Theme.Text3, FontWeights.Normal); ph.Margin = new Thickness(34, 0, 0, 0); ph.IsHitTestVisible = false;
            fg.Children.Add(ph); fg.Children.Add(filter);
            fb.Child = fg;
            filter.TextChanged += delegate { ph.Visibility = filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Render(); };
            filter.KeyDown += (s, e) => { if (e.Key == Key.Escape) { filter.Text = ""; e.Handled = true; } };
            Grid.SetColumn(fb, 1); top.Children.Add(fb);
            tsp.Children.Add(top);

            // column headers
            var hg = MakeGrid(); hg.Margin = new Thickness(0, 18, 0, 6);
            AddHead(hg, 0, "Name", SortBy.Name, HorizontalAlignment.Left);
            AddHead(hg, 1, "CPU", SortBy.Cpu, HorizontalAlignment.Right);
            AddHead(hg, 2, "Memory", SortBy.Mem, HorizontalAlignment.Right);
            AddHead(hg, 3, "GPU", SortBy.Gpu, HorizontalAlignment.Right);
            AddHead(hg, 4, "Disk I/O", SortBy.Io, HorizontalAlignment.Right);
            tsp.Children.Add(hg);
            tsp.Children.Add(new Border { Height = 1, Background = Theme.Hairline, Margin = new Thickness(0, 0, 0, 4) });
            tsp.Children.Add(rows);
            moreText = K.TB("", 13, Theme.Accent, FontWeights.SemiBold); moreText.HorizontalAlignment = HorizontalAlignment.Center;
            moreBtn = new Border { Height = 38, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Child = moreText, Margin = new Thickness(0, 8, 0, 0), Cursor = Cursors.Hand };
            K.Pressable(moreBtn, () => { showAll = !showAll; Render(); }, true);
            tsp.Children.Add(moreBtn);
            var tcard = K.Card(tsp, new Thickness(22, 20, 22, 14)); tcard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(tcard);

            var note = K.TB("Updates every second while this page is open; nothing is sampled when it isn't. CPU is the share of all cores. Disk I/O counts every file and device read or write.", 12, Theme.Text3, FontWeights.Medium);
            note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None; note.Margin = new Thickness(4, 10, 0, 0);
            stack.Children.Add(note);

            view.IsVisibleChanged += delegate { ProcMon.SetActive(view.IsVisible); };
            ProcMon.Updated += f => w.Dispatcher.BeginInvoke(new Action(() => OnFrame(f)));
            UpdateSortUi();
        }

        // ---------------------------------------------------------------- building blocks
        Tile MakeTile(string glyph, string name, SortBy by)
        {
            var t = new Tile();
            var sp = new StackPanel();
            sp.Children.Add(UI.Header(glyph, name, null));
            t.Value = K.TB("–", 24, Theme.Text, FontWeights.Bold); t.Value.FontFamily = K.Display; t.Value.Margin = new Thickness(0, 14, 0, 0); K.Tabular(t.Value);
            sp.Children.Add(t.Value);
            t.Sub = K.TB("", 12, Theme.Text3, FontWeights.Medium); t.Sub.Margin = new Thickness(0, 2, 0, 12);
            sp.Children.Add(t.Sub);
            t.Bar = new UI.Bar(6, Theme.AccentFill);
            sp.Children.Add(t.Bar);
            t.Card = K.Card(sp, new Thickness(18, 16, 18, 18));
            if (by != SortBy.Name)
            {
                t.Card.Cursor = Cursors.Hand;
                t.Card.ToolTip = "Sort apps by " + name.ToLowerInvariant();
                t.Card.MouseLeftButtonUp += (s, e) => { sort = by; UpdateSortUi(); Render(); };
            }
            return t;
        }

        static Grid MakeGrid()
        {
            var g = new Grid();
            foreach (var c in Cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
            return g;
        }

        void AddHead(Grid g, int col, string text, SortBy by, HorizontalAlignment ha)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = ha };
            var t = K.TB(text, 12, Theme.Text3, FontWeights.SemiBold);
            var a = K.Glyph(by == SortBy.Name ? "" : "", 9, Theme.Accent); a.Margin = new Thickness(5, 1, 0, 0);
            if (ha == HorizontalAlignment.Right) { sp.Children.Add(a); sp.Children.Add(t); a.Margin = new Thickness(0, 1, 5, 0); }
            else { sp.Children.Add(t); sp.Children.Add(a); }
            var b = new Border { Background = Brushes.Transparent, Child = sp, Cursor = Cursors.Hand, Padding = new Thickness(col == 0 ? 40 : 0, 4, col == 0 ? 0 : 12, 4) };
            b.MouseLeftButtonUp += (s, e) => { sort = by; UpdateSortUi(); Render(); };
            Grid.SetColumn(b, col); g.Children.Add(b);
            heads[by] = t; arrows[by] = a;
        }

        void UpdateSortUi()
        {
            foreach (var kv in heads) kv.Value.Foreground = kv.Key == sort ? Theme.Text : Theme.Text3;
            foreach (var kv in arrows) kv.Value.Visibility = kv.Key == sort ? Visibility.Visible : Visibility.Hidden;
            var map = new Dictionary<SortBy, Tile> { { SortBy.Cpu, tCpu }, { SortBy.Mem, tMem }, { SortBy.Gpu, tGpu }, { SortBy.Io, tIo } };
            foreach (var kv in map) kv.Value.Card.BorderBrush = kv.Key == sort ? (Brush)K.Solid(Theme.A(Theme.Current.Accent, 0.55)) : Theme.Hairline;
        }

        public void ThemeChanged() { UpdateSortUi(); Render(); }

        // ---------------------------------------------------------------- one table row (reused)
        class Row
        {
            public Border Root; public Image Icon; public TextBlock Glyph, Name, Count, DTag;
            public TextBlock[] Val = new TextBlock[4]; public SolidColorBrush[] Heat = new SolidColorBrush[4];
            public StackPanel Actions; public Border EndBtn, OpenBtn;
            public ProcGroup G;
        }

        Row MakeRow()
        {
            var r = new Row();
            var g = MakeGrid(); g.Height = 40;
            // name cell
            var nc = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            var ih = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0) };
            r.Icon = new Image { Width = 20, Height = 20 }; RenderOptions.SetBitmapScalingMode(r.Icon, BitmapScalingMode.HighQuality);
            r.Glyph = K.Glyph("", 15, Theme.Text3);
            ih.Children.Add(r.Glyph); ih.Children.Add(r.Icon);
            nc.Children.Add(ih);
            r.Name = K.TB("", 13.5, Theme.Text, FontWeights.SemiBold); r.Name.MaxWidth = 340;
            nc.Children.Add(r.Name);
            r.Count = K.TB("", 12, Theme.Text3, FontWeights.Medium); r.Count.Margin = new Thickness(8, 1, 0, 0);
            nc.Children.Add(r.Count);
            r.DTag = K.TB("dGPU", 10.5, Theme.Orange, FontWeights.Bold);
            var dtb = new Border { CornerRadius = new CornerRadius(6), Background = Theme.OrangeSoft, Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), Child = r.DTag, VerticalAlignment = VerticalAlignment.Center };
            dtb.ToolTip = "Has the discrete GPU open — keeps it awake";
            nc.Children.Add(dtb);
            g.Children.Add(nc);
            // numeric cells with a heat tint (like Task Manager)
            for (int i = 0; i < 4; i++)
            {
                r.Heat[i] = new SolidColorBrush(Colors.Transparent);
                r.Val[i] = K.TB("", 13, Theme.Text, FontWeights.Medium); r.Val[i].HorizontalAlignment = HorizontalAlignment.Right; K.Tabular(r.Val[i]);
                var cb = new Border { Background = r.Heat[i], Child = r.Val[i], Padding = new Thickness(0, 0, 12, 0), Margin = new Thickness(2, 3, 0, 3), CornerRadius = new CornerRadius(8) };
                Grid.SetColumn(cb, i + 1); g.Children.Add(cb);
            }
            // actions (appear on hover)
            r.Actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            r.OpenBtn = K.IconBtn("", 30, 13, Theme.Text2, () => OpenLocation(r.G)); r.OpenBtn.ToolTip = "Open file location";
            r.EndBtn = K.IconBtn("", 30, 12, Theme.Red, () => EndTask(r.G)); r.EndBtn.ToolTip = "End task"; r.EndBtn.Margin = new Thickness(4, 0, 4, 0);
            r.Actions.Children.Add(r.OpenBtn); r.Actions.Children.Add(r.EndBtn);
            Grid.SetColumn(r.Actions, 5); g.Children.Add(r.Actions);

            r.Root = new Border { Child = g, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent };
            r.Root.MouseEnter += delegate { r.Root.Background = K.Solid(Theme.A(Theme.Current.Text, 0.045)); r.Actions.BeginAnimation(UIElement.OpacityProperty, K.Anim(1, 140, K.OutCubic)); };
            r.Root.MouseLeave += delegate { r.Root.Background = Brushes.Transparent; r.Actions.BeginAnimation(UIElement.OpacityProperty, K.Anim(0, 200, K.OutCubic)); };
            return r;
        }

        void Fill(Row r, ProcGroup g, double ramTotal)
        {
            r.G = g;
            r.Name.Text = g.DisplayName;
            r.Count.Text = g.Count > 1 ? "×" + g.Count : "";
            ((Border)r.DTag.Parent).Visibility = g.OnDgpu ? Visibility.Visible : Visibility.Collapsed;
            r.Icon.Source = g.Icon; r.Glyph.Visibility = g.Icon == null ? Visibility.Visible : Visibility.Hidden;
            r.Root.ToolTip = (g.Path ?? g.Key) + "\n" + (g.Count == 1 ? "PID " + g.Pids[0] : g.Count + " processes · PID " + string.Join(", ", g.Pids.GetRange(0, Math.Min(8, g.Pids.Count))) + (g.Pids.Count > 8 ? ", …" : ""));
            r.Val[0].Text = Pct(g.Cpu);
            r.Val[1].Text = K.Bytes(g.Mem);
            r.Val[2].Text = Pct(g.Gpu);
            r.Val[3].Text = Rate(g.Io);
            var hc = Theme.Current.Orange;
            r.Heat[0].Color = Theme.A(hc, Heat(g.Cpu / 50));
            r.Heat[1].Color = Theme.A(hc, Heat(ramTotal > 0 ? g.Mem / ramTotal * 5 : 0));
            r.Heat[2].Color = Theme.A(hc, Heat(g.Gpu / 60));
            r.Heat[3].Color = Theme.A(hc, Heat(g.Io / (40.0 * 1048576)));
            r.EndBtn.Visibility = ProcMon.CanEnd(g) ? Visibility.Visible : Visibility.Collapsed;
            r.OpenBtn.Visibility = g.Path != null ? Visibility.Visible : Visibility.Collapsed;
        }

        static double Heat(double t) { t = Math.Max(0, Math.Min(1, t)); return t < 0.02 ? 0 : 0.06 + 0.34 * t; }
        static string Pct(double v) { return v < 0.05 ? "0%" : v < 10 ? v.ToString("0.0", K.EN) + "%" : v.ToString("0", K.EN) + "%"; }
        static string Rate(double b) { return b < 1024 * 10 ? "0 MB/s" : b < 1048576 * 10 ? (b / 1048576).ToString("0.0", K.EN) + " MB/s" : (b / 1048576).ToString("0", K.EN) + " MB/s"; }
        static string Speed(double b) { return b < 1024 ? "0 KB/s" : b < 1048576 ? (b / 1024).ToString("0", K.EN) + " KB/s" : (b / 1048576).ToString("0.0", K.EN) + " MB/s"; }

        // ---------------------------------------------------------------- actions
        void OpenLocation(ProcGroup g)
        {
            if (g == null || g.Path == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + g.Path + "\""); } catch (Exception ex) { Log.Write("activity open: " + ex.Message); }
        }

        void EndTask(ProcGroup g)
        {
            if (g == null || !ProcMon.CanEnd(g)) return;
            var pids = new List<int>(g.Pids); string name = g.DisplayName;
            string body = pids.Count == 1
                ? "Unsaved work in " + name + " will be lost."
                : "This ends all " + pids.Count + " " + g.Key + " processes. Unsaved work in " + name + " will be lost.";
            w.Confirm("End " + name + "?", body, "End task", ok =>
            {
                if (!ok) return;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    int failed = ProcMon.End(pids);
                    w.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (failed == 0) w.Island.Show("", "Ended " + name, Theme.Red);
                        else w.Island.Show("", failed == pids.Count ? "Couldn't end " + name + " — needs administrator" : "Ended part of " + name + " (" + failed + " need administrator)", Theme.Orange);
                    }));
                });
            });
        }

        // ---------------------------------------------------------------- live data
        void OnFrame(ProcFrame f) { frame = f; if (view.IsVisible) Render(); }

        List<ProcGroup> Sorted()
        {
            var list = new List<ProcGroup>();
            string q = filter.Text.Trim();
            foreach (var g in frame.Groups)
                if (q.Length == 0 || g.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || g.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) list.Add(g);
            Comparison<ProcGroup> by;
            switch (sort)
            {
                case SortBy.Name: by = (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase); break;
                case SortBy.Mem: by = (a, b) => b.Mem.CompareTo(a.Mem); break;
                case SortBy.Gpu: by = (a, b) => { int c = b.Gpu.CompareTo(a.Gpu); if (c == 0) c = b.OnDgpu.CompareTo(a.OnDgpu); return c != 0 ? c : b.Mem.CompareTo(a.Mem); }; break;
                case SortBy.Io: by = (a, b) => { int c = b.Io.CompareTo(a.Io); return c != 0 ? c : b.Mem.CompareTo(a.Mem); }; break;
                default: by = (a, b) => { int c = Math.Round(b.Cpu, 1).CompareTo(Math.Round(a.Cpu, 1)); return c != 0 ? c : b.Mem.CompareTo(a.Mem); }; break;
            }
            list.Sort(by);
            return list;
        }

        void Render()
        {
            var s = Hw.Last;
            var list = Sorted();
            int n = showAll ? list.Count : Math.Min(Collapsed, list.Count);
            while (pool.Count < n) { var r = MakeRow(); pool.Add(r); rows.Children.Add(r.Root); }
            for (int i = 0; i < pool.Count; i++)
            {
                if (i < n) { Fill(pool[i], list[i], s.RamTotal); pool[i].Root.Visibility = Visibility.Visible; }
                else { pool[i].Root.Visibility = Visibility.Collapsed; pool[i].G = null; }
            }
            countText.Text = frame.Groups.Count == 0 ? "" : string.Format(K.EN, "{0} apps · {1} processes", frame.Groups.Count, frame.ProcCount);
            moreBtn.Visibility = list.Count > Collapsed ? Visibility.Visible : Visibility.Collapsed;
            moreText.Text = showAll ? "Show fewer" : "Show all " + list.Count;

            // summary
            if (s.Time != default(DateTime))
            {
                tCpu.Value.Text = s.CpuLoad.ToString("0", K.EN) + "%";
                tCpu.Sub.Text = Environment.ProcessorCount + " threads" + (s.CpuWatts >= 0 ? string.Format(K.EN, " · {0:0.0} W", s.CpuWatts) : "");
                tCpu.Bar.Set(s.CpuLoad / 100);
                if (s.RamTotal > 0)
                {
                    tMem.Value.Text = (s.RamUsed / s.RamTotal * 100).ToString("0", K.EN) + "%";
                    tMem.Sub.Text = K.Bytes(s.RamUsed) + " of " + K.Bytes(s.RamTotal);
                    tMem.Bar.Set(s.RamUsed / s.RamTotal);
                }
            }
            if (frame.Time == default(DateTime)) return;
            bool asleep = s.Ok && UI.GpuAsleep(s);
            double gpuMax = Math.Max(0, Math.Max(frame.Igpu, frame.Dgpu));
            tGpu.Value.Text = Pct(gpuMax);
            tGpu.Sub.Text = frame.Dgpu < 0 ? "Integrated " + Pct(Math.Max(0, frame.Igpu))
                : "iGPU " + Pct(Math.Max(0, frame.Igpu)) + " · dGPU " + (asleep ? "off" : Pct(frame.Dgpu));
            tGpu.Bar.Set(gpuMax / 100);
            tIo.Value.Text = Rate(frame.Io);
            tIo.Sub.Text = "All apps, read + write";
            tIo.Bar.Set(Math.Min(1, frame.Io / (200.0 * 1048576)));
            tNet.Value.Text = Speed(frame.NetDown);
            tNet.Sub.Text = "↓ " + Speed(frame.NetDown) + " · ↑ " + Speed(frame.NetUp);
            tNet.Value.Text = Speed(frame.NetDown + frame.NetUp);
            tNet.Bar.Set(Math.Min(1, (frame.NetDown + frame.NetUp) / (12.5 * 1048576)));

            // discrete GPU card
            var users = new List<ProcGroup>();
            foreach (var g in frame.Groups) if (g.OnDgpu) users.Add(g);
            users.Sort((a, b) => { int c = b.DGpu.CompareTo(a.DGpu); return c != 0 ? c : b.Mem.CompareTo(a.Mem); });
            string dn = frame.DgpuName.Length > 0 ? frame.DgpuName : "Discrete GPU";
            Color col;
            if (frame.Dgpu < 0) { gTitle.Text = "No discrete GPU found"; gSub.Text = frame.IgpuName.Length > 0 ? "Graphics: " + frame.IgpuName : ""; col = Theme.Current.Accent; }
            else if (users.Count == 0)
            {
                gTitle.Text = asleep ? dn + " is asleep" : dn + " is idle";
                gSub.Text = "No app is using it. Everything runs on " + (frame.IgpuName.Length > 0 ? frame.IgpuName : "the integrated GPU") + ", which saves battery.";
                col = Theme.Current.Green;
            }
            else
            {
                var names = new List<string>();
                for (int i = 0; i < users.Count && i < 6; i++) names.Add(users[i].DisplayName + (users[i].DGpu >= 0.5 ? " (" + Pct(users[i].DGpu) + ")" : ""));
                gTitle.Text = dn + (asleep ? " — opened by " : " is awake — in use by ") + users.Count + (users.Count == 1 ? " app" : " apps");
                gSub.Text = string.Join(", ", names) + (users.Count > 6 ? " and " + (users.Count - 6) + " more" : "") + (users.Count == 1 ? ". Close it" : ". Close them") + " to let the discrete GPU sleep and save battery.";
                col = asleep ? Theme.Current.Accent : Theme.Current.Orange;
            }
            gGlyph.Foreground = K.Solid(col); gIcon.Background = K.Solid(Theme.A(col, 0.16));
        }

        public void Update(Snapshot s) { }
        public void OnShown() { UpdateSortUi(); Render(); }

        // IPC (tests): "top [n]" lists the busiest apps of the last sample
        public string Ipc(string arg)
        {
            var f = frame; var sb = new StringBuilder();
            sb.AppendFormat(K.EN, "sample={0:0}ms procs={1} apps={2} igpu={3:0.0} dgpu={4:0.0} ({5}) io={6} net=down {7}/up {8}\n", f.SampleMs, f.ProcCount, f.Groups.Count, f.Igpu, f.Dgpu, f.DgpuName, Rate(f.Io), Speed(f.NetDown), Speed(f.NetUp));
            var list = Sorted(); int n = 12; int.TryParse(arg, out n); if (n <= 0) n = 12;
            for (int i = 0; i < list.Count && i < n; i++)
            {
                var g = list[i];
                sb.AppendFormat(K.EN, "{0,-34} x{1,-3} cpu {2,6} mem {3,9} gpu {4,6} io {5,9}{6}\n", g.DisplayName.Length > 34 ? g.DisplayName.Substring(0, 34) : g.DisplayName, g.Count, Pct(g.Cpu), K.Bytes(g.Mem), Pct(g.Gpu), Rate(g.Io), g.OnDgpu ? " [dGPU]" : "");
            }
            return sb.ToString();
        }
    }
}
