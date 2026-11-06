// Fans page: fan mode (Auto / Custom / Max) and a 10-point curve editor per power-mode profile.
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
    // Draggable 10-point curve: x = temperature 20..100 °C, y = duty 0..100 %.
    class CurveEditor : Grid
    {
        const double TMin = 20, TMax = 100;
        readonly Canvas canvas = new Canvas { ClipToBounds = false, Background = Brushes.Transparent };
        readonly Path line = new Path { StrokeThickness = 2.6, StrokeLineJoin = PenLineJoin.Round };
        readonly Path area = new Path();
        readonly Path ghost = new Path { StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 3, 3 }, Opacity = 0.55 };
        readonly List<Ellipse> dots = new List<Ellipse>();
        readonly List<TextBlock> axis = new List<TextBlock>();
        readonly List<Line> grid = new List<Line>();
        readonly Border tip; readonly TextBlock tipText;
        // live markers, only for the selected fan (the other fan's curve is just a ghost line):
        //   tDot = component temperature, riding the temperature axis (colour = how hot)
        //   fDot = the fan's real speed right now (colour = the fan's curve colour)
        readonly Ellipse tDot, fDot, fHalo; readonly SolidColorBrush fHaloBrush = new SolidColorBrush(Colors.Gray);readonly Line vGuide, hGuide;
        readonly Border tLbl, fLbl;
        static readonly Brush darkText = K.Solid(Color.FromRgb(20, 20, 24));
        readonly SolidColorBrush tBrush = new SolidColorBrush(Colors.Gray),tSoft = new SolidColorBrush(Colors.Gray), fSoft = new SolidColorBrush(Colors.Gray);
        readonly double[] lt = { -1, -1 }, ld = { 0, 0 }; readonly int[] lr = { 0, 0 };
        public int SelectedFan;   // which fan `curve` belongs to (the other one is the ghost)
        readonly SolidColorBrush stroke = new SolidColorBrush(Colors.White);
        readonly LinearGradientBrush fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        FanCurve curve = FanCurve.Default(), other;
        int drag = -1, hover = -1; int maxRpm;
        public event Action Edited;
        public bool Locked;
        const double PadL = 44, PadR = 16, PadT = 14, PadB = 30;

        public CurveEditor()
        {
            Height = 300;
            Children.Add(canvas);
            for (int i = 0; i <= 4; i++) { var l = new Line { Stroke = Theme.Hairline, StrokeThickness = 1 }; grid.Add(l); canvas.Children.Add(l); }
            for (int i = 0; i <= 8; i++) { var l = new Line { Stroke = Theme.Hairline, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 4 } }; grid.Add(l); canvas.Children.Add(l); }
            for (int i = 0; i < 5 + 9; i++) { var t = K.TB("", 11, Theme.Text3, FontWeights.Medium); axis.Add(t); canvas.Children.Add(t); }
            fill.GradientStops.Add(new GradientStop(Colors.White, 0)); fill.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            area.Fill = fill; line.Stroke = stroke; ghost.Stroke = Theme.Text3;
            canvas.Children.Add(ghost); canvas.Children.Add(area); canvas.Children.Add(line);
            vGuide = new Line { StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 3 }, Stroke = tSoft, Opacity = 0, IsHitTestVisible = false };
            hGuide = new Line { StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 3 }, Stroke = fSoft, Opacity = 0, IsHitTestVisible = false };
            canvas.Children.Add(vGuide); canvas.Children.Add(hGuide);
            tDot = new Ellipse { Width = 14, Height = 14, Opacity = 0, IsHitTestVisible = false, Fill = tBrush, Stroke = Theme.Surface, StrokeThickness = 2.5 };
            // solid ball with a white rim inside a soft halo: nothing like the hollow drag handles
            fDot = new Ellipse { Width = 18, Height = 18, Opacity = 0, IsHitTestVisible = false, Fill = stroke, Stroke = Brushes.White, StrokeThickness = 3 };
            fDot.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.8 };
            fHalo = new Ellipse { Width = 38, Height = 38, Opacity = 0, IsHitTestVisible = false, Fill = fHaloBrush };
            tLbl = Label(true); fLbl = Label(false);
            canvas.Children.Add(tLbl); canvas.Children.Add(fLbl);
            canvas.Children.Add(tDot);
            for (int i = 0; i < 10; i++)
            {
                var d = new Ellipse { Width = 14, Height = 14, Fill = Theme.Surface, StrokeThickness = 3, Cursor = Cursors.Hand };
                d.RenderTransform = new ScaleTransform(1, 1); d.RenderTransformOrigin = new Point(0.5, 0.5);
                dots.Add(d); canvas.Children.Add(d);
            }
            // the live fan marker sits above the handles so it is never hidden by one
            canvas.Children.Add(fHalo); canvas.Children.Add(fDot);
            tipText = K.TB("", 12, Theme.Text, FontWeights.SemiBold);
            tip = new Border { Background = Theme.Raised, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 5), Child = tipText, Opacity = 0, IsHitTestVisible = false };
            canvas.Children.Add(tip);
            SizeChanged += delegate { Draw(); };
            MouseLeftButtonDown += OnDown; MouseMove += OnMove; MouseLeftButtonUp += OnUp;
            MouseLeave += delegate { if (drag < 0) { SetHover(-1); } };
            SetColor(Theme.Current.Accent);
        }

        public void SetColor(Color c)
        {
            stroke.Color = c;
            fill.GradientStops[0].Color = Theme.A(c, 0.30); fill.GradientStops[1].Color = Theme.A(c, 0);
            foreach (var d in dots) d.Stroke = stroke;
            fSoft.Color = Theme.A(c, 0.6); fHaloBrush.Color = Theme.A(c, 0.22);
            if (fDot != null) { ((DropShadowEffect)fDot.Effect).Color = c; fLbl.BorderBrush = stroke; ((TextBlock)fLbl.Child).Foreground = stroke; }
        }

        // temperature pill: filled with the temperature colour; fan pill: outlined in the fan colour
        Border Label(bool temp)
        {
            var t = K.TB("", 11.5, temp ? Brushes.White : (Brush)stroke, FontWeights.Bold); K.Tabular(t);
            return new Border
            {
                Child = t, CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 1, 6, 2), Opacity = 0, IsHitTestVisible = false,
                Background = temp ? (Brush)tBrush : Theme.Surface, BorderBrush = temp ? null : (Brush)stroke, BorderThickness = new Thickness(temp ? 0 : 1.5)
            };
        }
        public void SetCurve(FanCurve c, FanCurve otherFan, int maxRpm) { curve = c; other = otherFan; this.maxRpm = maxRpm; Draw(); }
        // k: 0 = CPU fan, 1 = GPU fan; temp < 0 hides that fan's markers. pct = real speed as % of max.
        public void SetLive(int k, double temp, double pct, int rpm) { lt[k] = temp; ld[k] = pct; lr[k] = rpm; DrawLive(); }

        // the curve as drawn: monotone cubic through the points
        static void Spline(FanCurve c, out List<double> xs, out List<double> ys, out double[] m)
        {
            xs = new List<double>(); ys = new List<double>();
            if (c.Temp[0] > TMin) { xs.Add(TMin); ys.Add(c.Duty[0]); }
            for (int i = 0; i < 10; i++)
            {
                double t = Math.Max(TMin, (double)c.Temp[i]);
                if (xs.Count > 0 && t <= xs[xs.Count - 1]) continue;
                xs.Add(t); ys.Add(c.Duty[i]);
            }
            int n = xs.Count;
            m = new double[n];
            if (n < 2) return;
            var d = new double[n - 1];
            for (int i = 0; i < n - 1; i++) d[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]);
            m[0] = d[0]; m[n - 1] = d[n - 2];
            for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
            for (int i = 0; i < n - 1; i++)
            {
                if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
                double a = m[i] / d[i], b = m[i + 1] / d[i], s = a * a + b * b;
                if (s > 9) { double tau = 3 / Math.Sqrt(s); m[i] = tau * a * d[i]; m[i + 1] = tau * b * d[i]; }
            }
        }

        // value of the drawn curve at temperature t
        public static double Eval(FanCurve c, double t)
        {
            List<double> xs, ys; double[] m;
            Spline(c, out xs, out ys, out m);
            int n = xs.Count;
            if (n == 0) return 0;
            if (t <= xs[0]) return ys[0];
            if (t >= xs[n - 1]) return ys[n - 1];
            int i = 0; while (i < n - 2 && t > xs[i + 1]) i++;
            double h = xs[i + 1] - xs[i], u = (t - xs[i]) / h;
            double h00 = 2 * u * u * u - 3 * u * u + 1, h10 = u * u * u - 2 * u * u + u, h01 = -2 * u * u * u + 3 * u * u, h11 = u * u * u - u * u;
            return Math.Max(0, Math.Min(100, h00 * ys[i] + h10 * h * m[i] + h01 * ys[i + 1] + h11 * h * m[i + 1]));
        }

        double X(double t) { return PadL + (t - TMin) / (TMax - TMin) * Math.Max(1, ActualWidth - PadL - PadR); }
        double Y(double d) { return PadT + (1 - d / 100.0) * Math.Max(1, ActualHeight - PadT - PadB); }
        double T(double x) { return TMin + (x - PadL) / Math.Max(1, ActualWidth - PadL - PadR) * (TMax - TMin); }
        double D(double y) { return (1 - (y - PadT) / Math.Max(1, ActualHeight - PadT - PadB)) * 100; }

        // Duty the EC uses at a temperature: step i covers (Temp[i-1], Temp[i]].
        public static double DutyAt(FanCurve c, double t)
        {
            for (int i = 0; i < 10; i++) if (t <= c.Temp[i]) return c.Duty[i];
            return 100;
        }

        // Smooth monotone curve through the points (Fritsch–Carlson): never overshoots between handles.
        Geometry Steps(FanCurve c, bool closed)
        {
            List<double> xs, ys; double[] m;
            Spline(c, out xs, out ys, out m);
            int n = xs.Count;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double bottom = Y(0);
                var p0 = new Point(X(xs[0]), Y(ys[0]));
                ctx.BeginFigure(closed ? new Point(p0.X, bottom) : p0, closed, closed);
                if (closed) ctx.LineTo(p0, false, false);
                for (int i = 0; i < n - 1; i++)
                {
                    double h = xs[i + 1] - xs[i];
                    var c1 = new Point(X(xs[i] + h / 3), Y(ys[i] + m[i] * h / 3));
                    var c2 = new Point(X(xs[i + 1] - h / 3), Y(ys[i + 1] - m[i + 1] * h / 3));
                    ctx.BezierTo(c1, c2, new Point(X(xs[i + 1]), Y(ys[i + 1])), true, true);
                }
                if (closed) ctx.LineTo(new Point(X(xs[n - 1]), bottom), false, false);
            }
            g.Freeze();
            return g;
        }

        void Draw()
        {
            if (ActualWidth <= 0) return;
            double w = ActualWidth, h = ActualHeight;
            int gi = 0, ai = 0;
            for (int i = 0; i <= 4; i++)
            {
                double y = Math.Round(Y(i * 25)) + 0.5;
                var l = grid[gi++]; l.X1 = PadL; l.X2 = w - PadR; l.Y1 = y; l.Y2 = y;
                var t = axis[ai++]; t.Text = (i * 25) + "%"; Canvas.SetLeft(t, 4); Canvas.SetTop(t, y - 9);
            }
            for (int i = 0; i <= 8; i++)
            {
                double x = Math.Round(X(TMin + i * 10)) + 0.5;
                var l = grid[gi++]; l.X1 = x; l.X2 = x; l.Y1 = PadT; l.Y2 = h - PadB;
                var t = axis[ai++]; t.Text = (TMin + i * 10) + "°"; t.Measure(new Size(100, 100)); Canvas.SetLeft(t, x - t.DesiredSize.Width / 2); Canvas.SetTop(t, h - PadB + 8);
            }
            ghost.Data = other != null ? Steps(other, false) : null;
            area.Data = Steps(curve, true);
            line.Data = Steps(curve, false);
            for (int i = 0; i < 10; i++)
            {
                // the handle sits at the right end of each step (its temperature limit)
                double x = X(Math.Max(TMin, curve.Temp[i])), y = Y(curve.Duty[i]);
                // read-only charts get small solid markers, not draggable-looking handles
                double ds = Locked ? 7 : 14;
                dots[i].Width = dots[i].Height = ds;
                dots[i].Fill = Locked ? (Brush)stroke : Theme.Surface;
                dots[i].StrokeThickness = Locked ? 0 : 3;
                Canvas.SetLeft(dots[i], x - ds / 2); Canvas.SetTop(dots[i], y - ds / 2);
                dots[i].Opacity = i == 9 && !Locked ? 0.5 : 1;
                dots[i].Cursor = i == 9 || Locked ? Cursors.Arrow : Cursors.Hand;
            }
            DrawLive();
        }

        // Temperature lives on the °C axis, fan speed is the ball above it: the dashed line between them
        // shows "at this temperature the fan runs this fast".
        void DrawLive()
        {
            int k = Math.Max(0, Math.Min(1, SelectedFan));
            bool on = ActualWidth > 0 && lt[k] >= 0 && curve != null;
            foreach (UIElement e in new UIElement[] { tDot, fDot, fHalo, vGuide, hGuide, tLbl, fLbl }) e.Opacity = on ? 1 : 0;
            if (!on) return;
            double t = Math.Max(TMin, Math.Min(TMax, lt[k]));
            double x = X(t), yAxis = Y(0), yFan = Y(Math.Max(0, Math.Min(100, ld[k])));
            var tc = Theme.TempColor(lt[k]);
            tBrush.Color = tc; tSoft.Color = Theme.A(tc, 0.7);
            bool light = 0.299 * tc.R + 0.587 * tc.G + 0.114 * tc.B > 160;
            ((TextBlock)tLbl.Child).Foreground = light ? darkText : Brushes.White;
            Canvas.SetLeft(tDot, x - 7); Canvas.SetTop(tDot, yAxis - 7);
            Canvas.SetLeft(fDot, x - 9); Canvas.SetTop(fDot, yFan - 9);
            Canvas.SetLeft(fHalo, x - 19); Canvas.SetTop(fHalo, yFan - 19);
            vGuide.X1 = vGuide.X2 = x; vGuide.Y1 = yFan + 10; vGuide.Y2 = yAxis - 7;
            vGuide.Opacity = yAxis - yFan > 18 ? 1 : 0;
            hGuide.X1 = PadL; hGuide.X2 = x - 19; hGuide.Y1 = hGuide.Y2 = Math.Round(yFan) + 0.5;
            hGuide.Opacity = x - PadL > 20 ? 1 : 0;
            // °C pill sits on the temperature axis, covering the tick labels under the dot
            var tt = (TextBlock)tLbl.Child; tt.Text = Math.Round(lt[k]) + "°C";
            tLbl.Measure(new Size(200, 50));
            double lx = Math.Max(0, Math.Min(ActualWidth - tLbl.DesiredSize.Width, x - tLbl.DesiredSize.Width / 2));
            Canvas.SetLeft(tLbl, lx); Canvas.SetTop(tLbl, yAxis + 9);
            // rpm pill beside the ball (flips left near the right edge, nudged off the axis)
            var ft = (TextBlock)fLbl.Child; ft.Text = string.Format(K.EN, "{0:N0} rpm · {1:0}%", lr[k], ld[k]);
            fLbl.Measure(new Size(200, 50));
            bool right = x + 22 + fLbl.DesiredSize.Width < ActualWidth - 4;
            Canvas.SetLeft(fLbl, right ? x + 22 : x - 22 - fLbl.DesiredSize.Width);
            Canvas.SetTop(fLbl, Math.Max(0, Math.Min(yAxis - fLbl.DesiredSize.Height - 12, yFan - fLbl.DesiredSize.Height / 2)));
        }

        int Hit(Point p)
        {
            int best = -1; double bd = 18 * 18;
            for (int i = 0; i < 9; i++)
            {
                double dx = X(Math.Max(TMin, curve.Temp[i])) - p.X, dy = Y(curve.Duty[i]) - p.Y, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        void SetHover(int i)
        {
            if (i == hover) return;
            if (hover >= 0) K.Pop((ScaleTransform)dots[hover].RenderTransform, 1, 220, K.OutCubic);
            hover = i;
            if (i >= 0) { K.Pop((ScaleTransform)dots[i].RenderTransform, 1.45, 320, K.OutBack); ShowTip(i); }
            else tip.BeginAnimation(OpacityProperty, K.Anim(0, 160, K.OutCubic));
        }

        void ShowTip(int i)
        {
            int rpm = (int)Math.Round(maxRpm * curve.Duty[i] / 100.0);
            int from = i == 0 ? (int)TMin : curve.Temp[i - 1] + 1;
            tipText.Text = string.Format(K.EN, "{0}–{1}°C  ·  {2}%{3}", from, curve.Temp[i], curve.Duty[i], maxRpm > 0 ? string.Format(K.EN, "  ·  {0:N0} rpm", rpm) : "");
            tip.Measure(new Size(400, 100));
            double x = X(Math.Max(TMin, curve.Temp[i])), y = Y(curve.Duty[i]);
            Canvas.SetLeft(tip, Math.Max(0, Math.Min(ActualWidth - tip.DesiredSize.Width, x - tip.DesiredSize.Width / 2)));
            Canvas.SetTop(tip, y - 40 < 0 ? y + 14 : y - 40);
            tip.BeginAnimation(OpacityProperty, K.Anim(1, 140, K.OutCubic));
        }

        void OnDown(object s, MouseButtonEventArgs e)
        {
            if (Locked) return;
            int i = Hit(e.GetPosition(canvas));
            if (i < 0) return;
            drag = i; CaptureMouse(); e.Handled = true;
        }
        void OnMove(object s, MouseEventArgs e)
        {
            var p = e.GetPosition(canvas);
            if (drag < 0) { if (!Locked) SetHover(Hit(p)); return; }
            int i = drag;
            int lo = i == 0 ? (int)TMin : curve.Temp[i - 1] + 1, hi = curve.Temp[i + 1] - 1;
            int t = (int)Math.Round(T(p.X)); t = Math.Max(lo, Math.Min(hi, t));
            int dmin = i == 0 ? 0 : curve.Duty[i - 1], dmax = curve.Duty[i + 1];
            int d = (int)Math.Round(D(p.Y)); d = Math.Max(dmin, Math.Min(dmax, d));
            if (t >= 85 && d < 70) d = Math.Min(dmax, 70);
            if (curve.Temp[i] == t && curve.Duty[i] == d) return;
            curve.Temp[i] = (byte)t; curve.Duty[i] = (byte)d;
            Draw(); ShowTip(i);
            if (Edited != null) Edited();
        }
        void OnUp(object s, MouseButtonEventArgs e)
        {
            if (drag < 0) return;
            drag = -1; ReleaseMouseCapture();
            SetHover(Hit(e.GetPosition(canvas)));
        }
    }

    // A complete curve editor for one power-mode profile: fan tabs, chart, legend, presets, save.
    // The Cooling page uses one for the running mode and one (inside "Other power modes") for the rest.
    class CurveCard : Border
    {
        public static readonly int[] ProfileIdx = { 0, 3, 2 };              // Quiet, Balanced (OEM "entertainment"), Performance
        public static readonly string[] ProfileName = { "Quiet", "Balanced", "Performance" };

        readonly MainWindow w; readonly bool others;
        readonly TextBlock title, note, liveText;
        readonly Segmented fanSeg, profileSeg;
        readonly CurveEditor editor = new CurveEditor();
        readonly Border applyBtn, revertBtn; readonly Toggle linkT;
        FanCurve[] work; int fan, prof = -1; bool suppress;
        public bool Dirty { get; private set; }
        public int Profile { get { return prof; } }
        public int ActiveProfile = -1;   // profile index (0/2/3) of the running power mode

        public CurveCard(MainWindow w, bool otherModes)
        {
            this.w = w; others = otherModes;
            CornerRadius = new CornerRadius(20); Background = Theme.Surface; BorderBrush = Theme.Hairline; BorderThickness = new Thickness(1); Padding = new Thickness(22);
            K.HoverEdge(this);
            var cc = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title = K.TB("", 15, Theme.Text, FontWeights.SemiBold);
            head.Children.Add(title);
            fanSeg = new Segmented(new[] { "CPU fan", "GPU fan" }, 0, 36); fanSeg.Width = 230;
            fanSeg.Changed += i => { fan = i; Show(); };
            Grid.SetColumn(fanSeg, 1); head.Children.Add(fanSeg);
            cc.Children.Add(head);
            profileSeg = new Segmented(ProfileName, 1, 34); profileSeg.Width = 380; profileSeg.HorizontalAlignment = HorizontalAlignment.Left;
            profileSeg.Changed += i => Pick(i);
            if (others)
            {
                var pr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                var prl = K.TB("Mode", 12.5, Theme.Text3, FontWeights.SemiBold); prl.Margin = new Thickness(0, 0, 12, 0);
                pr.Children.Add(prl); pr.Children.Add(profileSeg);
                cc.Children.Add(pr);
            }
            var hint = K.TB(others ? "These curves are only used when that mode is running and its fans are set to Custom curve."
                                   : "Drag a point to set the fan speed for that temperature. The last point is fixed at 100 °C = full speed for safety.",
                            12.5, Theme.Text3, FontWeights.Medium);
            hint.Margin = new Thickness(0, 10, 0, 0); hint.TextWrapping = TextWrapping.Wrap; hint.TextTrimming = TextTrimming.None;
            cc.Children.Add(hint);
            editor.Margin = new Thickness(0, 16, 0, 0);
            editor.Edited += () => { if (linkT.IsOn) work[1 - fan] = work[fan].Clone(); SetDirty(true); };
            cc.Children.Add(editor);
            cc.Children.Add(FansPage.Legend(!others));
            liveText = K.TB("", 12.5, Theme.Text2, FontWeights.Medium);
            linkT = new Toggle(Settings.Bool("fanLink", false));
            linkT.Changed += on => { Settings.Set("fanLink", on); if (on) { work[1 - fan] = work[fan].Clone(); SetDirty(true); Show(); } };
            var link = new StackPanel { Orientation = Orientation.Horizontal };
            var lt = K.TB("Same curve for both fans", 12.5, Theme.Text2, FontWeights.Medium); lt.Margin = new Thickness(0, 0, 10, 0);
            link.Children.Add(lt); link.Children.Add(linkT);
            var lr = UI.Row(liveText, link); lr.Margin = new Thickness(0, 12, 0, 0);
            cc.Children.Add(lr);
            cc.Children.Add(UI.Divider());
            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pre = new StackPanel { Orientation = Orientation.Horizontal };
            var prt = K.TB("Start from", 12.5, Theme.Text3, FontWeights.SemiBold); prt.Margin = new Thickness(0, 0, 10, 0);
            pre.Children.Add(prt);
            pre.Children.Add(Preset("Silent", new byte[] { 50, 55, 60, 65, 70, 75, 80, 85, 92, 100 }, new byte[] { 0, 20, 30, 35, 42, 50, 60, 70, 85, 100 }));
            pre.Children.Add(Preset("Default", FanCurve.DefTemp, FanCurve.DefDuty));
            pre.Children.Add(Preset("Cool", new byte[] { 35, 40, 45, 50, 55, 60, 65, 70, 80, 100 }, new byte[] { 25, 40, 50, 55, 62, 70, 78, 86, 95, 100 }));
            actions.Children.Add(pre);
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            revertBtn = K.PillBtn("Revert", Theme.Raised, Theme.Text, 38, () => { Load(); w.Island.Show("", "Changes discarded", null); });
            revertBtn.Margin = new Thickness(0, 0, 10, 0);
            applyBtn = K.PillBtn("Save & apply", Theme.AccentFill, Theme.OnAccent, 38, Apply);
            btns.Children.Add(revertBtn); btns.Children.Add(applyBtn);
            Grid.SetColumn(btns, 1); actions.Children.Add(btns);
            cc.Children.Add(actions);
            note = K.TB("", 12, Theme.Text3, FontWeights.Medium); note.Margin = new Thickness(0, 12, 0, 0); note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.None;
            cc.Children.Add(note);
            Child = cc;
            Theme.Changed += delegate { Show(); };
            SetDirty(false);
        }

        Border Preset(string name, byte[] t, byte[] d)
        {
            var b = K.PillBtn(name, Theme.Raised, Theme.Text2, 34, () =>
            {
                if (work == null) return;
                var c = new FanCurve(); Array.Copy(t, c.Temp, 10); Array.Copy(d, c.Duty, 10); c.Sanitize();
                work[fan] = c; if (linkT.IsOn) work[1 - fan] = c.Clone();
                SetDirty(true); Show();
            });
            b.Margin = new Thickness(0, 0, 8, 0);
            return b;
        }

        // Switch to profile index i (0 Quiet, 1 Balanced, 2 Performance); asks before dropping edits.
        public void Pick(int i)
        {
            if (suppress) return;
            if (i == prof && work != null) return;
            if (Dirty)
            {
                int target = i;
                suppress = true; profileSeg.Select(prof, false); suppress = false;
                w.Confirm("Discard changes?", "The " + ProfileName[prof] + " curve you edited hasn't been saved.", "Discard", ok =>
                {
                    if (!ok) return;
                    Dirty = false; SetProfile(target);
                });
                return;
            }
            SetProfile(i);
        }

        public void SetProfile(int i)
        {
            prof = i;
            suppress = true; profileSeg.Select(i, false); suppress = false;
            Load();
        }

        public void Load()
        {
            if (prof < 0) return;
            var s = FanProfiles.Load()[ProfileIdx[prof]];
            work = new[] { s[0].Clone(), s[1].Clone() };
            SetDirty(false);
            Show();
        }

        void SetDirty(bool d)
        {
            Dirty = d;
            applyBtn.Opacity = d ? 1 : 0.45; applyBtn.IsHitTestVisible = d;
            revertBtn.Opacity = d ? 1 : 0.45; revertBtn.IsHitTestVisible = d;
            UpdateTexts();
        }

        bool Running { get { return prof >= 0 && ProfileIdx[prof] == ActiveProfile; } }

        public void UpdateTexts()
        {
            if (prof < 0) return;
            string p = ProfileName[prof];
            bool customOn = Hw.Fan.Mode == 6;
            title.Text = others ? "Custom curve for " + p : "Your fan curve for " + p + (Running && customOn ? " · running now" : "");
            if (Running && customOn) note.Text = "Saving writes the curve to the fan controller right away. It is also re-applied every time you switch to " + p + ".";
            else if (Running) note.Text = "Used when this mode's fans are set to Custom curve.";
            else note.Text = "Saved for " + p + ". Used when you switch to " + p + " and its fans are set to Custom curve.";
            if (Dirty) note.Text = "Unsaved changes.  " + note.Text;
        }

        void Show()
        {
            if (work == null) return;
            editor.SelectedFan = fan;
            editor.SetColor(fan == 0 ? Theme.Current.Accent : Theme.Current.Orange);
            editor.SetCurve(work[fan], work[1 - fan], Hw.Fan.MaxRpm[fan]);
            UpdateTexts();
            Live(Hw.Last);
        }

        void Apply()
        {
            if (work == null) return;
            var cpu = work[0].Clone(); var gpu = work[1].Clone(); int p = prof;
            applyBtn.IsHitTestVisible = false;
            Hw.SaveCurves(ProfileIdx[p], cpu, gpu, r => w.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (r == "applied") w.Island.Show("", "Fan curve applied", Theme.Green);
                else if (r == "saved") w.Island.Show("", "Fan curve saved for " + ProfileName[p], Theme.Accent);
                else w.Island.Show("", r == "saved-verify-failed" ? "Saved, but the fan controller didn't confirm it" : "Couldn't save: " + r, Theme.Orange);
                if (r.StartsWith("error")) { SetDirty(true); return; }
                Load();
            })));
        }

        public void Live(Snapshot s)
        {
            if (s == null || !s.Ok || work == null) return;
            bool show = Running;
            for (int k = 0; k < 2; k++)
            {
                int mx = Hw.Fan.MaxRpm[k];
                double t = k == 0 ? s.CpuTemp : (UI.GpuAsleep(s) ? -1 : s.GpuTemp);
                double pct = mx > 0 ? Math.Min(100, s.Fans[k].Rpm * 100.0 / mx) : s.Fans[k].Duty;
                editor.SetLive(k, show ? t : -1, pct, s.Fans[k].Rpm);
            }
            liveText.Text = !show ? "This mode isn't running, so there are no live markers." : FansPage.LiveLine(s);
        }
    }

    // Cooling page, top to bottom:
    //   1. Now            - one sentence about the current power mode and fan behaviour + live fans
    //   2. Fan behaviour  - Automatic / Custom curve / Maximum for the ACTIVE power mode
    //   3. Details        - what the chosen behaviour does (BIOS curve, full speed, or the curve editor)
    //   4. Other modes    - a separate editor for the custom curves of the other power modes
    class FansPage : IPage
    {
        readonly MainWindow w;
        readonly StackPanel stack = new StackPanel { Margin = new Thickness(36, 0, 36, 36) };
        readonly ScrollViewer view;

        readonly TextBlock nowTitle, nowSub; readonly Border nowIcon; readonly TextBlock nowGlyph;
        readonly TextBlock[] liveFan = new TextBlock[2];
        readonly TextBlock behaviourTitle, behaviourNote;
        readonly List<Border> optCards = new List<Border>(); readonly List<Border> optDots = new List<Border>();
        static readonly int[] FanModes = { 0, 6, 1 };                // Automatic, Custom, Maximum
        readonly Border autoCard, maxCard;
        readonly TextBlock autoTitle, autoCpu, autoGpu, autoFoot, maxText, autoLive, statusText;
        readonly CurveEditor runEditor = new CurveEditor { Locked = true, Height = 220 };
        readonly CurveCard main, other;
        readonly Border advRow; readonly TextBlock advChevron, advSub; bool advOpen;
        int activeProf = -1;

        public string Title { get { return "Cooling"; } }
        public string Subtitle { get { return "How the fans behave in each power mode"; } }
        public FrameworkElement View { get { return view; } }
        public Panel StaggerRoot { get { return stack; } }

        public FansPage(MainWindow w)
        {
            this.w = w;
            view = UI.Scroll(stack);

            // ---------------- 1. now
            var ng = new Grid();
            ng.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ng.ColumnDefinitions.Add(new ColumnDefinition());
            ng.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nowIcon = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(16), Background = Theme.AccentSoft, VerticalAlignment = VerticalAlignment.Center };
            nowGlyph = K.Glyph("", 22, Theme.Accent); nowIcon.Child = nowGlyph;
            ng.Children.Add(nowIcon);
            var nt = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            nowTitle = K.TB("–", 19, Theme.Text, FontWeights.Bold); nowTitle.FontFamily = K.Display;
            nowSub = K.TB("", 12.5, Theme.Text2, FontWeights.Medium); nowSub.Margin = new Thickness(0, 3, 0, 0); nowSub.TextWrapping = TextWrapping.Wrap; nowSub.TextTrimming = TextTrimming.None;
            nt.Children.Add(nowTitle); nt.Children.Add(nowSub);
            Grid.SetColumn(nt, 1); ng.Children.Add(nt);
            var lives = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string[] ln = { "CPU fan", "GPU fan" };
            for (int i = 0; i < 2; i++)
            {
                var b = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 24, 0, 0, 0) };
                b.Children.Add(K.TB(ln[i], 11.5, Theme.Text3, FontWeights.SemiBold));
                liveFan[i] = K.TB("–", 15, Theme.Text, FontWeights.SemiBold); K.Tabular(liveFan[i]);
                b.Children.Add(liveFan[i]);
                lives.Children.Add(b);
            }
            Grid.SetColumn(lives, 2); ng.Children.Add(lives);
            stack.Children.Add(K.Card(ng, new Thickness(22)));

            // ---------------- 2. behaviour for the active mode
            var bc = new StackPanel();
            behaviourTitle = K.TB("How should the fans behave?", 15, Theme.Text, FontWeights.SemiBold);
            bc.Children.Add(behaviourTitle);
            var opts = UI.Columns(1, 1, 1); opts.Margin = new Thickness(0, 14, 0, 0);
            UI.Put(opts, Option(0, "", "Automatic", "The BIOS picks the speed from the temperature. Each power mode has its own built-in curve."), 0);
            UI.Put(opts, Option(1, "", "Custom curve", "You decide how fast the fans spin at each temperature."), 1);
            UI.Put(opts, Option(2, "", "Maximum", "Both fans at full speed. Coolest, but loud."), 2);
            bc.Children.Add(opts);
            behaviourNote = K.TB("", 12.5, Theme.Text3, FontWeights.Medium); behaviourNote.Margin = new Thickness(0, 14, 0, 0); behaviourNote.TextWrapping = TextWrapping.Wrap; behaviourNote.TextTrimming = TextTrimming.None;
            bc.Children.Add(behaviourNote);
            var bcard = K.Card(bc, new Thickness(22)); bcard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(bcard);

            // ---------------- 3a. automatic
            var ac = new StackPanel();
            autoTitle = K.TB("", 15, Theme.Text, FontWeights.SemiBold); autoTitle.VerticalAlignment = VerticalAlignment.Center;
            autoFanSeg = new Segmented(new[] { "CPU fan", "GPU fan" }, 0, 36); autoFanSeg.Width = 230;
            autoFanSeg.Changed += i => { autoFan = i; ShowAutoCurve(); UpdateLive(Hw.Last); };
            ac.Children.Add(UI.Row(autoTitle, autoFanSeg));
            autoCpu = Bullet(ac, Theme.Accent); autoGpu = Bullet(ac, Theme.Orange);
            runEditor.Margin = new Thickness(0, 14, 0, 0);
            ac.Children.Add(runEditor);
            ac.Children.Add(Legend(true));
            autoLive = K.TB("", 12.5, Theme.Text2, FontWeights.Medium); autoLive.Margin = new Thickness(0, 10, 0, 0);
            ac.Children.Add(autoLive);
            autoFoot = K.TB("", 12, Theme.Text3, FontWeights.Medium); autoFoot.Margin = new Thickness(0, 8, 0, 0); autoFoot.TextWrapping = TextWrapping.Wrap; autoFoot.TextTrimming = TextTrimming.None;
            ac.Children.Add(autoFoot);
            autoCard = K.Card(ac, new Thickness(22)); autoCard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(autoCard);

            // ---------------- 3b. maximum
            var mx = new StackPanel();
            mx.Children.Add(K.TB("Maximum speed", 15, Theme.Text, FontWeights.SemiBold));
            maxText = K.TB("", 13, Theme.Text2, FontWeights.Medium); maxText.Margin = new Thickness(0, 8, 0, 0); maxText.TextWrapping = TextWrapping.Wrap; maxText.TextTrimming = TextTrimming.None;
            mx.Children.Add(maxText);
            maxCard = K.Card(mx, new Thickness(22)); maxCard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(maxCard);

            // ---------------- 3c. custom curve of the running mode
            main = new CurveCard(w, false) { Margin = new Thickness(0, 18, 0, 0) };
            stack.Children.Add(main);

            // ---------------- 4. other power modes (expander + its own editor right below)
            var ar = new Grid();
            ar.ColumnDefinitions.Add(new ColumnDefinition()); ar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var al = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            al.Children.Add(K.TB("Curves for other power modes", 14, Theme.Text, FontWeights.SemiBold));
            advSub = K.TB("Prepare the custom curve of a mode before you switch to it", 12.5, Theme.Text3, FontWeights.Medium); advSub.Margin = new Thickness(0, 3, 0, 0);
            al.Children.Add(advSub);
            ar.Children.Add(al);
            advChevron = K.Glyph("", 14, Theme.Text2); Grid.SetColumn(advChevron, 1);
            advChevron.RenderTransform = new RotateTransform(0); advChevron.RenderTransformOrigin = new Point(0.5, 0.5);
            ar.Children.Add(advChevron);
            advRow = new Border { CornerRadius = new CornerRadius(16), Background = Theme.Surface, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1), Padding = new Thickness(22, 16, 22, 16), Child = ar, Cursor = Cursors.Hand, Margin = new Thickness(0, 18, 0, 0) };
            K.Pressable(advRow, ToggleAdvanced, true);
            stack.Children.Add(advRow);
            other = new CurveCard(w, true) { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            stack.Children.Add(other);

            statusText = K.TB("", 12, Theme.Text3, FontWeights.Medium); statusText.Margin = new Thickness(4, 14, 0, 0); statusText.TextWrapping = TextWrapping.Wrap; statusText.TextTrimming = TextTrimming.None;
            stack.Children.Add(statusText);

            Hw.FanChanged += f => w.Dispatcher.BeginInvoke(new Action(() => OnFan(f)));
            autoCard.Visibility = maxCard.Visibility = main.Visibility = Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- building blocks
        Border Option(int idx, string glyph, string name, string desc)
        {
            var sp = new StackPanel { Margin = new Thickness(18) };
            var top = new Grid();
            var ic = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(13), Background = Theme.AccentSoft, HorizontalAlignment = HorizontalAlignment.Left };
            ic.Child = K.Glyph(glyph, 18, Theme.Accent);
            top.Children.Add(ic);
            var ring = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), BorderBrush = Theme.Text3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Theme.AccentFill, RenderTransform = new ScaleTransform(0, 0), RenderTransformOrigin = new Point(0.5, 0.5) };
            ring.Child = dot; top.Children.Add(ring);
            sp.Children.Add(top);
            var n = K.TB(name, 16, Theme.Text, FontWeights.Bold); n.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(n);
            var d = K.TB(desc, 12.5, Theme.Text2, FontWeights.Normal); d.TextWrapping = TextWrapping.Wrap; d.TextTrimming = TextTrimming.None; d.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(d);
            var card = new Border { CornerRadius = new CornerRadius(16), Background = Theme.Raised, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1.5), Child = sp, Cursor = Cursors.Hand };
            K.Pressable(card, () => ChooseBehaviour(idx), true);
            optCards.Add(card); optDots.Add(dot);
            return card;
        }

        static TextBlock Bullet(Panel host, Brush dotColor)
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = dotColor, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
            var t = K.TB("", 13, Theme.Text2, FontWeights.Medium); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
            Grid.SetColumn(t, 1); g.Children.Add(t);
            host.Children.Add(g);
            return t;
        }

        // how to read the chart; `live` adds the two marker kinds
        public static FrameworkElement Legend(bool live)
        {
            var wp = new WrapPanel { Margin = new Thickness(44, 8, 0, 0) };
            Action<UIElement, string> add = (e, s) =>
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
                var fe = e as FrameworkElement; if (fe != null) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(0, 0, 6, 0); }
                sp.Children.Add(e); sp.Children.Add(K.TB(s, 11.5, Theme.Text3, FontWeights.SemiBold));
                wp.Children.Add(sp);
            };
            add(K.TB("→", 11.5, Theme.Text3, FontWeights.SemiBold), "temperature (°C)");
            add(K.TB("↑", 11.5, Theme.Text3, FontWeights.SemiBold), "fan speed (% of max)");
            add(new Border { Width = 18, Height = 3, CornerRadius = new CornerRadius(2), Background = Theme.Text2 }, "selected fan");
            add(new System.Windows.Shapes.Line { X1 = 0, X2 = 18, Y1 = 1.5, Y2 = 1.5, Stroke = Theme.Text3, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 3, 3 } }, "other fan");
            if (live)
            {
                var hot = new LinearGradientBrush(Theme.TempColor(40), Theme.TempColor(85), 0); hot.Freeze();
                add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = hot }, "temperature now (on the °C axis, colored by heat)");
                add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Theme.Accent, BorderBrush = Brushes.White, BorderThickness = new Thickness(2.5) }, "fan speed now (solid ball with halo)");
            }
            return wp;
        }

        public static string LiveLine(Snapshot s)
        {
            int m0 = Hw.Fan.MaxRpm[0], m1 = Hw.Fan.MaxRpm[1];
            string cpu = string.Format(K.EN, "CPU {0}°C → fan {1:N0} rpm ({2:0}%)", s.CpuTemp, s.Fans[0].Rpm, m0 > 0 ? Math.Min(100, s.Fans[0].Rpm * 100.0 / m0) : s.Fans[0].Duty);
            string gpu = UI.GpuAsleep(s) ? "GPU asleep (fan off)" : string.Format(K.EN, "GPU {0}°C → fan {1:N0} rpm ({2:0}%)", s.GpuTemp, s.Fans[1].Rpm, m1 > 0 ? Math.Min(100, s.Fans[1].Rpm * 100.0 / m1) : s.Fans[1].Duty);
            return "Now:  " + cpu + "   ·   " + gpu;
        }

        // Plain-language summary of a fan table: when the fan starts, its lowest and highest speed.
        public static string Describe(FanCurve c, int max)
        {
            int first = -1;
            for (int i = 0; i < 10; i++) if (c.Duty[i] > 0) { first = i; break; }
            if (first < 0) return "stays off";
            int startT = first == 0 ? 0 : c.Temp[first - 1] + 1;
            int lo = c.Duty[first], hi = c.Duty[9], hiT = c.Temp[9];
            for (int i = 9; i >= 0; i--) if (c.Duty[i] == hi) hiT = i == 0 ? 0 : c.Temp[i - 1] + 1;
            Func<int, string> rpm = d => string.Format(K.EN, "{0:N0} rpm ({1}%)", Math.Round(max * d / 100.0), d);
            string s = (first > 0 ? "off below " + startT + " °C, then " : "") + rpm(lo);
            if (hi != lo) s += ", rising to " + rpm(hi) + " from " + hiT + " °C";
            else s += " and it stays there up to " + c.Temp[9] + " °C — this mode caps the fan";
            return s;
        }

        // ---------------------------------------------------------------- behaviour
        void ChooseBehaviour(int idx)
        {
            var f = Hw.Fan;
            if (f.PowerMode == 1) { w.Island.Show("", "Battery Saver always uses automatic fans", Theme.Yellow); return; }
            int mode = FanModes[idx];
            if (mode == f.Mode) return;
            Action go = () =>
            {
                Hw.SetFanMode(mode);
                w.Island.Show("", idx == 0 ? "Fans on automatic" : idx == 1 ? "Custom fan curve on" : "Fans at maximum", null);
            };
            if (main.Dirty && mode != 6)
                w.Confirm("Discard curve changes?", "Your edited fan curve hasn't been saved.", "Discard", ok => { if (ok) { main.Load(); go(); } });
            else go();
        }

        void ToggleAdvanced()
        {
            if (advOpen && other.Dirty)
            {
                w.Confirm("Discard changes?", "The " + CurveCard.ProfileName[other.Profile] + " curve you edited hasn't been saved.", "Discard", ok => { if (ok) { other.Load(); SetAdvanced(false); } });
                return;
            }
            SetAdvanced(!advOpen);
        }

        void SetAdvanced(bool open)
        {
            advOpen = open;
            advChevron.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, K.Anim(open ? 180 : 0, 320, K.OutBack));
            if (open)
            {
                // start on a mode that isn't running (the running one is edited above)
                int pick = 0;
                for (int i = 0; i < 3; i++) if (CurveCard.ProfileIdx[i] != activeProf) { pick = i; break; }
                if (other.Profile < 0 || CurveCard.ProfileIdx[other.Profile] == activeProf) other.SetProfile(pick); else other.Load();
            }
            ShowCard(other, open);
            if (open) other.BringIntoView();
        }

        static void ShowCard(UIElement e, bool on)
        {
            var target = on ? Visibility.Visible : Visibility.Collapsed;
            if (e.Visibility == target) return;
            e.Visibility = target;
            if (on && K.AnimScale > 0)
            {
                var tt = new TranslateTransform(0, 14); e.RenderTransform = tt; e.Opacity = 0;
                e.BeginAnimation(UIElement.OpacityProperty, K.Anim(1, 300, K.OutCubic));
                tt.BeginAnimation(TranslateTransform.YProperty, K.Anim(0, 420, K.OutQuint));
            }
        }

        // ---------------------------------------------------------------- state from the hardware
        public void ThemeChanged() { OnFan(Hw.Fan); }

        void OnFan(FanState f)
        {
            var pm = Modes.Of(f.PowerMode);
            string pmName = pm != null ? pm.Name : "this mode";
            bool saver = f.PowerMode == 1;
            int newActive = FanProfiles.ForPowerMode(f.PowerMode);
            if (newActive != activeProf)
            {
                activeProf = newActive;
                main.ActiveProfile = other.ActiveProfile = activeProf;
                int pi = Array.IndexOf(CurveCard.ProfileIdx, activeProf);
                if (pi >= 0 && !main.Dirty) main.SetProfile(pi);
            }

            string beh = f.Mode == 1 ? "fans at maximum" : f.Mode == 6 ? "custom fan curve" : f.Mode == 0 ? "automatic fans" : "OEM fan mode " + f.Mode;
            nowTitle.Text = pmName + " mode · " + beh;
            nowSub.Text = saver ? "Battery Saver manages the fans by itself." : "Change the power mode on the Power Mode page; each mode remembers its own fan behaviour.";
            if (pm != null) { var c = pm.Col(); nowGlyph.Text = pm.Glyph; nowGlyph.Foreground = K.Solid(c); nowIcon.Background = K.Solid(Theme.A(c, 0.16)); }

            behaviourTitle.Text = saver ? "Fans in Battery Saver" : "How should the fans behave in " + pmName + "?";
            int mi = Array.IndexOf(FanModes, f.Mode);
            for (int i = 0; i < optCards.Count; i++)
            {
                bool on = i == mi;
                optCards[i].BorderBrush = on ? Theme.Accent : Theme.Hairline;
                optCards[i].Opacity = saver && i != 0 ? 0.4 : 1;
                optCards[i].IsHitTestVisible = !saver;
                K.Pop((ScaleTransform)optDots[i].RenderTransform, on ? 1 : 0, on ? 480 : 200, on ? K.Elastic : K.OutCubic);
            }
            behaviourNote.Text = saver ? "Battery Saver always uses the automatic fan curve; switch power mode to change it."
                : "Applies to " + pmName + " only and is remembered for it." + (f.Mode == 1 ? " Maximum is temporary: the next power-mode change goes back to the saved choice." : "");

            if (f.Ok && f.Ec[0] != null)
            {
                autoTitle.Text = "What Automatic does in " + pmName;
                autoCpu.Text = "CPU fan: " + Describe(f.Ec[0], f.MaxRpm[0]) + ".";
                autoGpu.Text = "GPU fan: " + Describe(f.Ec[1], f.MaxRpm[1]) + ".";
                autoFoot.Text = string.Format(K.EN, "Read from the fan controller. The fans can physically reach {0:N0} / {1:N0} rpm; the built-in curve of each power mode decides how far they go.", f.MaxRpm[0], f.MaxRpm[1]);
                ShowAutoCurve();
                maxText.Text = string.Format(K.EN, "Both fans run at full speed whatever the temperature: CPU fan up to {0:N0} rpm, GPU fan up to {1:N0} rpm. Use it for heavy loads or to cool down fast; it is loud.", f.MaxRpm[0], f.MaxRpm[1]);
                statusText.Text = string.Format(K.EN, "Custom curves are stored in {0} (the original is backed up in the ControlHub data folder).", FanProfiles.XmlPath);
            }
            bool custom = f.Mode == 6 && !saver;
            ShowCard(autoCard, f.Ok && !custom && f.Mode != 1);
            ShowCard(maxCard, f.Ok && f.Mode == 1 && !saver);
            ShowCard(main, custom);
            advRow.Visibility = saver ? Visibility.Collapsed : Visibility.Visible;
            main.UpdateTexts(); other.UpdateTexts();
            UpdateLive(Hw.Last);
        }

        Segmented autoFanSeg; int autoFan;
        void ShowAutoCurve()
        {
            var f = Hw.Fan;
            if (f == null || !f.Ok || f.Ec[0] == null) return;
            int k = autoFan;
            runEditor.SelectedFan = k;
            runEditor.SetColor(k == 0 ? Theme.Current.Accent : Theme.Current.Orange);
            runEditor.SetCurve(f.Ec[k], f.Ec[1 - k], f.MaxRpm[k]);
        }

        void UpdateLive(Snapshot s)
        {
            if (s == null || !s.Ok) return;
            for (int i = 0; i < 2; i++)
            {
                bool asleep = i == 1 && UI.GpuAsleep(s);
                liveFan[i].Text = asleep ? "off · GPU asleep" : string.Format(K.EN, "{0:N0} rpm · {1}°C", s.Fans[i].Rpm, i == 0 ? s.CpuTemp : s.GpuTemp);
            }
            if (autoCard.Visibility == Visibility.Visible)
            {
                for (int k = 0; k < 2; k++)
                {
                    int mx = Hw.Fan.MaxRpm[k];
                    double t = k == 0 ? s.CpuTemp : (UI.GpuAsleep(s) ? -1 : s.GpuTemp);
                    runEditor.SetLive(k, t, mx > 0 ? Math.Min(100, s.Fans[k].Rpm * 100.0 / mx) : s.Fans[k].Duty, s.Fans[k].Rpm);
                }
                autoLive.Text = LiveLine(s);
            }
            if (main.Visibility == Visibility.Visible) main.Live(s);
            if (other.Visibility == Visibility.Visible) other.Live(s);
        }

        public void Update(Snapshot s) { if (view.IsVisible) UpdateLive(s); }

        public void OnShown()
        {
            if (!main.Dirty) main.Load();
            Hw.RefreshFans();
        }

        // IPC helpers for testing
        public string Ipc(string arg)
        {
            var a = arg.Split(' ');
            if (a[0] == "state")
            {
                var f = Hw.Fan;
                return "mode=" + f.Mode + " power=" + f.PowerMode + " max=" + f.MaxRpm[0] + "/" + f.MaxRpm[1] + " table=0x" + f.EcProfileId.ToString("X2")
                    + " | ec cpu " + (f.Ec[0] != null ? f.Ec[0].Describe() : "-") + " | ec gpu " + (f.Ec[1] != null ? f.Ec[1].Describe() : "-");
            }
            if (a[0] == "mode") { Hw.SetFanMode(int.Parse(a[1])); return "ok"; }
            if (a[0] == "refresh") { Hw.RefreshFans(); return "ok"; }
            if (a[0] == "advanced") { ToggleAdvanced(); return "advanced=" + advOpen; }
            if (a[0] == "other" && a.Length > 1) { other.Pick(int.Parse(a[1])); return "other=" + other.Profile; }
            if (a[0] == "resave")
            {
                int p = FanProfiles.ForPowerMode(Hw.Last.Mode);
                if (p < 0) return "no profile for this power mode";
                var c = FanProfiles.Load()[p];
                Hw.SaveCurves(p, c[0], c[1], r => Log.Write("resave result: " + r));
                return "ok profile " + p;
            }
            return "fan state | mode <0|1|6> | refresh | advanced | other <0-2> | resave";
        }
    }
}
