// Entry point: single instance, settings, tray icon, IPC (for testing without input injection).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ControlHub
{
    static class Settings
    {
        static readonly Dictionary<string, string> map = new Dictionary<string, string>();
        static string FilePath { get { return Path.Combine(AppDir.Data, "settings.ini"); } }
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) map[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
        }
        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDir.Data);
                var sb = new StringBuilder();
                foreach (var kv in map) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }
        public static string Get(string k, string def) { string v; return map.TryGetValue(k, out v) ? v : def; }
        public static int Int(string k, int def) { int v; return int.TryParse(Get(k, ""), out v) ? v : def; }
        public static bool Bool(string k, bool def) { string v = Get(k, ""); return v == "" ? def : v == "1"; }
        public static void Set(string k, object v)
        {
            string s = v is bool ? ((bool)v ? "1" : "0") : Convert.ToString(v, K.EN);
            map[k] = s; Save();
        }
    }

    static class Program
    {
        public const string PipeName = "ControlHub_ipc";
        public static MainWindow Win;
        public static Forms.NotifyIcon Tray;
        public static bool Quitting;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--ipc") return Client(string.Join(" ", args, 1, args.Length - 1));

            bool created;
            var mutex = new Mutex(true, "ControlHub_single_instance", out created);
            if (!created) { Client("show"); return 0; }

            Settings.Load();
            ModeStats.Load();
            bool startHidden = Array.IndexOf(args, "--tray") >= 0;
            if (Array.IndexOf(args, "--dry") >= 0) Dchu.DryRun = true;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Log.Write("UI error: " + e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);

            string pal = Settings.Get("palette", "oled");
            var p = Theme.Palettes.Find(x => x.Key == pal) ?? Theme.Palettes[0];
            Theme.Apply(p, false);
            K.AnimScale = Settings.Int("anim", 100) / 100.0;
            Hw.IntervalMs = Settings.Int("interval", 1000);
            K.Font = K.Bundled("Inter-Regular.ttf", "Inter", K.Font);
            // tooltip colours: own (frozen) copies, swapped on every theme change. Never put the shared
            // theme brushes in resources: WPF freezes them there and theme switching breaks.
            Action tipColors = () =>
            {
                app.Resources["TipBg"] = K.Solid(Theme.Current.Raised);
                app.Resources["TipEdge"] = K.Solid(Theme.A(Theme.Current.Text, 0.08));
                app.Resources["TipFg"] = K.Solid(Theme.Current.Text);
            };
            tipColors();
            Theme.Changed += tipColors;
            app.Resources.Add(typeof(System.Windows.Controls.ToolTip), K.ToolTipStyle());
            KbFx.Load();

            Win = new MainWindow();
            SetupTray();
            if (startHidden) Hw.SetBackground(true);
            Hw.Start();
            // the BIOS restores whole keyboard levels after sleep; put a fine level back
            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += delegate { t.Stop(); Hw.KbReapplyFine(); };
                t.Start();
            };
            StartIpcServer();
            if (!startHidden) Win.Show();
            app.Run();
            KbFx.RestoreOnExit();
            ModeStats.Save();
            Hw.Stop();
            if (Tray != null) { Tray.Visible = false; Tray.Dispose(); }
            GC.KeepAlive(mutex);
            return 0;
        }

        public static void ShowWindow()
        {
            if (Win == null) return;
            if (!Win.IsVisible) Win.Show();
            if (Win.WindowState == WindowState.Minimized) Win.WindowState = WindowState.Normal;
            Win.Activate();
        }

        public static void Quit()
        {
            Quitting = true;
            if (Win != null) { Win.SaveBounds(); Win.Close(); }
            Application.Current.Shutdown();
        }

        // ---------------------------------------------------------------- tray
        static void SetupTray()
        {
            Tray = new Forms.NotifyIcon { Icon = MakeIcon(Theme.Current.Accent, Theme.Current.Accent2), Text = "ControlHub", Visible = true };
            Tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Win.Dispatcher.BeginInvoke(new Action(ShowWindow)); };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open ControlHub", null, (s, e) => Win.Dispatcher.BeginInvoke(new Action(ShowWindow)));
            menu.Items.Add(new Forms.ToolStripSeparator());
            foreach (var m in Modes.All)
            {
                var mode = m;
                menu.Items.Add(mode.Name, null, (s, e) => Win.Dispatcher.BeginInvoke(new Action(() => Win.RequestMode(mode.Value))));
            }
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Win.Dispatcher.BeginInvoke(new Action(Quit)));
            Tray.ContextMenuStrip = menu;
        }

        public static Drawing.Icon MakeIcon(Color a, Color b)
        {
            using (var bmp = new Drawing.Bitmap(32, 32))
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);
                var rect = new Drawing.Rectangle(2, 2, 28, 28);
                using (var path = new Drawing.Drawing2D.GraphicsPath())
                {
                    int r = 10;
                    path.AddArc(rect.X, rect.Y, r, r, 180, 90); path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
                    path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90); path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
                    path.CloseFigure();
                    using (var br = new Drawing.SolidBrush(Drawing.Color.FromArgb(a.R, a.G, a.B)))
                        g.FillPath(br, path);
                }
                using (var pen = new Drawing.Pen(Drawing.Color.White, 3f))
                {
                    pen.StartCap = Drawing.Drawing2D.LineCap.Round; pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                    g.DrawArc(pen, 9, 9, 14, 14, 135, 270);
                    g.DrawLine(pen, 16, 16, 21, 11);
                }
                return Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ---------------------------------------------------------------- IPC
        static int Client(string cmd)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    c.Connect(3000);
                    var w = new StreamWriter(c) { AutoFlush = true };
                    var r = new StreamReader(c);
                    w.WriteLine(cmd);
                    string resp = r.ReadToEnd();
                    Console.WriteLine(resp);
                }
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("ipc error: " + ex.Message); return 1; }
        }

        static void StartIpcServer()
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte))
                        {
                            s.WaitForConnection();
                            var r = new StreamReader(s);
                            var w = new StreamWriter(s) { AutoFlush = true };
                            string cmd = r.ReadLine() ?? "";
                            string resp = "";
                            Win.Dispatcher.Invoke(new Action(() => { resp = Handle(cmd); }));
                            w.Write(resp);
                            s.WaitForPipeDrain();
                        }
                    }
                    catch (Exception ex) { Log.Write("ipc: " + ex.Message); Thread.Sleep(200); }
                }
            }) { IsBackground = true, Name = "ipc" };
            t.Start();
        }

        static string Handle(string cmd)
        {
            var parts = cmd.Trim().Split(new[] { ' ' }, 2);
            string verb = parts[0].ToLowerInvariant(), arg = parts.Length > 1 ? parts[1].Trim() : "";
            switch (verb)
            {
                case "show": ShowWindow(); return "ok";
                case "hide": Win.Hide(); return "ok";
                case "quit": Win.Dispatcher.BeginInvoke(new Action(Quit)); return "ok";
                case "page": Win.Navigate(arg); return "ok";
                case "shot": return Win.Shot(arg);
                case "dry": Dchu.DryRun = arg != "off"; return "dry=" + Dchu.DryRun;
                case "toast": Win.Island.Show("", arg, null); return "ok";
                case "display": return "current=" + Display.Current() + " rates=" + string.Join(",", Display.Rates()) + " always=" + RefreshPolicy.Always;
                case "scroll": Win.ScrollTo(double.Parse(arg, K.EN)); return "ok";
                case "confirm": Win.Confirm("Discard changes?", "The fan curve you edited hasn't been saved. Switching profile will discard it.", "Discard", ok => Log.Write("confirm test: " + ok)); return "ok";
                case "key": { var k = (System.Windows.Input.Key)Enum.Parse(typeof(System.Windows.Input.Key), arg); Win.RaiseKey(k); return "ok"; }
                case "palette":
                    {
                        var p = Theme.Palettes.Find(x => x.Key == arg);
                        if (p == null) return "unknown palette";
                        Theme.Apply(p, true); return "ok";
                    }
                case "anim": K.AnimScale = double.Parse(arg, K.EN); return "ok";
                case "size":
                    {
                        var wh = arg.Split('x');
                        Win.Width = double.Parse(wh[0], K.EN); Win.Height = double.Parse(wh[1], K.EN); return "ok";
                    }
                case "mode": Win.RequestMode(int.Parse(arg)); return "ok";
                case "fan": return Win.FanPage.Ipc(arg);
                case "charge": return Win.Batt.Ipc(arg);
                case "pref": return Win.Pref.Ipc(arg);
                case "kb":
                    {
                        var a = arg.Split(' ');
                        if (a[0] == "level") Hw.KbSetLevel(int.Parse(a[1]));
                        else if (a[0] == "fine") Hw.KbSetFine(double.Parse(a[1], K.EN), true);
                        else if (a[0] == "fx") { KbFx.Set((KbFx.Kind)int.Parse(a[1])); return "fx=" + KbFx.Current; }
                        else if (a[0] == "color") { var c = Theme.Hex(a[1]); Hw.KbSetColor(c.R, c.G, c.B); }
                        else if (a[0] == "sleep") Hw.KbSetSleep(a[1] == "on", a.Length > 2 ? int.Parse(a[2]) : Hw.Kb.SleepSec);
                        else if (a[0] == "refresh") Hw.RefreshKb();
                        else return "kb level <0-4> | color #rrggbb | sleep on|off [sec] | refresh";
                        return "ok";
                    }
                case "state":
                    {
                        var s = Hw.Last; var k = Hw.Kb;
                        return string.Format(K.EN, "ok={0} cpu={1} gpu={2} fan1={3}rpm/{4}% fan2={5}rpm/{6}% mode={7} load={8:0} ram={9:0.0}/{10:0.0}GB batt={11} ac={12} | kb type={13} on={14} level={15} rgb={16},{17},{18} mode={19} sleep={20}/{21}s breathe={22} cycle={23} | dchu={24} dry={25}",
                            s.Ok, s.CpuTemp, s.GpuTemp, s.Fans[0].Rpm, s.Fans[0].Duty, s.Fans[1].Rpm, s.Fans[1].Duty, s.Mode, s.CpuLoad, s.RamUsed / 1073741824.0, s.RamTotal / 1073741824.0, s.Battery, s.OnAc,
                            k.Type, k.On, k.Level, k.R, k.G, k.B, k.Mode, k.SleepOn, k.SleepSec, k.CanBreathe, k.CanCycle, Dchu.Available, Dchu.DryRun);
                    }
            }
            return "unknown command: " + verb;
        }
    }
}
