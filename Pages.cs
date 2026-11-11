// Pages: Overview (dashboard), Power Mode, Keyboard, Settings.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ControlHub
{
    static class UI
    {
        public static ScrollViewer Scroll(UIElement child)
        {
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = child, Focusable = false };
            SmoothWheel(sv);
            return sv;
        }

        // Eased wheel scrolling.
        public static void SmoothWheel(ScrollViewer sv)
        {
            var sp = new Spring(0, 0.4); bool live = false;
            sp.Tune(170, 1.0);
            sv.PreviewMouseWheel += (s, e) =>
            {
                e.Handled = true;
                if (!live) sp.Snap(sv.VerticalOffset);
                sp.Target = Math.Max(0, Math.Min(sv.ScrollableHeight, sp.Target - e.Delta * 0.9));
                if (K.AnimScale <= 0) { sv.ScrollToVerticalOffset(sp.Target); return; }
                if (!live)
                {
                    live = true;
                    Frame.Add(dt => { sp.Step(dt); sv.ScrollToVerticalOffset(sp.Value); if (sp.Settle()) { live = false; return false; } return true; });
                }
            };
        }

        public static StackPanel Header(string glyph, string text, Brush tint)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(10), Background = tint == null ? (Brush)Theme.AccentSoft : tint };
            dot.Child = K.Glyph(glyph, 14, Theme.Text);
            sp.Children.Add(dot);
            var t = K.TB(text, 14.5, Theme.Text, FontWeights.SemiBold); t.Margin = new Thickness(12, 0, 0, 0);
            sp.Children.Add(t);
            return sp;
        }

        public static Grid Columns(params double[] stars)
        {
            var g = new Grid();
            for (int i = 0; i < stars.Length; i++)
            {
                if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stars[i], GridUnitType.Star) });
            }
            return g;
        }
        public static void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col * 2); g.Children.Add(e); }

        // Thin bar whose fill glides to its value. The fill lives in a Canvas, so animating its width
        // never re-measures the page (a Grid parent passed every frame's size change up the tree), and
        // the glide is capped at 60 fps: these bars update every second on the live pages.
        public class Bar : Grid
        {
            readonly Border fill; readonly Canvas host = new Canvas { ClipToBounds = true }; double v;
            public Bar(double h, Brush fg)
            {
                Height = h;
                Children.Add(new Border { CornerRadius = new CornerRadius(h / 2), Background = Theme.Raised });
                fill = new Border { CornerRadius = new CornerRadius(h / 2), Background = fg, Height = h, Width = 0 };
                host.Children.Add(fill);
                Children.Add(host);
                SizeChanged += delegate { fill.BeginAnimation(WidthProperty, null); fill.Width = Target(); };
            }
            double Target() { return v <= 0 ? 0 : Math.Max(Height, ActualWidth * v); }
            public void SetFill(Brush b) { fill.Background = b; }
            public void Set(double t)
            {
                v = Math.Max(0, Math.Min(1, t));
                if (ActualWidth <= 0) return;
                double to = Target();
                if (Math.Abs(to - fill.ActualWidth) < 1 && fill.HasAnimatedProperties == false) { fill.Width = to; return; }
                fill.BeginAnimation(WidthProperty, K.Anim(to, 700, K.OutCubic));
            }
        }

        public static Border Chip(string text, Brush bg, Brush fg)
        {
            var t = K.TB(text, 11.5, fg, FontWeights.SemiBold);
            return new Border { CornerRadius = new CornerRadius(9), Background = bg, Padding = new Thickness(9, 3, 9, 4), Child = t, VerticalAlignment = VerticalAlignment.Center };
        }

        public static Grid Row(UIElement left, UIElement right)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(left);
            Grid.SetColumn(right, 1); g.Children.Add(right);
            return g;
        }

        public static StackPanel Label2(string a, string b)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(K.TB(a, 14, Theme.Text, FontWeights.SemiBold));
            if (b != null) { var t = K.TB(b, 12.5, Theme.Text3, FontWeights.Medium); t.Margin = new Thickness(0, 3, 0, 0); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; sp.Children.Add(t); }
            return sp;
        }

        public static Border Divider() { return new Border { Height = 1, Background = Theme.Hairline, Margin = new Thickness(0, 16, 0, 16) }; }

        // The EC reports a fixed 20° while the discrete GPU is powered down.
        public static bool GpuAsleep(Snapshot s) { return s.GpuAsleep; }
    }

    // ===================================================================== Overview
    // Top to bottom: health banner + quick actions, gauges + power mode, fans, history charts, memory/battery.
    // Every card that has a detail page opens it when clicked.
    class DashboardPage : IPage
    {
        readonly MainWindow w;
        readonly StackPanel stack = new StackPanel { Margin = new Thickness(36, 0, 36, 36) };
        readonly ScrollViewer view;
        // banner
        readonly Border healthIcon; readonly TextBlock healthGlyph, healthTitle, healthSub;
        // gauges + mode
        readonly Gauge cpuG = new Gauge(164, 12), gpuG = new Gauge(164, 12);
        readonly SpringNumber cpuT, gpuT;
        readonly TextBlock cpuSub, gpuSub, modeName, modeDesc;
        readonly TextBlock[] cpuStat = new TextBlock[3], gpuStat = new TextBlock[3];   // clock, load, power
        readonly Border modeIcon; readonly TextBlock modeGlyph;
        readonly Segmented modeSeg;
        // fans
        readonly FanRotor[] rotors = new FanRotor[2];
        readonly SpringNumber[] rpm = new SpringNumber[2];
        readonly TextBlock[] duty = new TextBlock[2], fanState = new TextBlock[2];
        readonly Border[] fanChip = new Border[2];
        readonly UI.Bar[] dutyBar = new UI.Bar[2];
        // charts
        readonly LiveChart tempChart = new LiveChart(120, 20, 100), fanChart = new LiveChart(120, 0, 6000), powerChart = new LiveChart(120, 0, 40);
        readonly TextBlock[] tempNow = new TextBlock[2], fanNow = new TextBlock[2]; readonly TextBlock powerNow;
        // memory / battery
        readonly TextBlock ramText, battText, battSub, ramSub, battLimit;
        readonly UI.Bar ramBar, battBar;
        BatteryInfo binfo = new BatteryInfo(); DateTime binfoAt;
        int lastMode = -2; bool suppress;

        public string Title { get { return "Overview"; } }
        public string Subtitle { get { return "Live temperatures, fans and power at a glance"; } }
        public FrameworkElement View { get { return view; } }
        public Panel StaggerRoot { get { return stack; } }

        public DashboardPage(MainWindow w)
        {
            this.w = w;
            view = UI.Scroll(stack);

            // ---- 0. health banner
            var hb = new Grid();
            hb.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hb.ColumnDefinitions.Add(new ColumnDefinition());
            hb.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            healthIcon = new Border { Width = 50, Height = 50, CornerRadius = new CornerRadius(25), Background = Theme.GreenSoft, VerticalAlignment = VerticalAlignment.Center };
            healthGlyph = K.Glyph("", 22, Theme.Green); healthIcon.Child = healthGlyph;
            hb.Children.Add(healthIcon);
            var ht = new StackPanel { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            healthTitle = K.TB("Reading sensors…", 19, Theme.Text, FontWeights.Bold); healthTitle.FontFamily = K.Display;
            healthSub = K.TB("", 12.5, Theme.Text2, FontWeights.Medium); healthSub.Margin = new Thickness(0, 3, 0, 0); healthSub.TextWrapping = TextWrapping.Wrap; healthSub.TextTrimming = TextTrimming.None;
            ht.Children.Add(healthTitle); ht.Children.Add(healthSub);
            Grid.SetColumn(ht, 1); hb.Children.Add(ht);
            stack.Children.Add(K.Card(hb, new Thickness(20, 16, 16, 16)));

            // ---- 1. CPU, GPU, mode
            var r1 = UI.Columns(1, 1, 1.6);
            r1.Height = 340; r1.Margin = new Thickness(0, 18, 0, 0);
            cpuT = new SpringNumber(40, Theme.Text, FontWeights.Bold, "0", "°"); cpuT.FontFamily = K.Display;
            gpuT = new SpringNumber(40, Theme.Text, FontWeights.Bold, "0", "°"); gpuT.FontFamily = K.Display;
            cpuSub = K.TB("", 12, Theme.Text3, FontWeights.SemiBold); gpuSub = K.TB("", 12, Theme.Text3, FontWeights.SemiBold);
            UI.Put(r1, GaugeCard("", "Processor", cpuG, cpuT, cpuSub, cpuStat), 0);
            UI.Put(r1, GaugeCard("", "Graphics", gpuG, gpuT, gpuSub, gpuStat), 1);

            var mc = new Grid();
            mc.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mc.RowDefinitions.Add(new RowDefinition());
            mc.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mc.Children.Add(UI.Header("", "Power mode", null));
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var mrow = new StackPanel { Orientation = Orientation.Horizontal };
            modeIcon = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(18), Background = Theme.AccentSoft };
            modeGlyph = K.Glyph("", 24, Theme.Accent); modeIcon.Child = modeGlyph;
            mrow.Children.Add(modeIcon);
            var mtx = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            modeName = K.TB("–", 24, Theme.Text, FontWeights.Bold); modeName.FontFamily = K.Display;
            mtx.Children.Add(K.TB("Active now", 12, Theme.Text3, FontWeights.Medium));
            mtx.Children.Add(modeName);
            mrow.Children.Add(mtx);
            mid.Children.Add(mrow);
            modeDesc = K.TB("", 12.5, Theme.Text2, FontWeights.Normal); modeDesc.TextWrapping = TextWrapping.Wrap; modeDesc.TextTrimming = TextTrimming.None; modeDesc.Margin = new Thickness(0, 14, 0, 0);
            mid.Children.Add(modeDesc);
            Grid.SetRow(mid, 1); mc.Children.Add(mid);
            var glyphs = new string[Modes.All.Length]; var names = new string[Modes.All.Length];
            for (int i = 0; i < names.Length; i++) { glyphs[i] = Modes.All[i].Glyph; names[i] = Modes.All[i].Name; }
            modeSeg = new Segmented(glyphs, 0, 42);
            modeSeg.UseIcons(names);
            modeSeg.Changed += i => { if (!suppress) w.RequestMode(Modes.All[i].Value); };
            Grid.SetRow(modeSeg, 2); mc.Children.Add(modeSeg);
            UI.Put(r1, Clickable(K.Card(mc, new Thickness(22)), "power"), 2);
            stack.Children.Add(r1);

            // ---- 2. fans
            var r2 = UI.Columns(1, 1);
            r2.Margin = new Thickness(0, 18, 0, 0);
            string[] fanNames = { "CPU fan", "GPU fan" };
            for (int i = 0; i < 2; i++) UI.Put(r2, Clickable(FanCard(i, fanNames[i]), "cooling"), i);
            stack.Children.Add(r2);

            // ---- 3. history: temperature, fan speed, CPU power
            var r3 = UI.Columns(1, 1, 1);
            r3.Margin = new Thickness(0, 18, 0, 0);
            tempChart.AddSeries(Theme.Current.Accent, "CPU"); tempChart.AddSeries(Theme.Current.Accent2, "GPU");
            tempChart.Unit = "°";
            fanChart.AddSeries(Theme.Current.Green, "CPU"); fanChart.AddSeries(Theme.Current.Orange, "GPU");
            fanChart.AutoMax = true; fanChart.AutoStep = 500; fanChart.Format = "#,0";
            powerChart.AddSeries(Theme.Current.Yellow, "CPU"); powerChart.AutoMax = true; powerChart.AutoStep = 5; powerChart.Unit = " W";
            Theme.Changed += delegate
            {
                tempChart.SetColor(0, Theme.Current.Accent); tempChart.SetColor(1, Theme.Current.Accent2);
                fanChart.SetColor(0, Theme.Current.Green); fanChart.SetColor(1, Theme.Current.Orange);
                powerChart.SetColor(0, Theme.Current.Yellow);
            };
            UI.Put(r3, ChartCard("", "Temperature", tempChart, new[] { "CPU", "GPU" }, new Brush[] { Theme.Accent, Theme.Accent2 }, tempNow), 0);
            UI.Put(r3, ChartCard("", "Fan speed", fanChart, new[] { "CPU", "GPU" }, new Brush[] { Theme.Green, Theme.Orange }, fanNow), 1);
            var pn = new TextBlock[1];
            UI.Put(r3, ChartCard("", "CPU power", powerChart, new[] { "CPU" }, new Brush[] { Theme.Yellow }, pn), 2);
            powerNow = pn[0];
            stack.Children.Add(r3);
            var cap = K.TB("Last 2 minutes · hover a chart to see earlier values", 12, Theme.Text3, FontWeights.Medium); cap.Margin = new Thickness(4, 10, 0, 0);
            stack.Children.Add(cap);

            // ---- 4. memory + battery
            var r4 = UI.Columns(1, 1);
            r4.Margin = new Thickness(0, 18, 0, 0);
            ramText = K.TB("–", 22, Theme.Text, FontWeights.Bold); ramSub = K.TB("", 12.5, Theme.Text3, FontWeights.Medium);
            ramBar = new UI.Bar(8, Theme.AccentFill);
            UI.Put(r4, StatCard("", "Memory", ramText, ramSub, ramBar, null), 0);
            battText = K.TB("–", 22, Theme.Text, FontWeights.Bold); battSub = K.TB("", 12.5, Theme.Text3, FontWeights.Medium);
            battLimit = K.TB("", 12, Theme.Text3, FontWeights.SemiBold);
            battBar = new UI.Bar(8, Theme.Green);
            UI.Put(r4, Clickable(StatCard("", "Battery", battText, battSub, battBar, battLimit), "battery"), 1);
            stack.Children.Add(r4);

            Hw.KbChanged += k => w.Dispatcher.BeginInvoke(new Action(UpdateQuick));
            Hw.FanChanged += f => w.Dispatcher.BeginInvoke(new Action(() => { UpdateQuick(); UpdateFanChips(); }));
            Hw.ChargeChanged += c => w.Dispatcher.BeginInvoke(new Action(UpdateQuick));
        }

        // ---------------------------------------------------------------- building blocks
        // The whole card opens a page; a small arrow appears on hover so it reads as a link.
        Border Clickable(Border card, string page)
        {
            var arrow = K.Glyph("", 12, Theme.Text3);
            arrow.HorizontalAlignment = HorizontalAlignment.Right; arrow.VerticalAlignment = VerticalAlignment.Top;
            arrow.Margin = new Thickness(0, -6, -8, 0); arrow.Opacity = 0; arrow.IsHitTestVisible = false;
            arrow.RenderTransform = new TranslateTransform(-6, 0);
            var g = new Grid();
            var inner = card.Child; card.Child = null;
            g.Children.Add(inner); g.Children.Add(arrow);
            card.Child = g;
            card.Cursor = Cursors.Hand;
            card.MouseEnter += delegate { arrow.BeginAnimation(UIElement.OpacityProperty, K.Anim(1, 160, K.OutCubic)); ((TranslateTransform)arrow.RenderTransform).BeginAnimation(TranslateTransform.XProperty, K.Anim(0, 260, K.OutBack)); };
            card.MouseLeave += delegate { arrow.BeginAnimation(UIElement.OpacityProperty, K.Anim(0, 200, K.OutCubic)); ((TranslateTransform)arrow.RenderTransform).BeginAnimation(TranslateTransform.XProperty, K.Anim(-6, 200, K.OutCubic)); };
            card.MouseLeftButtonUp += (s, e) => { if (!e.Handled) w.Navigate(page); };
            return card;
        }

        // Gauge with only the temperature (and one short word) inside the ring; clock / load / power
        // sit in a strip underneath so nothing overlaps the arc.
        Border GaugeCard(string glyph, string name, Gauge g, SpringNumber num, TextBlock sub, TextBlock[] stat)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(UI.Header(glyph, name, null));
            var host = new Grid { Width = g.Width, Height = g.Height, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            host.Children.Add(g);
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            num.HorizontalAlignment = HorizontalAlignment.Center;
            center.Children.Add(num);
            sub.HorizontalAlignment = HorizontalAlignment.Center; sub.MaxWidth = g.Width * 0.62; sub.Margin = new Thickness(0, -2, 0, 0);
            center.Children.Add(sub);
            host.Children.Add(center);
            // scale ends of the 270° arc, under the two arc tips
            var lo = K.TB("20°", 10.5, Theme.Text3, FontWeights.SemiBold); lo.HorizontalAlignment = HorizontalAlignment.Left; lo.VerticalAlignment = VerticalAlignment.Bottom; lo.Margin = new Thickness(g.Width * 0.17, 0, 0, -2);
            var hi = K.TB("100°", 10.5, Theme.Text3, FontWeights.SemiBold); hi.HorizontalAlignment = HorizontalAlignment.Right; hi.VerticalAlignment = VerticalAlignment.Bottom; hi.Margin = new Thickness(0, 0, g.Width * 0.13, -2);
            host.Children.Add(lo); host.Children.Add(hi);
            Grid.SetRow(host, 1); grid.Children.Add(host);

            var strip = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            string[] labels = { "MHz", "Load", "Power" };
            for (int i = 0; i < 3; i++)
            {
                strip.ColumnDefinitions.Add(new ColumnDefinition());
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                stat[i] = K.TB("–", 14, Theme.Text, FontWeights.SemiBold); stat[i].HorizontalAlignment = HorizontalAlignment.Center; K.Tabular(stat[i]);
                var l = K.TB(labels[i], 11, Theme.Text3, FontWeights.Medium); l.HorizontalAlignment = HorizontalAlignment.Center; l.Margin = new Thickness(0, 1, 0, 0);
                sp.Children.Add(stat[i]); sp.Children.Add(l);
                Grid.SetColumn(sp, i); strip.Children.Add(sp);
                if (i > 0) { var d = new Border { Width = 1, Background = Theme.Hairline, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) }; Grid.SetColumn(d, i); strip.Children.Add(d); }
            }
            var sb = new Border { CornerRadius = new CornerRadius(14), Background = Theme.Raised, Padding = new Thickness(4, 9, 4, 9), Child = strip, Margin = new Thickness(0, 12, 0, 0) };
            Grid.SetRow(sb, 2); grid.Children.Add(sb);
            return K.Card(grid, new Thickness(20));
        }

        static string Mhz(double v) { return v > 0 ? v.ToString("#,0", K.EN) : "–"; }
        static string TempWord(int t) { return t < 60 ? "Cool" : t < 75 ? "Warm" : t < 88 ? "Hot" : "Very hot"; }

        Border FanCard(int i, string name)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var rotorHost = new Border { Width = 92, Height = 92, CornerRadius = new CornerRadius(46), Background = Theme.Raised };
            rotors[i] = new FanRotor(74);
            rotorHost.Child = rotors[i];
            g.Children.Add(rotorHost);
            var right = new StackPanel { Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(K.TB(name, 14.5, Theme.Text, FontWeights.SemiBold));
            fanChip[i] = UI.Chip("", Theme.AccentSoft, Theme.Accent); fanChip[i].Margin = new Thickness(10, 0, 0, 0);
            title.Children.Add(fanChip[i]);
            fanState[i] = K.TB("", 12, Theme.Text3, FontWeights.Medium);
            right.Children.Add(UI.Row(title, fanState[i]));
            var rp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
            rpm[i] = new SpringNumber(30, Theme.Text, FontWeights.Bold, "#,0", ""); rpm[i].FontFamily = K.Display;
            rp.Children.Add(rpm[i]);
            var unit = K.TB("rpm", 13, Theme.Text3, FontWeights.SemiBold); unit.Margin = new Thickness(6, 10, 0, 0);
            rp.Children.Add(unit);
            right.Children.Add(rp);
            dutyBar[i] = new UI.Bar(6, i == 0 ? (Brush)Theme.Green : Theme.Orange);
            right.Children.Add(dutyBar[i]);
            duty[i] = K.TB("", 12, Theme.Text3, FontWeights.Medium); duty[i].Margin = new Thickness(0, 8, 0, 0);
            right.Children.Add(duty[i]);
            Grid.SetColumn(right, 1); g.Children.Add(right);
            return K.Card(g, new Thickness(22));
        }

        Border ChartCard(string glyph, string name, LiveChart chart, string[] legend, Brush[] colors, TextBlock[] now)
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(140) });
            g.Children.Add(UI.Header(glyph, name, null));
            var leg = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            for (int i = 0; i < legend.Length; i++)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                sp.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = colors[i], Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(K.TB(legend[i] + " ", 12, Theme.Text3, FontWeights.Medium));
                now[i] = K.TB("–", 12.5, Theme.Text, FontWeights.SemiBold); K.Tabular(now[i]);
                sp.Children.Add(now[i]);
                leg.Children.Add(sp);
            }
            Grid.SetRow(leg, 1); g.Children.Add(leg);
            chart.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(chart, 2); g.Children.Add(chart);
            return K.Card(g, new Thickness(20));
        }

        Border StatCard(string glyph, string name, TextBlock big, TextBlock sub, UI.Bar bar, TextBlock extra)
        {
            var sp = new StackPanel();
            sp.Children.Add(UI.Row(UI.Header(glyph, name, null), big));
            bar.Margin = new Thickness(0, 16, 0, 10);
            sp.Children.Add(bar);
            if (extra == null) sp.Children.Add(sub);
            else sp.Children.Add(UI.Row(sub, extra));
            return K.Card(sp, new Thickness(22));
        }

        public void ThemeChanged() { UpdateQuick(); ShowMode(Hw.Last.Mode); }

        void UpdateQuick()
        {
            UpdateBatteryLimit();
        }

        void UpdateFanChips()
        {
            var f = Hw.Fan;
            string t = f.Mode == 1 ? "Max" : f.Mode == 6 ? "Custom" : f.Mode == 0 ? "Auto" : "Mode " + f.Mode;
            for (int i = 0; i < 2; i++) ((TextBlock)fanChip[i].Child).Text = t;
        }

        void UpdateBatteryLimit()
        {
            var c = Hw.Charge;
            battLimit.Text = c.Supported && c.Enabled ? "Limit " + c.Start + "–" + c.Stop + "%" : "";
        }

        // ---------------------------------------------------------------- live data
        public void Optimistic(int mode) { ShowMode(mode); }

        void ShowMode(int mode)
        {
            var m = Modes.Of(mode);
            if (m == null) { modeName.Text = mode < 0 ? "–" : "Mode " + mode; return; }
            var c = m.Col();
            modeName.Text = m.Name; modeDesc.Text = m.Desc;
            modeGlyph.Text = m.Glyph; modeGlyph.Foreground = K.Solid(c);
            modeIcon.Background = K.Solid(Theme.A(c, 0.16));
            suppress = true; modeSeg.Select(Modes.IndexOf(mode), false); suppress = false;
        }

        void Health(Snapshot s)
        {
            double t = s.CpuTemp;
            string title; string glyph; Color c;
            if (t < 60) { title = "Running cool"; glyph = ""; c = Theme.Current.Green; }
            else if (t < 75) { title = "Warm"; glyph = ""; c = Theme.Current.Accent; }
            else if (t < 88) { title = "Running hot"; glyph = ""; c = Theme.Current.Orange; }
            else { title = "Very hot — let it cool down"; glyph = ""; c = Theme.Current.Red; }
            int m0 = Hw.Fan.MaxRpm[0];
            double pct = m0 > 0 ? s.Fans[0].Rpm * 100.0 / m0 : s.Fans[0].Duty;
            string fans = s.Fans[0].Rpm == 0 && s.Fans[1].Rpm == 0 ? "fans off" : pct < 35 ? "fans quiet" : pct < 70 ? "fans audible" : "fans loud";
            var m = Modes.Of(s.Mode);
            healthTitle.Text = title;
            healthSub.Text = string.Format(K.EN, "CPU {0}°C{1} · {2} · GPU {3} · {4}", s.CpuTemp,
                s.CpuWatts >= 0 ? string.Format(K.EN, " at {0:0.0} W", s.CpuWatts) : "", fans,
                UI.GpuAsleep(s) ? "asleep" : s.GpuTemp + "°C", m != null ? m.Name + " mode" : "");
            healthGlyph.Text = glyph; healthGlyph.Foreground = K.Solid(c); healthIcon.Background = K.Solid(Theme.A(c, 0.16));
        }

        public void Update(Snapshot s)
        {
            if (s == null || s.Time == default(DateTime)) return;
            bool vis = view.IsVisible;
            tempChart.Push(0, s.CpuTemp); tempChart.Push(1, UI.GpuAsleep(s) ? 20 : s.GpuTemp);
            fanChart.Push(0, s.Fans[0].Rpm); fanChart.Push(1, s.Fans[1].Rpm);
            powerChart.Push(0, Math.Max(0, s.CpuWatts));
            double sec = Hw.Background ? Hw.BackgroundIntervalMs / 1000.0 : Hw.IntervalMs / 1000.0;
            tempChart.SampleSeconds = fanChart.SampleSeconds = powerChart.SampleSeconds = sec;
            if (!vis) return;
            tempChart.Redraw(); fanChart.Redraw(); powerChart.Redraw();

            Health(s);
            cpuT.Set(s.CpuTemp);
            cpuG.SetValue01((s.CpuTemp - 20) / 80.0);
            cpuG.SetColor(Theme.TempColor(s.CpuTemp));
            cpuSub.Text = TempWord(s.CpuTemp);
            cpuStat[0].Text = Mhz(s.CpuMhz);
            cpuStat[1].Text = s.CpuLoad.ToString("0", K.EN) + "%";
            cpuStat[2].Text = s.CpuWatts >= 0 ? s.CpuWatts.ToString("0.0", K.EN) + " W" : "–";
            bool gpuIdle = UI.GpuAsleep(s);
            if (gpuIdle)
            {
                gpuT.SetText("Off"); gpuG.SetValue01(0); gpuSub.Text = "Asleep";
                gpuStat[0].Text = "–"; gpuStat[1].Text = "–"; gpuStat[2].Text = "0 W";
            }
            else
            {
                gpuT.Set(s.GpuTemp); gpuG.SetValue01((s.GpuTemp - 20) / 80.0); gpuG.SetColor(Theme.TempColor(s.GpuTemp)); gpuSub.Text = TempWord(s.GpuTemp);
                gpuStat[0].Text = Mhz(s.GpuMhz);
                gpuStat[1].Text = s.GpuLoad >= 0 ? s.GpuLoad.ToString("0", K.EN) + "%" : "–";
                gpuStat[2].Text = s.GpuWatts >= 0 ? s.GpuWatts.ToString("0.0", K.EN) + " W" : "–";
            }
            gpuT.Opacity = gpuIdle ? 0.55 : 1;
            var gtip = s.GpuMhz > 0 ? "NVIDIA graphics clock, load and board power" : "Shown while the discrete GPU is awake";
            ((FrameworkElement)gpuStat[0].Parent).ToolTip = gtip;

            tempNow[0].Text = s.CpuTemp + "°"; tempNow[1].Text = gpuIdle ? "asleep" : s.GpuTemp + "°";
            fanNow[0].Text = s.Fans[0].Rpm.ToString("#,0", K.EN); fanNow[1].Text = s.Fans[1].Rpm.ToString("#,0", K.EN);
            powerNow.Text = s.CpuWatts >= 0 ? s.CpuWatts.ToString("0.0", K.EN) + " W" : "n/a";

            for (int i = 0; i < 2; i++)
            {
                var f = s.Fans[i];
                int mx = Hw.Fan.MaxRpm[i];
                double pct = mx > 0 ? Math.Min(100, f.Rpm * 100.0 / mx) : f.Duty;
                rotors[i].SetRpm(f.Rpm);
                rpm[i].Set(f.Rpm);
                dutyBar[i].Set(pct / 100.0);
                duty[i].Text = mx > 0 ? string.Format(K.EN, "{0:0}% of {1:N0} rpm max", pct, mx) : "Duty " + f.Duty + "%";
                fanState[i].Text = f.Rpm == 0 ? "Stopped" : pct > 70 ? "Fast" : pct > 40 ? "Medium" : "Slow";
                rotors[i].SetFill(f.Rpm == 0 ? Theme.Text3 : i == 0 ? (Brush)Theme.Green : Theme.Orange);
            }
            if (s.Mode != lastMode) { lastMode = s.Mode; if (w.PendingMode < 0) ShowMode(s.Mode); }

            if (s.RamTotal > 0)
            {
                ramText.Text = string.Format(K.EN, "{0:0}%", s.RamUsed / s.RamTotal * 100);
                ramSub.Text = K.Bytes(s.RamUsed) + " of " + K.Bytes(s.RamTotal) + " used · " + K.Bytes(s.RamTotal - s.RamUsed) + " free";
                ramBar.Set(s.RamUsed / s.RamTotal);
            }
            if ((DateTime.Now - binfoAt).TotalSeconds >= 5)
            {
                binfoAt = DateTime.Now;
                System.Threading.ThreadPool.QueueUserWorkItem(_ => { var b = BatteryInfo.Read(); w.Dispatcher.BeginInvoke(new Action(() => { binfo = b; })); });
            }
            if (s.Battery >= 0)
            {
                battText.Text = s.Battery + "%";
                string rate = binfo.Ok && binfo.RateW > 0.05 ? string.Format(K.EN, " · {0}{1:0.0} W", binfo.Discharging ? "−" : "+", binfo.RateW) : "";
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                string left = !s.OnAc && ps.BatteryLifeRemaining > 0 ? string.Format(" · {0}h {1:00}m left", ps.BatteryLifeRemaining / 3600, ps.BatteryLifeRemaining / 60 % 60) : "";
                battSub.Text = (s.OnAc ? (binfo.Charging || s.Charging ? "Charging" : "Plugged in") : "On battery") + rate + left;
                battBar.Set(s.Battery / 100.0);
                battBar.SetFill(s.Battery <= 20 && !s.OnAc ? Theme.Red : s.OnAc ? Theme.Green : (Brush)Theme.Accent);
            }
            else { battText.Text = "–"; battSub.Text = "No battery"; }
        }

        public void OnShown()
        {
            lastMode = -2;
            Hw.RefreshCharge(); Hw.RefreshFans();
            UpdateQuick(); UpdateFanChips();
        }
    }

    // Small toggle/shortcut tile used in the Overview banner.
    class QuickTile : Border
    {
        readonly TextBlock icon, state; readonly Border iconBox;
        public QuickTile(string glyph, string label, Action click)
        {
            Width = 116; Margin = new Thickness(8, 0, 0, 0); CornerRadius = new CornerRadius(16); Background = Theme.Raised; Padding = new Thickness(12, 10, 12, 10);
            BorderBrush = Theme.Hairline; BorderThickness = new Thickness(1.2); Cursor = Cursors.Hand;
            var sp = new StackPanel();
            iconBox = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left };
            icon = K.Glyph(glyph, 14, Theme.Text2); iconBox.Child = icon;
            sp.Children.Add(iconBox);
            var l = K.TB(label, 12, Theme.Text, FontWeights.SemiBold); l.Margin = new Thickness(0, 8, 0, 0);
            state = K.TB("–", 11.5, Theme.Text3, FontWeights.Medium);
            sp.Children.Add(l); sp.Children.Add(state);
            Child = sp;
            K.Pressable(this, click, true);
            Set(false, "–");
        }
        public void Set(bool on, string text)
        {
            state.Text = text;
            state.Foreground = on ? Theme.Accent : Theme.Text3;
            icon.Foreground = on ? Theme.Accent : Theme.Text2;
            iconBox.Background = on ? Theme.AccentSoft : K.Solid(Theme.A(Theme.Current.Text, 0.05));
            BorderBrush = on ? K.Solid(Theme.A(Theme.Current.Accent, 0.5)) : Theme.Hairline;
        }
    }

    // ===================================================================== Power Mode
    // Only properties of the modes themselves are shown (never usage statistics, which depend on load
    // and on the user's fan setting): the BIOS automatic fan table, the user's fan setting per mode,
    // and what the OEM FnKey service is known (from its code/log) to change.
    class PowerPage : IPage
    {
        readonly MainWindow w;
        readonly StackPanel stack = new StackPanel { Margin = new Thickness(36, 0, 36, 36) };
        readonly ScrollViewer view;
        // hero
        readonly Border heroIcon; readonly TextBlock heroGlyph, heroName, heroDesc, heroLive;
        // mode cards
        readonly List<Border> cards = new List<Border>();
        readonly List<Border> dots = new List<Border>();
        // comparison table: [row][mode column]
        readonly TextBlock[,] cells = new TextBlock[4, 4];
        readonly Border[] colBg = new Border[4];
        readonly Border scanBtn; readonly TextBlock scanNote;
        int shown = -2; bool scanning;

        // relative ratings 1..4 from the manufacturer's descriptions: performance, quietness, battery life
        static readonly int[][] Ratings = { new[] { 1, 3, 4 }, new[] { 2, 4, 3 }, new[] { 3, 2, 2 }, new[] { 4, 1, 1 } };

        public string Title { get { return "Power Mode"; } }
        public string Subtitle { get { return "Pick how the laptop balances speed, noise and battery"; } }
        public FrameworkElement View { get { return view; } }
        public Panel StaggerRoot { get { return stack; } }

        public PowerPage(MainWindow w)
        {
            this.w = w;
            view = UI.Scroll(stack);

            // ---- 1. active mode
            var hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); hg.ColumnDefinitions.Add(new ColumnDefinition());
            heroIcon = new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(20), Background = Theme.AccentSoft, VerticalAlignment = VerticalAlignment.Center };
            heroGlyph = K.Glyph("", 26, Theme.Accent); heroIcon.Child = heroGlyph;
            hg.Children.Add(heroIcon);
            var ht = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var hr = new StackPanel { Orientation = Orientation.Horizontal };
            hr.Children.Add(K.TB("Active", 12.5, Theme.Text3, FontWeights.SemiBold));
            ht.Children.Add(hr);
            heroName = K.TB("–", 24, Theme.Text, FontWeights.Bold); heroName.FontFamily = K.Display;
            heroDesc = K.TB("", 13, Theme.Text2, FontWeights.Medium); heroDesc.TextWrapping = TextWrapping.Wrap; heroDesc.TextTrimming = TextTrimming.None; heroDesc.Margin = new Thickness(0, 2, 0, 0);
            heroLive = K.TB("", 12.5, Theme.Text3, FontWeights.Medium); heroLive.Margin = new Thickness(0, 6, 0, 0);
            ht.Children.Add(heroName); ht.Children.Add(heroDesc); ht.Children.Add(heroLive);
            Grid.SetColumn(ht, 1); hg.Children.Add(ht);
            stack.Children.Add(K.Card(hg, new Thickness(22)));

            // ---- 2. choose
            var cg = UI.Columns(1, 1, 1, 1); cg.Margin = new Thickness(0, 18, 0, 0);
            for (int i = 0; i < Modes.All.Length; i++) UI.Put(cg, ModeCard(Modes.All[i], Ratings[i]), i);
            stack.Children.Add(cg);
            var rn = K.TB("Ratings are relative and follow the manufacturer's description of each mode.", 12, Theme.Text3, FontWeights.Medium); rn.Margin = new Thickness(4, 10, 0, 0);
            stack.Children.Add(rn);

            // ---- 3. what changes between modes
            var tc = new StackPanel();
            tc.Children.Add(UI.Header("", "What each mode changes", null));
            var tbl = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            for (int c = 0; c < 4; c++) tbl.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < 5; r++) tbl.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < 4; c++)
            {
                colBg[c] = new Border { CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Margin = new Thickness(3, 0, 3, 0) };
                Grid.SetColumn(colBg[c], c + 1); Grid.SetRowSpan(colBg[c], 5); tbl.Children.Add(colBg[c]);
                var m = Modes.All[c];
                var hd = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 8, 10) };
                hd.Children.Add(K.Glyph(m.Glyph, 13, Modes.BrushOf(m))); var hn = K.TB(m.Name, 13, Theme.Text, FontWeights.SemiBold); hn.Margin = new Thickness(7, 0, 0, 0); hd.Children.Add(hn);
                Grid.SetColumn(hd, c + 1); tbl.Children.Add(hd);
            }
            string[] rows = { "Fans (your setting)", "Built-in fan limit", "NVIDIA Dynamic Boost", "Screen refresh rate" };
            string[] rowHelp = { "Automatic, Custom curve or Maximum — set on the Cooling page", "How fast the BIOS lets the fans go with automatic fans", "Lets the GPU borrow power from the CPU in games", "On battery and on the charger" };
            for (int r = 0; r < 4; r++)
            {
                var lb = new StackPanel { Margin = new Thickness(0, 10, 10, 10) };
                lb.Children.Add(K.TB(rows[r], 13, Theme.Text, FontWeights.SemiBold));
                var hlp = K.TB(rowHelp[r], 11.5, Theme.Text3, FontWeights.Medium); hlp.TextWrapping = TextWrapping.Wrap; hlp.TextTrimming = TextTrimming.None;
                lb.Children.Add(hlp);
                Grid.SetRow(lb, r + 1); tbl.Children.Add(lb);
                var line = new Border { Height = 1, Background = Theme.Hairline, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetRow(line, r + 1); Grid.SetColumnSpan(line, 5); tbl.Children.Add(line);
                for (int c = 0; c < 4; c++)
                {
                    var t = K.TB("–", 12.5, Theme.Text2, FontWeights.Medium); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
                    t.Margin = new Thickness(12, 10, 8, 10); t.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetRow(t, r + 1); Grid.SetColumn(t, c + 1); tbl.Children.Add(t);
                    cells[r, c] = t;
                }
            }
            tc.Children.Add(tbl);
            // ControlHub extra: keep the low refresh rate of Quiet / Battery Saver on the charger too
            var rt = new Toggle(RefreshPolicy.Always);
            rt.Changed += on =>
            {
                RefreshPolicy.SetAlways(on); tableAt = DateTime.MinValue;
                w.Island.Show("", on ? "Quiet & Battery Saver: 60 Hz on the charger too" : "Refresh rate follows the built-in behaviour", null);
            };
            var rrow = UI.Row(UI.Label2("Also lower the refresh rate on the charger",
                "The built-in service only drops Quiet and Battery Saver to 60 Hz on battery. Turn this on to use 60 Hz in those modes while plugged in as well; Balanced and Performance always use the highest rate."), rt);
            rrow.Margin = new Thickness(0, 18, 0, 0);
            tc.Children.Add(rrow);
            var foot = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            foot.ColumnDefinitions.Add(new ColumnDefinition()); foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            scanNote = K.TB("", 12, Theme.Text3, FontWeights.Medium); scanNote.TextWrapping = TextWrapping.Wrap; scanNote.TextTrimming = TextTrimming.None; scanNote.VerticalAlignment = VerticalAlignment.Center; scanNote.Margin = new Thickness(0, 0, 16, 0);
            foot.Children.Add(scanNote);
            scanBtn = K.PillBtn("Read built-in fan limits", Theme.Raised, Theme.Text, 36, AskScan);
            Grid.SetColumn(scanBtn, 1); foot.Children.Add(scanBtn);
            tc.Children.Add(foot);
            var tcard = K.Card(tc, new Thickness(22)); tcard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(tcard);

            var note = K.TB("Modes are applied by the built-in FnKey service, exactly like the original Control Center. You can also switch from the tray icon.", 12, Theme.Text3, FontWeights.Medium);
            note.Margin = new Thickness(4, 14, 0, 0); note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None;
            stack.Children.Add(note);
        }

        public void ThemeChanged() { shown = -2; Show(w.PendingMode >= 0 ? w.PendingMode : Hw.Last.Mode); }

        Border ModeCard(ModeInfo m, int[] rating)
        {
            var c = m.Col();
            var sp = new StackPanel { Margin = new Thickness(18) };
            var top = new Grid();
            var ic = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(14), Background = Modes.SoftOf(m), HorizontalAlignment = HorizontalAlignment.Left };
            ic.Child = K.Glyph(m.Glyph, 19, Modes.BrushOf(m));
            top.Children.Add(ic);
            var ring = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), BorderBrush = Theme.Text3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Modes.BrushOf(m), RenderTransform = new ScaleTransform(0, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
            ring.Child = dot; top.Children.Add(ring);
            sp.Children.Add(top);
            var name = K.TB(m.Name, 17, Theme.Text, FontWeights.Bold); name.FontFamily = K.Display; name.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(name);
            var d = K.TB(m.Desc, 12.5, Theme.Text2, FontWeights.Normal); d.TextWrapping = TextWrapping.Wrap; d.TextTrimming = TextTrimming.None; d.Margin = new Thickness(0, 4, 0, 14); d.Height = 52;
            sp.Children.Add(d);
            string[] labels = { "Performance", "Quietness", "Battery life" };
            for (int i = 0; i < 3; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(K.TB(labels[i], 11.5, Theme.Text3, FontWeights.SemiBold));
                var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                for (int k = 0; k < 4; k++)
                    bars.Children.Add(new Border { Width = 14, Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(3, 0, 0, 0), Background = k < rating[i] ? Modes.BrushOf(m) : (Brush)Theme.Hover });
                Grid.SetColumn(bars, 1); row.Children.Add(bars);
                sp.Children.Add(row);
            }
            var card = new Border { CornerRadius = new CornerRadius(20), Background = Theme.Surface, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1.5), Child = sp, Cursor = Cursors.Hand };
            var mode = m.Value;
            K.Pressable(card, () => { if (!scanning) w.RequestMode(mode); }, true);
            cards.Add(card); dots.Add(dot);
            return card;
        }

        public void Optimistic(int mode) { Show(mode); }

        void Show(int mode)
        {
            int idx = Modes.IndexOf(mode);
            if (idx == shown) return;
            shown = idx;
            for (int i = 0; i < cards.Count; i++)
            {
                bool on = i == idx;
                var c = Modes.All[i].Col();
                cards[i].BorderBrush = on ? K.Solid(Theme.A(c, 0.85)) : Theme.Hairline;
                cards[i].Effect = on ? new DropShadowEffect { Color = c, BlurRadius = 28, ShadowDepth = 0, Opacity = 0.3 } : null;
                K.Pop((ScaleTransform)dots[i].RenderTransform, on ? 1 : 0, on ? 480 : 200, on ? K.Elastic : K.OutCubic);
                colBg[i].Background = on ? K.Solid(Theme.A(c, 0.09)) : Brushes.Transparent;
            }
            var m = Modes.Of(mode);
            if (m != null)
            {
                var c = m.Col();
                heroName.Text = m.Name; heroDesc.Text = m.Desc;
                heroGlyph.Text = m.Glyph; heroGlyph.Foreground = K.Solid(c); heroIcon.Background = K.Solid(Theme.A(c, 0.16));
            }
        }

        // ---------------------------------------------------------------- comparison table
        static int FanSetting(int pm, Snapshot s)
        {
            if (!Dchu.Available) return -1;
            if (pm == 1) return 0;
            if (Modes.Of(s.Mode) == Modes.Of(pm)) return Dchu.ReadApp(1029, 1)[0];
            int slot = pm == 0 || pm == 4 ? 0 : pm;
            return Dchu.ReadApp(1094 + slot, 1)[0];
        }

        void FillTable(Snapshot s)
        {
            for (int c = 0; c < 4; c++)
            {
                var m = Modes.All[c];
                int fm = FanSetting(m.Value, s);
                cells[0, c].Text = fm == 1 ? "Maximum" : fm == 6 ? "Custom curve" : fm == 0 ? (m.Value == 1 ? "Automatic (always)" : "Automatic") : fm < 0 ? "–" : "OEM mode " + fm;
                var e = FanCache.Get(m.Value);
                if (e != null && e.FanMode != 6 && e.FanMode != 1)
                {
                    int top0 = 0, top1 = 0;
                    for (int k = 0; k < 10; k++) { top0 = Math.Max(top0, e.Cpu.Duty[k]); top1 = Math.Max(top1, e.Gpu.Duty[k]); }
                    cells[1, c].Text = top0 >= 100 && top1 >= 100 ? "No cap: up to full speed"
                        : string.Format(K.EN, "CPU up to {0:N0} rpm ({1}%) · GPU up to {2:N0} rpm ({3}%)", Math.Round(e.Max0 * top0 / 100.0), top0, Math.Round(e.Max1 * top1 / 100.0), top1);
                    cells[1, c].Foreground = Theme.Text;
                }
                else { cells[1, c].Text = "Not read yet"; cells[1, c].Foreground = Theme.Text3; }
                // known from the FnKey service (code + its log on this laptop)
                cells[2, c].Text = m.Value == 4 ? "Off" : m.Value == 3 || m.Value == 2 ? "On" : "Unknown";
                // FnKey SetPanelRefreshRate: modes 1 and 4 lower the panel on battery, 2 and 3 restore the full rate
                cells[3, c].Text = m.Value == 4 || m.Value == 1
                    ? (RefreshPolicy.Always ? "60 Hz, also on the charger" : "60 Hz on battery · full on the charger")
                    : "Full refresh rate";
                cells[2, c].Foreground = cells[2, c].Text == "Unknown" ? Theme.Text3 : Theme.Text2;
                cells[3, c].Foreground = cells[3, c].Text == "Unknown" ? Theme.Text3 : Theme.Text2;
            }
            int missing = 0;
            foreach (var m in Modes.All) { var e = FanCache.Get(m.Value); if (e == null || e.FanMode == 6 || e.FanMode == 1) missing++; }
            scanNote.Text = scanning ? "Reading… the laptop switches modes for a few seconds each."
                : missing == 0 ? "Built-in fan limits are read from the fan controller."
                : "Built-in fan limits can only be read while a mode runs with automatic fans. Read them now, or they fill in as you use each mode.";
        }

        // ---------------------------------------------------------------- read the BIOS fan tables of every mode
        void AskScan()
        {
            if (scanning) return;
            var cur = Modes.Of(Hw.Last.Mode);
            var custom = new List<string>();
            foreach (var m in Modes.All) { int fm = FanSetting(m.Value, Hw.Last); if (fm != 0) custom.Add(m.Name); }
            w.Confirm("Read built-in fan limits?",
                "ControlHub switches through every power mode for about 8 seconds each and reads its automatic fan table, then goes back to " + (cur != null ? cur.Name : "the current mode") + "."
                + (custom.Count > 0 ? " In " + string.Join(", ", custom.ToArray()) + " the fans are briefly set to Automatic to read the table, then your setting (and custom curve) is put back." : "")
                + " The fans may change speed while this runs.",
                "Start", ok => { if (ok) RunScan(Hw.Last.Mode); });
        }

        // Per mode: switch -> (fans to Automatic if needed) -> read table -> put the fan setting back.
        void RunScan(int original)
        {
            scanning = true; scanBtn.Opacity = 0.45; scanBtn.IsHitTestVisible = false;
            int i = 0, phase = 0, restore = -1;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            Action finish = () =>
            {
                t.Stop();
                Hw.SetPowerMode(original); Hw.Poke();
                var om = Modes.Of(original);
                w.Island.Show("", "Done · back to " + (om != null ? om.Name : "previous mode"), Theme.Green);
                scanning = false; scanBtn.Opacity = 1; scanBtn.IsHitTestVisible = true;
                tableAt = DateTime.MinValue;
            };
            Action step = () =>
            {
                if (i >= Modes.All.Length) { finish(); return; }
                var m = Modes.All[i];
                switch (phase)
                {
                    case 0:   // switch to the mode
                        w.Island.Show(m.Glyph, string.Format("Reading {0}… ({1}/{2})", m.Name, i + 1, Modes.All.Length), K.Solid(m.Col()));
                        Hw.SetPowerMode(m.Value); Hw.Poke();
                        phase = 1; break;
                    case 1:   // make sure the fans run the BIOS table
                        int fm = Dchu.Available ? Dchu.ReadApp(1029, 1)[0] : 0;
                        restore = m.Value != 1 && fm != 0 ? fm : -1;
                        if (restore >= 0) Hw.SetFanMode(0);
                        phase = 2; break;
                    case 2:   // read it (FanCache keeps automatic tables)
                        Hw.RefreshFans();
                        phase = 3; break;
                    default:  // put the user's fan setting back, then the next mode
                        if (restore >= 0) Hw.SetFanMode(restore);
                        restore = -1; phase = 0; i++;
                        break;
                }
            };
            t.Tick += delegate { step(); };
            step(); t.Start();
        }

        // ---------------------------------------------------------------- live
        DateTime tableAt;
        public void Update(Snapshot s)
        {
            if (s == null || !s.Ok || !view.IsVisible) return;
            if (w.PendingMode < 0) Show(s.Mode);
            heroLive.Text = string.Format(K.EN, "Now: CPU {0}°C{1} · CPU fan {2:N0} rpm · GPU {3}", s.CpuTemp,
                s.CpuWatts >= 0 ? string.Format(K.EN, " at {0:0.0} W", s.CpuWatts) : "", s.Fans[0].Rpm,
                UI.GpuAsleep(s) ? "asleep" : string.Format(K.EN, "{0}°C, fan {1:N0} rpm", s.GpuTemp, s.Fans[1].Rpm));
            if ((DateTime.Now - tableAt).TotalSeconds >= 3) { tableAt = DateTime.Now; FillTable(s); }
        }
        public void OnShown() { shown = -2; tableAt = DateTime.MinValue; Update(Hw.Last); }
    }

    // ===================================================================== Keyboard
    class KeyboardPage : IPage
    {
        readonly MainWindow w;
        readonly StackPanel stack = new StackPanel { Margin = new Thickness(36, 0, 36, 36) };
        readonly ScrollViewer view;
        readonly Canvas kbCanvas = new Canvas();
        readonly List<Border> keys = new List<Border>();
        readonly Border kbGlow;
        readonly Toggle power, sleepT;
        readonly Segmented sleepSeg;
        readonly List<FxTileParts> fxTiles = new List<FxTileParts>();
        class FxTileParts { public KbFx.Kind Kind; public Border Card, Dot, Ring; public SolidColorBrush Swatch; }
        TextBlock fxNow, fxSpeedName; StackPanel fxSpeedBox;
        TextBlock fxXSub, fxXChevron; System.Windows.Controls.Primitives.UniformGrid fxXTiles; bool fxXOpen;

        void ShowExperimental(bool open, bool anim)
        {
            fxXOpen = open;
            fxXTiles.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            fxXSub.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            var rt = (RotateTransform)fxXChevron.RenderTransform;
            if (anim) { rt.BeginAnimation(RotateTransform.AngleProperty, K.Anim(open ? 180 : 0, 260, K.OutCubic)); if (open) Motion.Stagger(fxXTiles, 10, 35); }
            else { rt.BeginAnimation(RotateTransform.AngleProperty, null); rt.Angle = open ? 180 : 0; }
        }

        Border FxTile(KbFx.Kind kind)
        {
            var p = new FxTileParts { Kind = kind, Swatch = new SolidColorBrush(Colors.Gray) };
            var sp = new StackPanel { Margin = new Thickness(14, 14, 14, 13) };
            var top = new Grid();
            // live mini preview of the effect
            var sw = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = p.Swatch, HorizontalAlignment = HorizontalAlignment.Left, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1) };
            top.Children.Add(sw);
            p.Ring = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), BorderBrush = Theme.Text3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            p.Dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.AccentFill, RenderTransform = new ScaleTransform(0, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
            p.Ring.Child = p.Dot; top.Children.Add(p.Ring);
            sp.Children.Add(top);
            var n = K.TB(KbFx.Name(kind), 13.5, Theme.Text, FontWeights.SemiBold); n.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(n);
            var tg = K.TB(KbFx.Tag(kind), 11.5, Theme.Text3, FontWeights.Medium); tg.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(tg);
            p.Card = new Border { CornerRadius = new CornerRadius(14), Background = Theme.Raised, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1.5), Child = sp, Cursor = Cursors.Hand, Margin = new Thickness(5) };
            K.Pressable(p.Card, () => ChooseFx(kind), true);
            fxTiles.Add(p);
            return p.Card;
        }

        void ChooseFx(KbFx.Kind kind)
        {
            if (kind == KbFx.Current) return;
            KbFx.Set(kind);
            if (kind != KbFx.Kind.Static && !Hw.Kb.On) Hw.KbSetFine(Math.Max(1, Hw.Kb.Level), true);
            suppress = true; fxSpeed.SetValue(KbFx.Speed, false); suppress = false;
            UpdateFxUi(true);
            w.Island.Show("", kind == KbFx.Kind.Static ? "Keyboard effect off" : KbFx.Name(kind) + " effect", null);
        }

        void SelectTiles(bool anim)
        {
            foreach (var p in fxTiles)
            {
                bool on = p.Kind == KbFx.Current;
                p.Card.BorderBrush = on ? Theme.Accent : Theme.Hairline;
                p.Ring.BorderBrush = on ? Theme.Accent : Theme.Text3;
                var st = (ScaleTransform)p.Dot.RenderTransform;
                if (anim) K.Pop(st, on ? 1 : 0, on ? 480 : 200, on ? K.Elastic : K.OutCubic);
                else { st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.BeginAnimation(ScaleTransform.ScaleYProperty, null); st.ScaleX = st.ScaleY = on ? 1 : 0; }
            }
            fxNow.Text = KbFx.Current == KbFx.Kind.Static ? "Off" : KbFx.Name(KbFx.Current);
            if (KbFx.IsExperimental(KbFx.Current) && !fxXOpen) ShowExperimental(true, anim);
        }

        // swatch colour = effect colour, dimmed by the effect's brightness
        void PaintTiles(KbState kb)
        {
            var bg = Theme.Current.Raised;
            foreach (var p in fxTiles)
            {
                Color c; double lv;
                KbFx.Visual(p.Kind, kb, true, out c, out lv);
                p.Swatch.Color = Theme.Mix(bg, c, 0.25 + 0.75 * Math.Max(0, Math.Min(1, lv / 4.0)));
            }
        }
        readonly SliderBar level; readonly TextBlock levelValue;
        double shownLevel = 1;

        // round to 0.01 and pull gently onto whole levels
        static double Snap(double v)
        {
            v = Math.Round(v, 2);
            double r = Math.Round(v);
            return Math.Abs(v - r) < 0.05 ? r : v;
        }

        void ShowLevel(double f, bool on)
        {
            shownLevel = f;
            levelValue.Text = on ? f.ToString("0.00", K.EN) : "Off";
            levelLabel.Text = on ? "of 4" : "";
            PaintKeys(cur, on, f);
        }
        readonly SliderBar hue, sat;
        readonly Border swatch; readonly TextBlock hexText, levelLabel, sleepLabel, typeLabel;
        readonly WrapPanel presets = new WrapPanel();
        readonly Border effectCard;
        static readonly int[] SleepPresets = { 10, 30, 60, 90, 300 };
        readonly LinearGradientBrush satTrack = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        bool suppress; Color cur = Colors.DodgerBlue, previewCol = Colors.DodgerBlue; double curH, curS;

        public string Title { get { return "Keyboard"; } }
        public string Subtitle { get { return "RGB backlight, brightness and auto-off timer"; } }
        public FrameworkElement View { get { return view; } }
        public Panel StaggerRoot { get { return stack; } }

        public KeyboardPage(MainWindow w)
        {
            this.w = w;
            view = UI.Scroll(stack);

            // ---- preview
            var pv = new Grid { Height = 230 };
            kbGlow = new Border { CornerRadius = new CornerRadius(40), Margin = new Thickness(60, 40, 60, 20), Opacity = 0.0 };
            kbGlow.Effect = new BlurEffect { Radius = 60 };
            pv.Children.Add(kbGlow);
            var vb = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(10), Child = kbCanvas };
            pv.Children.Add(vb);
            BuildKeys();
            typeLabel = K.TB("", 12, Theme.Text3, FontWeights.Medium);
            typeLabel.HorizontalAlignment = HorizontalAlignment.Right; typeLabel.VerticalAlignment = VerticalAlignment.Bottom;
            pv.Children.Add(typeLabel);
            stack.Children.Add(K.Card(pv, new Thickness(22)));

            // ---- light + level
            var r = UI.Columns(1, 1.25);
            r.Margin = new Thickness(0, 18, 0, 0);
            var lc = new StackPanel();
            power = new Toggle(false);
            power.Changed += on =>
            {
                if (suppress) return;
                var kb = Hw.Kb;
                double f = on ? (kb.Precise > 0 ? kb.Precise : Math.Max(1, kb.Level)) : 0;
                Hw.KbSetFine(f, true);
                w.Island.Show("", on ? "Keyboard light on" : "Keyboard light off", null);
            };
            lc.Children.Add(UI.Row(UI.Header("", "Keyboard light", null), power));
            lc.Children.Add(UI.Divider());
            levelLabel = K.TB("", 12.5, Theme.Text3, FontWeights.Medium);
            levelValue = K.TB("", 20, Theme.Text, FontWeights.Bold); levelValue.FontFamily = K.Display; K.Tabular(levelValue);
            var lvHead = new StackPanel { Orientation = Orientation.Horizontal };
            lvHead.Children.Add(levelValue); levelLabel.Margin = new Thickness(10, 4, 0, 0); lvHead.Children.Add(levelLabel);
            lc.Children.Add(UI.Row(K.TB("Brightness", 14, Theme.Text, FontWeights.SemiBold), lvHead));
            level = new SliderBar(0, 4, 0, 1, 10);
            level.Margin = new Thickness(0, 12, 0, 0);
            level.Changed += v => { if (suppress) return; double f = Snap(v); ShowLevel(f, f > 0); Hw.KbSetFine(f, false); };
            level.Committed += v =>
            {
                if (suppress) return;
                double f = Snap(v);
                suppress = true; level.SetValue(f, false); suppress = false;
                ShowLevel(f, f > 0);
                Hw.KbSetFine(f, true);
                w.Island.Show("", f > 0 ? "Keyboard brightness " + f.ToString("0.00", K.EN) : "Keyboard light off", null);
            };
            lc.Children.Add(level);
            // labels sit exactly under the knob positions of 0..4 (knob is 20 px wide)
            var ticks = new Canvas { Height = 18, Margin = new Thickness(0, 4, 0, 0) };
            string[] tl = { "Off", "1", "2", "3", "4" };
            var tickTb = new List<TextBlock>();
            foreach (var s in tl) { var t = K.TB(s, 11.5, Theme.Text3, FontWeights.SemiBold); tickTb.Add(t); ticks.Children.Add(t); }
            ticks.SizeChanged += delegate
            {
                double usable = Math.Max(0, ticks.ActualWidth - 20);
                for (int i = 0; i < 5; i++)
                {
                    var t = tickTb[i]; t.Measure(new Size(100, 30));
                    double x = 10 + usable * i / 4.0 - t.DesiredSize.Width / 2;
                    Canvas.SetLeft(t, Math.Max(0, Math.Min(ticks.ActualWidth - t.DesiredSize.Width, x)));
                }
            };
            lc.Children.Add(ticks);
            lc.Children.Add(UI.Divider());
            sleepT = new Toggle(false);
            sleepT.Changed += on => { if (!suppress) Hw.KbSetSleep(on, SleepPresets[Math.Max(0, sleepSeg.Selected)]); };
            lc.Children.Add(UI.Row(UI.Label2("Auto-off", "Turns the light off when the keyboard is idle"), sleepT));
            sleepSeg = new Segmented(new[] { "10 s", "30 s", "1 min", "1.5 min", "5 min" }, 1, 36);
            sleepSeg.Margin = new Thickness(0, 12, 0, 0);
            sleepSeg.Changed += i => { if (!suppress) Hw.KbSetSleep(true, SleepPresets[i]); };
            lc.Children.Add(sleepSeg);
            sleepLabel = K.TB("", 12, Theme.Text3, FontWeights.Medium); sleepLabel.Margin = new Thickness(0, 8, 0, 0);
            lc.Children.Add(sleepLabel);
            UI.Put(r, K.Card(lc, new Thickness(22)), 0);

            // ---- colour
            var cc = new StackPanel();
            swatch = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(10), BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1) };
            hexText = K.TB("", 12.5, Theme.Text2, FontWeights.SemiBold); hexText.FontFamily = new FontFamily("Cascadia Mono, Consolas"); hexText.Margin = new Thickness(0, 0, 10, 0);
            var hx = new StackPanel { Orientation = Orientation.Horizontal }; hx.Children.Add(hexText); hx.Children.Add(swatch);
            cc.Children.Add(UI.Row(UI.Header("", "Color", null), hx));
            string[] pre = { "#ff3b30", "#ff9500", "#ffcc00", "#34c759", "#00c7be", "#32ade6", "#0a84ff", "#5e5ce6", "#af52de", "#ff2d55", "#ffffff", "#7f7f7f" };
            presets.Margin = new Thickness(-5, 14, 0, 4);
            foreach (var hexs in pre)
            {
                var col = Theme.Hex(hexs);
                var b = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = K.Solid(col), Margin = new Thickness(5), Cursor = Cursors.Hand, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1) };
                K.Pressable(b, () => PickColor(col, true), false);
                presets.Children.Add(b);
            }
            cc.Children.Add(presets);
            cc.Children.Add(new Border { Height = 10 });
            cc.Children.Add(K.TB("Hue", 12.5, Theme.Text3, FontWeights.SemiBold));
            hue = new SliderBar(0, 360, 1, 210, 12); hue.Margin = new Thickness(0, 6, 0, 10);
            var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(Theme.Hsv(i * 60, 1, 1), i / 6.0));
            hue.SetTrack(rainbow);
            hue.Changed += v => { curH = v; Preview(Theme.Hsv(curH, curS, 1)); };
            hue.Committed += v => PickColor(Theme.Hsv(curH, curS, 1), true);
            cc.Children.Add(hue);
            cc.Children.Add(K.TB("Saturation", 12.5, Theme.Text3, FontWeights.SemiBold));
            sat = new SliderBar(0, 100, 1, 100, 12); sat.Margin = new Thickness(0, 6, 0, 0);
            satTrack.GradientStops.Add(new GradientStop(Colors.White, 0)); satTrack.GradientStops.Add(new GradientStop(Colors.Red, 1));
            sat.SetTrack(satTrack);
            sat.Changed += v => { curS = v / 100; Preview(Theme.Hsv(curH, curS, 1)); };
            sat.Committed += v => PickColor(Theme.Hsv(curH, curS, 1), true);
            cc.Children.Add(sat);
            UI.Put(r, K.Card(cc, new Thickness(22)), 1);
            stack.Children.Add(r);

            // ---- effects (software: ControlHub animates the LEDs itself)
            var ec = new StackPanel();
            fxNow = K.TB("", 13, Theme.Accent, FontWeights.SemiBold);
            ec.Children.Add(UI.Row(UI.Header("", "Effects", null), fxNow));
            var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5, Margin = new Thickness(-5, 12, -5, 0) };
            foreach (var kind in KbFx.Main) tiles.Children.Add(FxTile(kind));
            ec.Children.Add(tiles);
            // experimental effects behind a fold
            var xr = new Grid { Margin = new Thickness(0, 12, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            xr.ColumnDefinitions.Add(new ColumnDefinition()); xr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var xl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            xl.Children.Add(K.TB("Experimental effects", 13.5, Theme.Text, FontWeights.SemiBold));
            fxXSub = K.TB("Heartbeat, Candle, Spectrum, Music, CPU load", 12, Theme.Text3, FontWeights.Medium); fxXSub.Margin = new Thickness(10, 1, 0, 0); fxXSub.VerticalAlignment = VerticalAlignment.Center;
            xl.Children.Add(fxXSub);
            xr.Children.Add(xl);
            fxXChevron = K.Glyph("", 12, Theme.Text2); Grid.SetColumn(fxXChevron, 1);
            fxXChevron.RenderTransform = new RotateTransform(0); fxXChevron.RenderTransformOrigin = new Point(0.5, 0.5);
            xr.Children.Add(fxXChevron);
            var xrow = new Border { CornerRadius = new CornerRadius(12), Background = Theme.Raised, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1), Padding = new Thickness(16, 11, 16, 11), Child = xr, Cursor = Cursors.Hand, Margin = new Thickness(0, 12, 0, 0) };
            K.Pressable(xrow, () => ShowExperimental(!fxXOpen, true), true);
            ec.Children.Add(xrow);
            fxXTiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5, Margin = new Thickness(-5, 6, -5, 0), Visibility = Visibility.Collapsed };
            foreach (var kind in KbFx.Experimental) fxXTiles.Children.Add(FxTile(kind));
            ec.Children.Add(fxXTiles);
            if (KbFx.IsExperimental(KbFx.Current)) ShowExperimental(true, false);
            fxDesc = K.TB("", 12.5, Theme.Text3, FontWeights.Medium); fxDesc.Margin = new Thickness(0, 14, 0, 0); fxDesc.TextWrapping = TextWrapping.Wrap; fxDesc.TextTrimming = TextTrimming.None;
            ec.Children.Add(fxDesc);
            var sp = UI.Columns(1, 1, 1); sp.Margin = new Thickness(0, 16, 0, 0);
            fxSpeedBox = new StackPanel();
            var spA = fxSpeedBox;
            fxSpeedVal = K.TB("", 13, Theme.Text, FontWeights.SemiBold); K.Tabular(fxSpeedVal);
            fxSpeedName = K.TB("Speed", 13, Theme.Text2, FontWeights.SemiBold);
            spA.Children.Add(UI.Row(fxSpeedName, fxSpeedVal));
            fxSpeed = new SliderBar(0, 1, 0, KbFx.Speed, 10); fxSpeed.Margin = new Thickness(0, 8, 0, 0);
            fxSpeed.Changed += v => { if (suppress) return; KbFx.Speed = v; ShowFxValues(); }; fxSpeed.Committed += v => { if (suppress) return; KbFx.SetSpeed(v); ShowFxValues(); };
            spA.Children.Add(fxSpeed);
            UI.Put(sp, spA, 0);
            // breathe range: both ends on the brightness scale, lowest locked at >= 0.50
            fxFloorBox = new StackPanel();
            fxFloorVal = K.TB("", 13, Theme.Text, FontWeights.SemiBold); K.Tabular(fxFloorVal);
            fxFloorBox.Children.Add(UI.Row(K.TB("Lowest", 13, Theme.Text2, FontWeights.SemiBold), fxFloorVal));
            fxFloor = new SliderBar(KbFx.MinLow, 4 - KbFx.Gap, 0, KbFx.Low, 10); fxFloor.Margin = new Thickness(0, 8, 0, 0);
            fxFloor.Changed += v =>
            {
                KbFx.Low = Math.Round(v, 2);
                if (KbFx.High < KbFx.Low + KbFx.Gap) { KbFx.High = Math.Round(KbFx.Low + KbFx.Gap, 2); fxHigh.SetValue(KbFx.High, false); }
                ShowFxValues();
            };
            fxFloor.Committed += v => { KbFx.SaveRange(); ShowFxValues(); };
            fxFloorBox.Children.Add(fxFloor);
            UI.Put(sp, fxFloorBox, 1);
            fxHighBox = new StackPanel();
            fxHighVal = K.TB("", 13, Theme.Text, FontWeights.SemiBold); K.Tabular(fxHighVal);
            fxHighBox.Children.Add(UI.Row(K.TB("Highest", 13, Theme.Text2, FontWeights.SemiBold), fxHighVal));
            fxHigh = new SliderBar(KbFx.MinLow + KbFx.Gap, 4, 0, KbFx.High, 10); fxHigh.Margin = new Thickness(0, 8, 0, 0);
            fxHigh.Changed += v =>
            {
                KbFx.High = Math.Round(v, 2);
                if (KbFx.Low > KbFx.High - KbFx.Gap) { KbFx.Low = Math.Max(KbFx.MinLow, Math.Round(KbFx.High - KbFx.Gap, 2)); fxFloor.SetValue(KbFx.Low, false); }
                ShowFxValues();
            };
            fxHigh.Committed += v => { KbFx.SaveRange(); ShowFxValues(); };
            fxHighBox.Children.Add(fxHigh);
            UI.Put(sp, fxHighBox, 2);
            fxParams = sp;
            ec.Children.Add(sp);
            effectCard = K.Card(ec, new Thickness(22)); effectCard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(effectCard);
            UpdateFxUi(false);
            view.IsVisibleChanged += delegate { if (view.IsVisible) StartPreviewFx(); };
            // effect changed elsewhere (IPC, colour pick): keep tiles and controls in sync
            KbFx.Changed += () => w.Dispatcher.BeginInvoke(new Action(() => UpdateFxUi(true)));

            OnKb(Hw.Kb);
        }

        void BuildKeys()
        {
            // a compact laptop layout: 6 rows, widths in key units
            double[][] rows =
            {
                new double[] { 1,1,1,1,1,1,1,1,1,1,1,1,1,1,1 },
                new double[] { 1,1,1,1,1,1,1,1,1,1,1,1,1,2 },
                new double[] { 1.5,1,1,1,1,1,1,1,1,1,1,1,1,1.5 },
                new double[] { 1.8,1,1,1,1,1,1,1,1,1,1,1,2.2 },
                new double[] { 2.3,1,1,1,1,1,1,1,1,1,1,2.7 },
                new double[] { 1.2,1.2,1.2,6.2,1.2,1.2,1,1,1 },
            };
            double u = 40, gap = 6, y = 0, maxW = 0;
            foreach (var row in rows)
            {
                double x = 0;
                foreach (var kw in row)
                {
                    double wpx = kw * u + (kw - 1) * gap;
                    var k = new Border { Width = wpx, Height = u, CornerRadius = new CornerRadius(8), Background = keyFace, BorderBrush = keyEdge, BorderThickness = new Thickness(1) };
                    Canvas.SetLeft(k, x); Canvas.SetTop(k, y);
                    kbCanvas.Children.Add(k); keys.Add(k);
                    x += wpx + gap;
                }
                maxW = Math.Max(maxW, x - gap);
                y += u + gap;
            }
            kbCanvas.Width = maxW; kbCanvas.Height = y - gap;
        }

        public void ThemeChanged() { Preview(cur); }

        void Preview(Color c)
        {
            swatch.Background = K.Solid(c);
            hexText.Text = Theme.ToHex(c).ToUpperInvariant();
            hue.SetKnob(K.Solid(Theme.Hsv(curH, 1, 1)));
            satTrack.GradientStops[1].Color = Theme.Hsv(curH, 1, 1);
            sat.SetKnob(K.Solid(c));
            previewCol = c;
            // while an effect runs, the preview timer paints the keys (with this colour while dragging)
            if (!fxLive || KbFx.Current == KbFx.Kind.Static) PaintKeys(c, Hw.Kb.On, shownLevel);
        }

        void PickColor(Color c, bool apply)
        {
            cur = c;
            double h, s, v; Theme.ToHsv(c, out h, out s, out v);
            if (s > 0.02) curH = h;
            curS = s;
            suppress = true;
            hue.SetValue(curH, false); sat.SetValue(curS * 100, false);
            // picking a colour ends the colour-changing effects (breathe keeps running with the new colour)
            if (apply && KbFx.OwnsColor(KbFx.Current)) { KbFx.Set(KbFx.Kind.Static); UpdateFxUi(true); }
            suppress = false;
            Preview(c);
            if (apply)
            {
                Hw.KbSetColor(c.R, c.G, c.B);
                if (!Hw.Kb.On) Hw.KbSetLevel(Math.Max(1, Hw.Kb.Level));
                w.Island.Show("", "Keyboard color " + Theme.ToHex(c).ToUpperInvariant(), K.Solid(c));
            }
        }

        // Shared (mutable) brushes: repainting the preview is just three colour changes.
        readonly SolidColorBrush keyFace = new SolidColorBrush(Colors.Gray), keyEdge = new SolidColorBrush(Colors.Gray), glowBrush = new SolidColorBrush(Colors.Gray);
        TextBlock fxSpeedVal, fxFloorVal;

        void ShowFxValues()
        {
            if (fxSpeedVal == null || fxHighVal == null) return;
            fxSpeedName.Text = KbFx.SpeedName(KbFx.Current);
            fxSpeedVal.Text = KbFx.SpeedText(KbFx.Current);
            fxFloorVal.Text = KbFx.Low.ToString("0.00", K.EN);
            fxHighVal.Text = KbFx.High.ToString("0.00", K.EN);
        }
        TextBlock fxHighVal; SliderBar fxHigh; StackPanel fxHighBox;

        TextBlock fxDesc; SliderBar fxSpeed, fxFloor; StackPanel fxFloorBox; Grid fxParams; bool fxLive;

        void PaintKeys(Color c, bool on, double lvl) { PaintKeys(c, on, lvl, false); }
        void PaintKeys(Color c, bool on, double lvl, bool instant)
        {
            if (lvl <= 0) on = false;
            keyFace.Color = on ? Theme.Mix(Theme.Current.Raised, c, 0.18 + 0.32 * lvl / 4.0) : Theme.Current.Raised;
            keyEdge.Color = on ? Theme.A(c, 0.35 + 0.5 * lvl / 4.0) : Theme.A(Theme.Current.Text, 0.08);
            if (kbGlow.Background != glowBrush) kbGlow.Background = glowBrush;
            glowBrush.Color = c;
            double op = on ? 0.18 + 0.32 * lvl / 4.0 : 0;
            if (instant) { kbGlow.BeginAnimation(UIElement.OpacityProperty, null); kbGlow.Opacity = op; }
            else kbGlow.BeginAnimation(UIElement.OpacityProperty, K.Anim(op, 500, K.OutCubic));
        }

        void UpdateFxUi(bool anim)
        {
            var k = KbFx.Current;
            fxDesc.Text = (KbFx.IsExperimental(k) ? "Experimental. " : "") + KbFx.Describe(k);
            if (KbFx.UsesRange(k)) fxDesc.Text += " Lowest is kept at 0.50 or more to avoid LED flicker.";
            if (k != KbFx.Kind.Static) fxDesc.Text += " Runs while ControlHub is open or in the tray, and turns the light off with the auto-off timer.";
            bool sp = KbFx.UsesSpeed(k), rg = KbFx.UsesRange(k);
            var pv = sp || rg ? Visibility.Visible : Visibility.Collapsed;
            if (anim && pv == Visibility.Visible && fxParams.Visibility != Visibility.Visible) K.PopIn(fxParams, 0.97, 260);
            fxParams.Visibility = pv;
            fxSpeedBox.Visibility = sp ? Visibility.Visible : Visibility.Hidden;
            fxFloorBox.Visibility = rg ? Visibility.Visible : Visibility.Hidden;
            fxHighBox.Visibility = fxFloorBox.Visibility;
            suppress = true; fxSpeed.SetValue(KbFx.Speed, false); suppress = false;
            SelectTiles(anim);
            // brightness effects own the brightness while they run
            bool own = rg;
            level.IsHitTestVisible = !own;
            level.BeginAnimation(UIElement.OpacityProperty, K.Anim(own ? 0.35 : 1, 220, K.OutCubic));
            levelLabel.Text = own ? "set by " + KbFx.Name(k) : (Hw.Kb.On ? "of 4" : "");
            ShowFxValues();
            if (k == KbFx.Kind.Static) { var kb = Hw.Kb; PaintKeys(Color.FromRgb(kb.R, kb.G, kb.B), kb.On, shownLevel); }
            StartPreviewFx();
        }

        // Mirror the effect on the on-screen keyboard while the page is visible.
        void StartPreviewFx()
        {
            if (K.AnimScale <= 0) { PaintTiles(Hw.Kb); return; }
            if (fxLive) return;
            fxLive = true;
            bool wasStatic = false;
            // ~30 fps on a timer, not the per-frame Rendering hook: that one keeps WPF composing at the
            // panel rate (165 Hz) and cost ~30 % of a core for a preview nobody needs that smooth
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += delegate
            {
                if (!view.IsVisible || K.AnimScale <= 0) { fxLive = false; timer.Stop(); return; }
                var kb = Hw.Kb;
                PaintTiles(kb);
                bool st = KbFx.Current == KbFx.Kind.Static;
                if (st)
                {
                    if (!wasStatic) PaintKeys(Color.FromRgb(kb.R, kb.G, kb.B), kb.On, shownLevel);
                }
                else
                {
                    Color c; double lv;
                    KbFx.Visual(kb, out c, out lv);
                    if ((hue.IsMouseCaptured || sat.IsMouseCaptured) && !KbFx.OwnsColor(KbFx.Current)) c = previewCol;
                    PaintKeys(c, kb.On, lv, true);
                }
                wasStatic = st;
            };
            timer.Start();
        }

        public void OnKb(KbState k)
        {
            suppress = true;
            power.Set(k.On, true);
            double fine = k.On ? (k.Precise > 0 ? k.Precise : k.Level) : 0;
            if (!level.IsMouseCaptured) { level.SetValue(fine, false); shownLevel = fine; levelValue.Text = k.On ? fine.ToString("0.00", K.EN) : "Off"; levelLabel.Text = KbFx.UsesRange(KbFx.Current) ? "set by " + KbFx.Name(KbFx.Current) : k.On ? "of 4" : ""; }
            sleepT.Set(k.SleepOn, true);
            int si = Array.IndexOf(SleepPresets, k.SleepSec);
            if (si >= 0) sleepSeg.Select(si, false); else sleepSeg.Clear();
            sleepSeg.Opacity = k.SleepOn ? 1 : 0.45; sleepSeg.IsHitTestVisible = k.SleepOn;
            sleepLabel.Text = k.SleepOn ? "Turns off after " + Dur(k.SleepSec) + " without typing" : "Light stays on";
            typeLabel.Text = k.Type == 6 ? "Single-zone RGB" : k.Type == 0 ? "No RGB backlight" : "Keyboard type " + k.Type;
            suppress = false;
            if (hue.IsMouseCaptured || sat.IsMouseCaptured) return;   // don't yank the knob mid-drag
            var c = Color.FromRgb(k.R, k.G, k.B);
            if (c.R == 0 && c.G == 0 && c.B == 0) c = Color.FromRgb(0, 0, 200);
            cur = c;
            double h, s, v; Theme.ToHsv(c, out h, out s, out v);
            if (s > 0.02) curH = h;
            curS = s;
            suppress = true; hue.SetValue(curH, false); sat.SetValue(curS * 100, false); suppress = false;
            Preview(c);
        }

        static string Dur(int s)
        {
            if (s < 60) return s + " seconds";
            if (s < 3600) return (s / 60) + " min" + (s % 60 != 0 ? " " + (s % 60) + " s" : "");
            return (s / 3600) + " h " + (s / 60 % 60) + " min";
        }

        public void Update(Snapshot s) { }
        public void OnShown()
        {
            Hw.RefreshKb();
            UpdateFxUi(false);
        }
    }

    // ===================================================================== Settings
    class SettingsPage : IPage
    {
        readonly MainWindow w;
        readonly StackPanel stack = new StackPanel { Margin = new Thickness(36, 0, 36, 36) };
        readonly ScrollViewer view;
        readonly List<Border> palCards = new List<Border>();
        readonly TextBlock about;

        public string Title { get { return "Settings"; } }
        public string Subtitle { get { return "Appearance, motion and app behavior"; } }
        public FrameworkElement View { get { return view; } }
        public Panel StaggerRoot { get { return stack; } }

        public SettingsPage(MainWindow w)
        {
            this.w = w;
            view = UI.Scroll(stack);

            // ---- theme
            var tc = new StackPanel();
            tc.Children.Add(UI.Header("", "Theme", null));
            var wp = new WrapPanel { Margin = new Thickness(-6, 12, 0, 0) };
            foreach (var p in Theme.Palettes)
            {
                var pal = p;
                var g = new Grid { Width = 150, Height = 84 };
                g.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = K.Solid(p.Surface) });
                var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 14, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                foreach (var c in new[] { p.Accent, p.Accent2, p.Green, p.Orange })
                    dots.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = K.Solid(c), Margin = new Thickness(0, 0, 6, 0) });
                g.Children.Add(dots);
                var nm = K.TB(p.Label, 13, K.Solid(p.Text), FontWeights.SemiBold); nm.Margin = new Thickness(14, 0, 0, 14); nm.VerticalAlignment = VerticalAlignment.Bottom;
                g.Children.Add(nm);
                var card = new Border { Child = g, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Margin = new Thickness(6), Cursor = Cursors.Hand, Padding = new Thickness(2) };
                K.Pressable(card, () => { Theme.Apply(pal, true); Settings.Set("palette", pal.Key); Mark(); w.Island.Show("", "Theme " + pal.Label, Theme.Accent); }, false);
                card.Tag = p.Key;
                palCards.Add(card); wp.Children.Add(card);
            }
            tc.Children.Add(wp);
            stack.Children.Add(K.Card(tc, new Thickness(22)));
            Mark();

            // ---- behaviour
            var bc = new StackPanel();
            bc.Children.Add(UI.Header("", "Motion & updates", null));
            bc.Children.Add(new Border { Height = 16 });
            int[] animVals = { 0, 60, 100, 140 };
            int ai = Array.IndexOf(animVals, Settings.Int("anim", 100)); if (ai < 0) ai = 2;
            var anim = new Segmented(new[] { "Off", "Fast", "Normal", "Relaxed" }, ai, 36); anim.Width = 340;
            anim.Changed += i => { K.AnimScale = animVals[i] / 100.0; Settings.Set("anim", animVals[i]); };
            bc.Children.Add(UI.Row(UI.Label2("Animation speed", "Duration of every transition and spring. Off also stops ambient motion."), anim));
            bc.Children.Add(UI.Divider());
            int[] ivals = { 500, 1000, 2000 };
            int ii = Array.IndexOf(ivals, Settings.Int("interval", 1000)); if (ii < 0) ii = 1;
            var iv = new Segmented(new[] { "0.5 s", "1 s", "2 s" }, ii, 36); iv.Width = 270;
            iv.Changed += i => { Hw.IntervalMs = ivals[i]; Settings.Set("interval", ivals[i]); Hw.Poke(); };
            bc.Children.Add(UI.Row(UI.Label2("Sensor interval", "How often temperatures and fans are read while the window is open"), iv));
            var bcard = K.Card(bc, new Thickness(22)); bcard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(bcard);

            // ---- system
            var sc = new StackPanel();
            sc.Children.Add(UI.Header("", "System", null));
            sc.Children.Add(new Border { Height = 16 });
            var tray = new Toggle(Settings.Bool("closeToTray", true));
            tray.Changed += on => Settings.Set("closeToTray", on);
            sc.Children.Add(UI.Row(UI.Label2("Close to tray", "The close button hides the window; ControlHub keeps running in the tray"), tray));
            sc.Children.Add(UI.Divider());
            var startup = new Toggle(StartupEnabled());
            startup.Changed += on => { SetStartup(on); w.Island.Show("", on ? "Starts with Windows" : "Won't start automatically", null); };
            sc.Children.Add(UI.Row(UI.Label2("Start with Windows", "Starts hidden in the tray after sign-in"), startup));
            var scard = K.Card(sc, new Thickness(22)); scard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(scard);

            // ---- about
            var ab = new StackPanel();
            ab.Children.Add(UI.Header("", "About this device", null));
            about = K.TB("", 12.5, Theme.Text2, FontWeights.Medium); about.TextWrapping = TextWrapping.Wrap; about.TextTrimming = TextTrimming.None; about.Margin = new Thickness(42, 10, 0, 12);
            ab.Children.Add(about);
            var logBtn = K.PillBtn("Open log", Theme.Raised, Theme.Text, 34, () => { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + System.IO.Path.Combine(AppDir.Data, "log.txt") + "\""); } catch { } });
            logBtn.HorizontalAlignment = HorizontalAlignment.Left; logBtn.Margin = new Thickness(42, 0, 0, 0);
            ab.Children.Add(logBtn);
            var acard = K.Card(ab, new Thickness(22)); acard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(acard);
        }

        void Mark()
        {
            foreach (var c in palCards) c.BorderBrush = (string)c.Tag == Theme.Current.Key ? Theme.Accent : Brushes.Transparent;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool StartupEnabled()
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("ControlHub") != null; } catch { return false; }
        }
        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("ControlHub", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --tray");
                    else k.DeleteValue("ControlHub", false);
                }
            }
            catch (Exception ex) { Log.Write("startup: " + ex.Message); }
        }

        public void Update(Snapshot s) { }
        public void OnShown()
        {
            var k = Hw.Kb; var s = Hw.Last;
            about.Text = string.Format("Hardware interface: {0}\nFans detected: {1}   ·   Keyboard type: {2}\nKeyboard effects: breathe {3}, cycle {4}\nData & log: {5}",
                Dchu.Available ? "InsydeDCHU (connected)" : "unavailable — " + Dchu.Error, s.FanCount, k.Type, k.CanBreathe ? "yes" : "no", k.CanCycle ? "yes" : "no", AppDir.Data);
        }
    }
}
