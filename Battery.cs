// Battery page: live battery status and FlexiCharger (charge limit) — same commands as the OEM app.
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
    // Large battery with an animated level, a charging shimmer and the charge-limit band.
    class BatteryGlyph : Grid
    {
        readonly Border body, fill, band, cap; readonly Line stopLine, startLine; readonly TextBlock bolt, stopLbl, startLbl;
        readonly Spring level = new Spring(0, 0.001); bool live, first = true;
        readonly LinearGradientBrush fillBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        readonly Canvas marks = new Canvas { IsHitTestVisible = false };
        const double W = 300, H = 120, Pad = 8;
        double startPct = -1, stopPct = -1;

        public BatteryGlyph()
        {
            Width = W + 16; Height = H + 34;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            body = new Border { Width = W, Height = H, CornerRadius = new CornerRadius(26), BorderThickness = new Thickness(3), BorderBrush = Theme.Text3, Padding = new Thickness(Pad) };
            var inner = new Grid { ClipToBounds = true };
            band = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Opacity = 0 };
            inner.Children.Add(band);
            fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(17), Background = fillBrush, Width = 0 };
            fillBrush.GradientStops.Add(new GradientStop(Colors.White, 0)); fillBrush.GradientStops.Add(new GradientStop(Colors.White, 1));
            inner.Children.Add(fill);
            bolt = K.Glyph("", 40, K.Solid(Colors.White)); bolt.Opacity = 0;
            bolt.Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 };
            inner.Children.Add(bolt);
            body.Child = inner;
            row.Children.Add(body);
            cap = new Border { Width = 10, Height = 42, CornerRadius = new CornerRadius(0, 6, 6, 0), Background = Theme.Text3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            row.Children.Add(cap);
            Children.Add(row);
            stopLine = new Line { Stroke = Theme.Text, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 2, 2 }, Opacity = 0 };
            startLine = new Line { Stroke = Theme.Text2, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 3 }, Opacity = 0 };
            stopLbl = K.TB("", 11.5, Theme.Text, FontWeights.SemiBold); stopLbl.Opacity = 0;
            startLbl = K.TB("", 11.5, Theme.Text3, FontWeights.SemiBold); startLbl.Opacity = 0;
            marks.Children.Add(startLine); marks.Children.Add(stopLine); marks.Children.Add(startLbl); marks.Children.Add(stopLbl);
            Children.Add(marks);
            level.Tune(60, 1.0);
            IsVisibleChanged += delegate { if (IsVisible) Kick(); };
        }

        double XOf(double pct) { return 3 + Pad + (W - 6 - 2 * Pad) * pct / 100.0; }

        public void Set(int pct, bool charging, Color c)
        {
            fillBrush.GradientStops[0].Color = Theme.Mix(c, Colors.Black, 0.15); fillBrush.GradientStops[1].Color = c;
            bolt.BeginAnimation(OpacityProperty, K.Anim(charging ? 0.95 : 0, 300, K.OutCubic));
            level.Target = Math.Max(0, Math.Min(100, pct));
            if (first || K.AnimScale <= 0) { first = false; level.Snap(level.Target); Apply(); return; }
            Kick();
        }

        // Shows the charge window: charging starts below `start` and stops at `stop` (stop = 100 hides it).
        public void SetLimit(int start, int stop)
        {
            bool on = stop > 0 && stop < 100;
            startPct = on ? start : -1; stopPct = on ? stop : -1;
            foreach (UIElement e in new UIElement[] { stopLine, startLine, stopLbl, startLbl })
                e.BeginAnimation(OpacityProperty, K.Anim(on ? 1 : 0, 260, K.OutCubic));
            band.BeginAnimation(OpacityProperty, K.Anim(on ? 1 : 0, 260, K.OutCubic));
            if (!on) return;
            double ys = 3, ye = H - 3;
            double xs = XOf(stop), xa = XOf(start);
            stopLine.X1 = stopLine.X2 = xs; stopLine.Y1 = ys - 6; stopLine.Y2 = ye + 6;
            startLine.X1 = startLine.X2 = xa; startLine.Y1 = ys; startLine.Y2 = ye;
            stopLbl.Text = "stop " + stop + "%"; startLbl.Text = "start " + start + "%";
            stopLbl.Measure(new Size(200, 50)); startLbl.Measure(new Size(200, 50));
            Canvas.SetLeft(stopLbl, xs - stopLbl.DesiredSize.Width / 2); Canvas.SetTop(stopLbl, H + 12);
            Canvas.SetLeft(startLbl, xa - startLbl.DesiredSize.Width / 2); Canvas.SetTop(startLbl, H + 12);
            if (Math.Abs(xs - xa) < 70) Canvas.SetLeft(startLbl, xs - 74 - startLbl.DesiredSize.Width / 2 + 30);
            band.Margin = new Thickness(XOf(stop) - 3 - Pad, 0, 0, 0);
            band.Width = Math.Max(0, (W - 6 - 2 * Pad) - (XOf(stop) - 3 - Pad));
            band.Background = new LinearGradientBrush(Theme.A(Theme.Current.Text, 0.10), Theme.A(Theme.Current.Text, 0.03), 0);
        }

        void Kick() { if (!live && IsVisible) { live = true; Frame.Add(Tick); } }
        bool Tick(double dt)
        {
            if (!IsVisible) { live = false; return false; }
            level.Step(dt); Apply();
            if (level.Settle()) { Apply(); live = false; return false; }
            return true;
        }
        void Apply() { fill.Width = Math.Max(0, (W - 6 - 2 * Pad) * level.Value / 100.0); }
    }

    class BatteryPage : IPage
    {
        readonly MainWindow w;
        readonly StackPanel stack = new StackPanel { Margin = new Thickness(36, 0, 36, 36) };
        readonly ScrollViewer view;
        readonly BatteryGlyph glyph = new BatteryGlyph();
        readonly TextBlock pct, state, rate, capacity, volts, timeLeft, limitNote, unsupported;
        readonly List<Border> optCards = new List<Border>();
        readonly List<Border> optDots = new List<Border>();
        readonly StackPanel customBox = new StackPanel();
        readonly WrapPanel startPanel = new WrapPanel(), stopPanel = new WrapPanel();
        readonly Border optionsCard;
        int choice = -1, selStart = -1, selStop = -1; bool busy;
        DateTime lastWmi; BatteryInfo info = new BatteryInfo();

        public string Title { get { return "Battery"; } }
        public string Subtitle { get { return "Live battery status and charge limit (FlexiCharger)"; } }
        public FrameworkElement View { get { return view; } }
        public Panel StaggerRoot { get { return stack; } }

        public BatteryPage(MainWindow w)
        {
            this.w = w;
            view = UI.Scroll(stack);

            // ---- status
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition());
            glyph.VerticalAlignment = VerticalAlignment.Center; glyph.Margin = new Thickness(6, 10, 0, 0);
            g.Children.Add(glyph);
            var right = new StackPanel { Margin = new Thickness(40, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            pct = K.TB("–", 52, Theme.Text, FontWeights.Bold); pct.FontFamily = K.Display;
            state = K.TB("", 15, Theme.Text2, FontWeights.SemiBold); state.Margin = new Thickness(2, 0, 0, 14);
            right.Children.Add(pct); right.Children.Add(state);
            var stats = new UniformGrid2(2);
            rate = Stat(stats, "Power"); timeLeft = Stat(stats, "Time left"); capacity = Stat(stats, "Capacity"); volts = Stat(stats, "Voltage");
            right.Children.Add(stats);
            Grid.SetColumn(right, 1); g.Children.Add(right);
            stack.Children.Add(K.Card(WithHeader("", "Status", g), new Thickness(22)));

            // ---- charge limit
            var oc = new StackPanel();
            var opts = UI.Columns(1, 1, 1);
            UI.Put(opts, Option(0, "", "Maximum", "Charges to 100%. Best when you need every minute on battery."), 0);
            UI.Put(opts, Option(1, "", "Recommended", "Charges between 70% and 80%. Best for a laptop that is plugged in most of the time."), 1);
            UI.Put(opts, Option(2, "", "Custom", "Pick when charging starts and where it stops."), 2);
            oc.Children.Add(opts);
            customBox.Margin = new Thickness(0, 18, 0, 0);
            customBox.Children.Add(K.TB("Start charging when below", 13, Theme.Text2, FontWeights.SemiBold));
            startPanel.Margin = new Thickness(-4, 8, 0, 14);
            customBox.Children.Add(startPanel);
            customBox.Children.Add(K.TB("Stop charging at", 13, Theme.Text2, FontWeights.SemiBold));
            stopPanel.Margin = new Thickness(-4, 8, 0, 0);
            customBox.Children.Add(stopPanel);
            customBox.Visibility = Visibility.Collapsed;
            oc.Children.Add(customBox);
            limitNote = K.TB("", 12.5, Theme.Text3, FontWeights.Medium); limitNote.Margin = new Thickness(0, 16, 0, 0); limitNote.TextWrapping = TextWrapping.Wrap; limitNote.TextTrimming = TextTrimming.None;
            oc.Children.Add(limitNote);
            optionsCard = K.Card(WithHeader("", "Charge limit", oc), new Thickness(22));
            optionsCard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(optionsCard);

            unsupported = K.TB("This laptop's BIOS doesn't report FlexiCharger support.", 13, Theme.Text3, FontWeights.Medium);
            unsupported.Margin = new Thickness(4, 16, 0, 0); unsupported.Visibility = Visibility.Collapsed;
            stack.Children.Add(unsupported);

            Hw.ChargeChanged += c => w.Dispatcher.BeginInvoke(new Action(() => OnCharge(c)));
        }

        static StackPanel WithHeader(string glyph, string title, FrameworkElement body)
        {
            var sp = new StackPanel();
            sp.Children.Add(UI.Header(glyph, title, null));
            body.Margin = new Thickness(0, 16, 0, 0);
            sp.Children.Add(body);
            return sp;
        }

        TextBlock Stat(Panel host, string label)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 12) };
            sp.Children.Add(K.TB(label, 12, Theme.Text3, FontWeights.Medium));
            var v = K.TB("–", 16, Theme.Text, FontWeights.SemiBold); K.Tabular(v);
            sp.Children.Add(v);
            host.Children.Add(sp);
            return v;
        }

        Border Option(int idx, string glyph, string name, string desc)
        {
            var sp = new StackPanel { Margin = new Thickness(18) };
            var top = new Grid();
            var ic = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(13), Background = Theme.AccentSoft, HorizontalAlignment = HorizontalAlignment.Left };
            ic.Child = K.Glyph(glyph, 18, Theme.Accent);
            top.Children.Add(ic);
            var ring = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), BorderBrush = Theme.Text3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Theme.AccentFill, RenderTransform = new ScaleTransform(0, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
            ring.Child = dot;
            top.Children.Add(ring);
            sp.Children.Add(top);
            var n = K.TB(name, 16, Theme.Text, FontWeights.Bold); n.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(n);
            var d = K.TB(desc, 12.5, Theme.Text2, FontWeights.Normal); d.TextWrapping = TextWrapping.Wrap; d.TextTrimming = TextTrimming.None; d.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(d);
            var card = new Border { CornerRadius = new CornerRadius(16), Background = Theme.Raised, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1.5), Child = sp, Cursor = Cursors.Hand };
            K.Pressable(card, () => Choose(idx), true);
            optCards.Add(card); optDots.Add(dot);
            return card;
        }

        void Choose(int idx)
        {
            if (busy || !Hw.Charge.Supported) return;
            if (idx == 2)
            {
                // custom starts from the current window (or 60 -> 80 when coming from Maximum)
                int s = selStart, e = selStop;
                if (choice != 2 || s < 0) { e = Hw.Charge.Stops.Contains(80) ? 80 : Hw.Charge.Stops[Hw.Charge.Stops.Count - 1]; s = Below(e); }
                ApplyCustom(s, e);
                return;
            }
            if (idx == choice) return;
            busy = true;
            Hw.SetCharge(idx, 0, 0);
            w.Island.Show("", idx == 0 ? "Charging to 100%" : "Charging between 70% and 80%", Theme.Green);
        }

        int Below(int stop)
        {
            int best = Hw.Charge.Starts[0];
            foreach (int s in Hw.Charge.Starts) if (s < stop) best = s;
            return best;
        }
        int Above(int start)
        {
            foreach (int e in Hw.Charge.Stops) if (e > start) return e;
            return Hw.Charge.Stops[Hw.Charge.Stops.Count - 1];
        }

        void ApplyCustom(int start, int stop)
        {
            if (start >= stop) return;
            busy = true;
            Hw.SetCharge(2, start, stop);
            w.Island.Show("", "Charging from " + start + "% to " + stop + "%", Theme.Green);
        }

        Border Chip(int v, bool sel, bool enabled, Action click)
        {
            var t = K.TB(v + "%", 13, sel ? Theme.OnAccent : Theme.Text, FontWeights.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Border { Width = 64, Height = 36, CornerRadius = new CornerRadius(12), Margin = new Thickness(4), Child = t, Background = sel ? (Brush)Theme.AccentFill : Theme.Raised, Cursor = enabled ? Cursors.Hand : Cursors.Arrow, Opacity = enabled ? 1 : 0.35 };
            if (enabled && !sel) K.Pressable(b, click, true);
            return b;
        }

        void BuildChips()
        {
            var c = Hw.Charge;
            startPanel.Children.Clear(); stopPanel.Children.Clear();
            foreach (int s in c.Starts)
            {
                int v = s;
                startPanel.Children.Add(Chip(v, v == selStart, v < selStop, () => ApplyCustom(v, selStop)));
            }
            foreach (int e in c.Stops)
            {
                int v = e;
                stopPanel.Children.Add(Chip(v, v == selStop, v > selStart, () => ApplyCustom(Math.Min(selStart, Below(v)) , v)));
            }
        }

        void OnCharge(ChargeState c)
        {
            busy = false;
            optionsCard.Visibility = c.Supported ? Visibility.Visible : Visibility.Collapsed;
            unsupported.Visibility = c.Supported ? Visibility.Collapsed : Visibility.Visible;
            if (!c.Supported) return;
            // what is really active: off -> Maximum, 70/80 with choice 1 -> Recommended, else Custom
            int ch = !c.Enabled ? 0 : (c.Start == 70 && c.Stop == 80 && c.Choice == 1 ? 1 : 2);
            choice = ch; selStart = c.Start; selStop = c.Stop;
            for (int i = 0; i < optCards.Count; i++)
            {
                bool on = i == ch;
                optCards[i].BorderBrush = on ? Theme.Accent : Theme.Hairline;
                K.Pop((ScaleTransform)optDots[i].RenderTransform, on ? 1 : 0, on ? 480 : 200, on ? K.Elastic : K.OutCubic);
            }
            customBox.Visibility = ch == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (ch == 2) BuildChips();
            glyph.SetLimit(c.Enabled ? c.Start : 0, c.Enabled ? c.Stop : 100);
            limitNote.Text = !c.Enabled ? "Charge limit is off: the battery charges to 100% whenever the charger is connected."
                : "While plugged in, the battery stops charging at " + c.Stop + "% and only starts again once it drops below " + c.Start + "%. Running on the charger in between is normal and keeps the battery healthier."
                ;
            UpdateStatus(Hw.Last);
        }

        void UpdateStatus(Snapshot s)
        {
            if (s == null) return;
            if (s.Battery < 0) { pct.Text = "–"; state.Text = "No battery detected"; return; }
            pct.Text = s.Battery + "%";
            var c = Hw.Charge;
            bool held = s.OnAc && !info.Charging && c.Enabled && s.Battery >= c.Start;
            state.Text = !s.OnAc ? "On battery" : info.Charging || s.Charging ? "Charging" : held ? "Plugged in · held by the charge limit" : s.Battery >= 99 ? "Plugged in · fully charged" : "Plugged in · not charging";
            Color col = s.Battery <= 20 && !s.OnAc ? Theme.Current.Red : s.OnAc ? Theme.Current.Green : Theme.Current.Accent;
            glyph.Set(s.Battery, s.OnAc && (info.Charging || s.Charging), col);
            if (info.Ok)
            {
                rate.Text = info.RateW > 0.05 ? string.Format(K.EN, "{0}{1:0.0} W", info.Discharging ? "−" : "+", info.RateW) : "0 W";
                capacity.Text = info.FullWh > 0 ? string.Format(K.EN, "{0:0.0} / {1:0.0} Wh", info.RemainingWh, info.FullWh) : string.Format(K.EN, "{0:0.0} Wh", info.RemainingWh);
                volts.Text = string.Format(K.EN, "{0:0.00} V", info.Volts);
            }
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            if (!s.OnAc && ps.BatteryLifeRemaining > 0) { var t = TimeSpan.FromSeconds(ps.BatteryLifeRemaining); timeLeft.Text = string.Format("{0}h {1:00}m", (int)t.TotalHours, t.Minutes); }
            else if (s.OnAc && info.Charging && info.RateW > 0.5 && info.FullWh > 0)
            {
                double target = c.Enabled ? c.Stop : 100;
                double whLeft = Math.Max(0, info.FullWh * target / 100 - info.RemainingWh);
                var t = TimeSpan.FromHours(whLeft / info.RateW);
                timeLeft.Text = whLeft <= 0.05 ? "–" : string.Format("{0}h {1:00}m to {2}%", (int)t.TotalHours, t.Minutes, target);
            }
            else timeLeft.Text = "–";
        }

        public void Update(Snapshot s)
        {
            if (!view.IsVisible) return;
            if ((DateTime.Now - lastWmi).TotalSeconds >= 3)
            {
                lastWmi = DateTime.Now;
                System.Threading.ThreadPool.QueueUserWorkItem(_ => { var b = BatteryInfo.Read(); w.Dispatcher.BeginInvoke(new Action(() => { info = b; UpdateStatus(Hw.Last); })); });
            }
            UpdateStatus(s);
        }

        public void OnShown() { lastWmi = DateTime.MinValue; Hw.RefreshCharge(); Update(Hw.Last); }

        public string Ipc(string arg)
        {
            var a = arg.Split(' ');
            if (a[0] == "state")
            {
                var c = Hw.Charge;
                return "supported=" + c.Supported + " enabled=" + c.Enabled + " start=" + c.Start + " stop=" + c.Stop + " choice=" + c.Choice + " starts=" + string.Join(",", c.Starts) + " stops=" + string.Join(",", c.Stops);
            }
            if (a[0] == "set") { Hw.SetCharge(int.Parse(a[1]), a.Length > 2 ? int.Parse(a[2]) : 0, a.Length > 3 ? int.Parse(a[3]) : 0); return "ok"; }
            if (a[0] == "refresh") { Hw.RefreshCharge(); return "ok"; }
            return "charge state | set <0|1|2> [start stop] | refresh";
        }
    }

    // Simple n-column uniform grid (left to right, top to bottom).
    class UniformGrid2 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid2(int cols) { Columns = cols; }
    }
}
