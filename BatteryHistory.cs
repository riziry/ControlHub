// Battery health and history from Windows' own battery log ("powercfg /batteryreport /xml",
// readable without admin, ~0.2 s). Windows records the charge level, charger and sleep periods and
// a daily full-charge capacity, so the history is there even for time ControlHub wasn't running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;

namespace ControlHub
{
    class TimeSpanRange { public DateTime A, B; public TimeSpanRange(DateTime a, DateTime b) { A = a; B = b; } }
    class TimePoint { public DateTime T; public double V; public TimePoint(DateTime t, double v) { T = t; V = v; } }

    class DayUse
    {
        public DateTime Day; public TimeSpan OnBattery, OnCharger; public double BatteryWh, FullWh, DesignWh;
    }

    class BattReport
    {
        public bool Ok; public string Error = ""; public DateTime When;
        public double DesignWh, FullWh; public int Cycles; public string Maker = "", Chemistry = "";
        public readonly List<TimePoint> Level = new List<TimePoint>();       // charge %, over time
        public readonly List<TimeSpanRange> Ac = new List<TimeSpanRange>(), Sleep = new List<TimeSpanRange>();
        public readonly List<DayUse> Days = new List<DayUse>();
        public double HealthPct { get { return DesignWh > 0 && FullWh > 0 ? Math.Min(100, FullWh / DesignWh * 100) : -1; } }

        static BattReport cache; static bool loading;
        public static BattReport Cached { get { return cache; } }

        // Refreshes in the background when older than maxAgeSec; done runs on the worker thread.
        public static void Load(int maxAgeSec, Action<BattReport> done)
        {
            if (cache != null && (DateTime.Now - cache.When).TotalSeconds < maxAgeSec) { done(cache); return; }
            if (loading) return;
            loading = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                BattReport r;
                try { r = Read(); } catch (Exception ex) { r = new BattReport { Error = ex.Message, When = DateTime.Now }; Log.Write("battery report: " + ex.Message); }
                if (r.Ok || cache == null) cache = r;
                loading = false;
                done(cache);
            });
        }

        static BattReport Read()
        {
            var r = new BattReport { When = DateTime.Now };
            string path = Path.Combine(AppDir.Data, "battery-report.xml");
            Directory.CreateDirectory(AppDir.Data);
            var psi = new ProcessStartInfo("powercfg.exe", "/batteryreport /xml /output \"" + path + "\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } r.Error = "powercfg timed out"; return r; }
            }
            var doc = new XmlDocument();
            doc.Load(path);
            var inv = CultureInfo.InvariantCulture;

            foreach (XmlElement b in doc.GetElementsByTagName("Battery"))
            {
                r.DesignWh = Num(b, "DesignCapacity") / 1000; r.FullWh = Num(b, "FullChargeCapacity") / 1000;
                r.Cycles = (int)Num(b, "CycleCount"); r.Maker = b.GetAttribute("Manufacturer"); r.Chemistry = b.GetAttribute("Chemistry");
                foreach (XmlNode c in b.ChildNodes)
                {
                    if (c.LocalName == "DesignCapacity") r.DesignWh = D(c.InnerText) / 1000;
                    else if (c.LocalName == "FullChargeCapacity") r.FullWh = D(c.InnerText) / 1000;
                    else if (c.LocalName == "CycleCount") r.Cycles = (int)D(c.InnerText);
                    else if (c.LocalName == "Manufacturer") r.Maker = c.InnerText;
                    else if (c.LocalName == "Chemistry") r.Chemistry = c.InnerText;
                }
                break;
            }

            foreach (XmlElement h in doc.GetElementsByTagName("HistoryEntry"))
            {
                var d = new DayUse();
                d.Day = DateTime.Parse(h.GetAttribute("LocalStartDate"), inv);
                d.OnBattery = Dur(h.GetAttribute("ActiveDcTime")); d.OnCharger = Dur(h.GetAttribute("ActiveAcTime"));
                d.BatteryWh = Num(h, "ActiveDcEnergy") / 1000; d.FullWh = Num(h, "FullChargeCapacity") / 1000; d.DesignWh = Num(h, "DesignCapacity") / 1000;
                r.Days.Add(d);
            }

            var pts = new List<TimePoint>();
            foreach (XmlElement u in doc.GetElementsByTagName("UsageEntry"))
            {
                DateTime t = DateTime.Parse(u.GetAttribute("LocalTimestamp"), inv);
                double cap = Num(u, "ChargeCapacity"), full = Num(u, "FullChargeCapacity");
                if (full > 0) pts.Add(new TimePoint(t, Math.Min(100, cap / full * 100)));
                string type = u.GetAttribute("EntryType");
                if (type == "ReportGenerated") continue;
                ulong dur; if (!ulong.TryParse(u.GetAttribute("Duration"), out dur) || dur > 10UL * 365 * 864000000000UL) continue;
                var end = t.AddTicks((long)dur);
                if (type != "Active") r.Sleep.Add(new TimeSpanRange(t, end));
                if (u.GetAttribute("Ac") == "1") r.Ac.Add(new TimeSpanRange(t, end));
            }
            foreach (XmlElement d in doc.GetElementsByTagName("Drain"))
            {
                double f0 = Num(d, "StartFullChargeCapacity"), f1 = Num(d, "EndFullChargeCapacity");
                if (f0 > 0) pts.Add(new TimePoint(DateTime.Parse(d.GetAttribute("LocalStartTimestamp"), inv), Math.Min(100, Num(d, "StartChargeCapacity") / f0 * 100)));
                if (f1 > 0) pts.Add(new TimePoint(DateTime.Parse(d.GetAttribute("LocalEndTimestamp"), inv), Math.Min(100, Num(d, "EndChargeCapacity") / f1 * 100)));
            }
            pts.Sort((a, b) => a.T.CompareTo(b.T));
            foreach (var p in pts)
            {
                if (r.Level.Count > 0 && Math.Abs((p.T - r.Level[r.Level.Count - 1].T).TotalSeconds) < 2) r.Level[r.Level.Count - 1] = p;
                else r.Level.Add(p);
            }
            r.Ok = r.DesignWh > 0 || r.Level.Count > 0;
            if (!r.Ok) r.Error = "Windows has no battery records yet";
            return r;
        }

        static double Num(XmlElement e, string a) { return D(e.GetAttribute(a)); }
        static double D(string s) { double v; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }
        static TimeSpan Dur(string s) { try { return string.IsNullOrEmpty(s) ? TimeSpan.Zero : XmlConvert.ToTimeSpan(s); } catch { return TimeSpan.Zero; } }
    }

    // ControlHub's own record: battery % once a minute while it runs (also from the tray), so the
    // chart has a real curve between the moments Windows logs (Windows only logs power-state changes).
    static class BattLog
    {
        static DateTime last; static bool pruned;
        static string FilePath { get { return Path.Combine(AppDir.Data, "battery-log.csv"); } }

        public static void Observe(Snapshot s)   // worker thread
        {
            if (s.Battery < 0 || (s.Time - last).TotalSeconds < 60) return;
            last = s.Time;
            try
            {
                Directory.CreateDirectory(AppDir.Data);
                if (!pruned) { pruned = true; Prune(); }
                File.AppendAllText(FilePath, s.Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "," + s.Battery + "," + (s.OnAc ? 1 : 0) + "\n");
            }
            catch (Exception ex) { Log.Write("battery log: " + ex.Message); }
        }

        static void Prune()
        {
            if (!File.Exists(FilePath)) return;
            var keep = new List<string>(); var cut = DateTime.Now.AddDays(-8);
            foreach (var l in File.ReadAllLines(FilePath))
            {
                DateTime t;
                if (l.Length > 19 && DateTime.TryParse(l.Substring(0, 19), CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && t >= cut) keep.Add(l);
            }
            File.WriteAllLines(FilePath, keep.ToArray());
        }

        public static List<TimePoint> Read()
        {
            var r = new List<TimePoint>();
            try
            {
                if (!File.Exists(FilePath)) return r;
                foreach (var l in File.ReadAllLines(FilePath))
                {
                    var p = l.Split(','); DateTime t; int v;
                    if (p.Length >= 2 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && int.TryParse(p[1], out v)) r.Add(new TimePoint(t, v));
                }
            }
            catch { }
            return r;
        }

        // Windows' points plus ours; where ours exist (within 3 min) they win.
        public static List<TimePoint> Merge(List<TimePoint> windows, List<TimePoint> own)
        {
            var res = new List<TimePoint>(own);
            int j = 0;
            foreach (var w in windows)
            {
                while (j < own.Count && own[j].T < w.T.AddMinutes(-3)) j++;
                bool near = j < own.Count && Math.Abs((own[j].T - w.T).TotalMinutes) <= 3;
                if (!near) res.Add(w);
            }
            res.Sort((a, b) => a.T.CompareTo(b.T));
            return res;
        }
    }

    // Line chart over real time: shaded charger / sleep periods, hover read-out. Drawn in OnRender
    // (one visual, repainted only when data, size, theme or the hover position changes).
    class TimeChart : FrameworkElement
    {
        public List<TimePoint> Points = new List<TimePoint>();
        public List<TimeSpanRange> Bands = new List<TimeSpanRange>(), Gaps = new List<TimeSpanRange>();
        public DateTime X0, X1;
        public double YMin = 0, YMax = 100; public bool AutoY;
        public Func<double, string> Fmt = v => v.ToString("0", K.EN) + "%";
        public Func<DateTime, string> Describe;           // extra hover text for a moment
        public bool DayTicks, ShowDots; public string BandName = "", GapName = "";
        public Func<Color> LineColor = () => Theme.Current.Accent;
        double hoverX = -1;
        const double L = 38, R = 10, T = 10, B = 24;

        public TimeChart()
        {
            ClipToBounds = true; Cursor = Cursors.Cross;
            MouseMove += (s, e) => { hoverX = e.GetPosition(this).X; InvalidateVisual(); };
            MouseLeave += delegate { hoverX = -1; InvalidateVisual(); };
            Theme.Changed += InvalidateVisual;
        }

        static readonly Typeface face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
#pragma warning disable 618
        FormattedText Txt(string s, double size, Color c) { return new FormattedText(s, K.EN, FlowDirection.LeftToRight, face, size, K.Solid(c)); }
#pragma warning restore 618

        double X(DateTime t) { double w = ActualWidth - L - R; return L + w * (t - X0).TotalSeconds / Math.Max(1, (X1 - X0).TotalSeconds); }
        double Y(double v, double lo, double hi) { double h = ActualHeight - T - B; return T + h * (1 - (v - lo) / Math.Max(1e-9, hi - lo)); }

        void Range(out double lo, out double hi)
        {
            lo = YMin; hi = YMax;
            if (!AutoY || Points.Count == 0) return;
            double mn = double.MaxValue, mx = double.MinValue;
            foreach (var p in Points) { mn = Math.Min(mn, p.V); mx = Math.Max(mx, p.V); }
            lo = Math.Floor((mn - 3) / 5) * 5; hi = Math.Min(YMax, Math.Ceiling((mx + 3) / 5) * 5);
            if (hi - lo < 10) lo = hi - 10;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 60 || h < 40) return;
            var p = Theme.Current; Color text = p.Text;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-testable everywhere
            double lo, hi; Range(out lo, out hi);
            var plot = new Rect(L, T, Math.Max(1, w - L - R), Math.Max(1, h - T - B));

            // bands (charger) and gaps (sleep)
            foreach (var b in Bands) Band(dc, b, plot, Theme.A(p.Green, 0.10));
            foreach (var g in Gaps) Band(dc, g, plot, Theme.A(text, 0.05));

            // horizontal grid + labels
            var gridPen = new Pen(K.Solid(Theme.A(text, 0.07)), 1); gridPen.Freeze();
            int steps = 4;
            for (int i = 0; i <= steps; i++)
            {
                double v = lo + (hi - lo) * i / steps, y = Math.Round(Y(v, lo, hi)) + 0.5;
                dc.DrawLine(gridPen, new Point(L, y), new Point(w - R, y));
                var ft = Txt(Fmt(v), 10.5, Theme.A(text, 0.38));
                dc.DrawText(ft, new Point(L - 8 - ft.Width, y - ft.Height / 2));
            }
            // time ticks
            double span = (X1 - X0).TotalHours;
            if (span > 0)
            {
                var ticks = new List<DateTime>();
                if (DayTicks || span > 50)
                {
                    int every = span > 24 * 20 ? 7 : span > 24 * 9 ? 2 : 1;
                    for (var d = X0.Date.AddDays(1); d < X1; d = d.AddDays(every)) ticks.Add(d);
                }
                else
                {
                    int stepH = span <= 13 ? 2 : span <= 26 ? 4 : 12;
                    var d = X0.Date.AddHours(Math.Ceiling(X0.Hour / (double)stepH) * stepH);
                    for (; d < X1; d = d.AddHours(stepH)) if (d > X0) ticks.Add(d);
                }
                double lastRight = -1;
                foreach (var t in ticks)
                {
                    double x = Math.Round(X(t)) + 0.5;
                    dc.DrawLine(gridPen, new Point(x, T), new Point(x, h - B));
                    string s = DayTicks || span > 50 ? t.ToString("ddd d", K.EN) : t.Hour == 0 ? t.ToString("ddd", K.EN) : t.ToString("HH:mm", K.EN);
                    var ft = Txt(s, 10.5, Theme.A(text, 0.38));
                    double lx = Math.Max(L, Math.Min(w - R - ft.Width, x - ft.Width / 2));
                    if (lx < lastRight + 6) continue;
                    dc.DrawText(ft, new Point(lx, h - B + 6)); lastRight = lx + ft.Width;
                }
            }

            // line + area
            var pts = new List<Point>();
            for (int i = 0; i < Points.Count; i++)
            {
                var q = Points[i];
                if (q.T < X0 && (i + 1 >= Points.Count || Points[i + 1].T < X0)) continue;
                if (q.T > X1 && i > 0 && Points[i - 1].T > X1) break;
                pts.Add(new Point(X(q.T), Y(q.V, lo, hi)));
            }
            Color lc = LineColor();
            dc.PushClip(new RectangleGeometry(plot));
            if (pts.Count >= 2)
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(pts[0].X, plot.Bottom), true, true);
                    foreach (var q in pts) ctx.LineTo(q, true, false);
                    ctx.LineTo(new Point(pts[pts.Count - 1].X, plot.Bottom), true, false);
                }
                area.Freeze();
                var fill = new LinearGradientBrush(Theme.A(lc, 0.28), Theme.A(lc, 0.02), 90); fill.Freeze();
                dc.DrawGeometry(fill, null, area);
                var line = new StreamGeometry();
                using (var ctx = line.Open())
                {
                    ctx.BeginFigure(pts[0], false, false);
                    for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, true);
                }
                line.Freeze();
                var pen = new Pen(K.Solid(lc), 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; pen.Freeze();
                dc.DrawGeometry(null, pen, line);
            }
            if (ShowDots) foreach (var q in pts) dc.DrawEllipse(K.Solid(p.Surface), new Pen(K.Solid(lc), 2), q, 3.2, 3.2);
            else if (pts.Count > 0) dc.DrawEllipse(K.Solid(lc), null, pts[pts.Count - 1], 4, 4);
            dc.Pop();

            // hover read-out
            if (hoverX >= L && hoverX <= w - R && Points.Count > 0)
            {
                var t = X0.AddSeconds((hoverX - L) / plot.Width * (X1 - X0).TotalSeconds);
                double v; DateTime at;
                if (!ValueAt(t, out v, out at)) return;
                double x = ShowDots ? X(at) : hoverX, y = Y(v, lo, hi);
                var hp = new Pen(K.Solid(Theme.A(text, 0.35)), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
                dc.DrawLine(hp, new Point(x, T), new Point(x, h - B));
                dc.DrawEllipse(K.Solid(lc), new Pen(K.Solid(p.Surface), 2), new Point(x, y), 5, 5);
                var when = ShowDots ? at : t;
                string head = (DayTicks ? when.ToString("ddd d MMM", K.EN) : when.ToString("ddd HH:mm", K.EN)) + "   " + Fmt(v);
                string extra = Describe != null ? Describe(when) : "";
                var f1 = Txt(head, 12, text); var f2 = extra.Length > 0 ? Txt(extra, 11, Theme.A(text, 0.6)) : null;
                double bw = Math.Max(f1.Width, f2 != null ? f2.Width : 0) + 20, bh = f1.Height + (f2 != null ? f2.Height + 2 : 0) + 12;
                double bx = x + 12 + bw > w - R ? x - 12 - bw : x + 12, by = Math.Max(T, Math.Min(h - B - bh, y - bh - 10));
                dc.DrawRoundedRectangle(K.Solid(p.Raised), new Pen(K.Solid(Theme.A(text, 0.10)), 1), new Rect(bx, by, bw, bh), 9, 9);
                dc.DrawText(f1, new Point(bx + 10, by + 6));
                if (f2 != null) dc.DrawText(f2, new Point(bx + 10, by + 8 + f1.Height));
            }
        }

        void Band(DrawingContext dc, TimeSpanRange b, Rect plot, Color c)
        {
            if (b.B < X0 || b.A > X1) return;
            double a = Math.Max(plot.Left, X(b.A)), z = Math.Min(plot.Right, X(b.B));
            if (z - a < 0.5) return;
            dc.DrawRectangle(K.Solid(c), null, new Rect(a, plot.Top, z - a, plot.Height));
        }

        // Interpolated value at t (or the nearest point when dots are shown).
        bool ValueAt(DateTime t, out double v, out DateTime at)
        {
            v = 0; at = t;
            if (Points.Count == 0) return false;
            if (ShowDots)
            {
                double best = double.MaxValue;
                foreach (var q in Points) { double d = Math.Abs((q.T - t).TotalSeconds); if (d < best && q.T >= X0 && q.T <= X1) { best = d; v = q.V; at = q.T; } }
                return best < double.MaxValue;
            }
            if (t <= Points[0].T) { if ((Points[0].T - t).TotalMinutes > 30) return false; v = Points[0].V; return true; }
            for (int i = 1; i < Points.Count; i++)
                if (Points[i].T >= t)
                {
                    var a = Points[i - 1]; var b = Points[i];
                    double k = (t - a.T).TotalSeconds / Math.Max(1, (b.T - a.T).TotalSeconds);
                    v = a.V + (b.V - a.V) * k; return true;
                }
            v = Points[Points.Count - 1].V; return true;
        }
    }
}
