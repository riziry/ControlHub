// Theme, motion helpers and styled controls (same design language as DynamicIsland).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ControlHub
{
    // ===================================================================== theme
    class Palette
    {
        public string Key, Label;
        public Color Bg, Surface, Raised, Hover, Text, Accent, Accent2, Green, Orange, Red, Yellow;
        public Palette(string key, string label, string bg, string surface, string raised, string hover, string text, string accent, string accent2, string green, string orange, string red, string yellow)
        {
            Key = key; Label = label;
            Bg = Theme.Hex(bg); Surface = Theme.Hex(surface); Raised = Theme.Hex(raised); Hover = Theme.Hex(hover);
            Text = Theme.Hex(text); Accent = Theme.Hex(accent); Accent2 = Theme.Hex(accent2);
            Green = Theme.Hex(green); Orange = Theme.Hex(orange); Red = Theme.Hex(red); Yellow = Theme.Hex(yellow);
        }
    }

    static class Theme
    {
        public static readonly List<Palette> Palettes = new List<Palette>
        {
            new Palette("oled", "OLED", "#000000", "#0b0b0d", "#151518", "#202025", "#f5f5f7", "#0a84ff", "#5e5ce6", "#30d158", "#ff9f0a", "#ff453a", "#ffd60a"),
            new Palette("graphite", "Graphite", "#0e0e10", "#18181b", "#232327", "#2f2f34", "#f2f2f7", "#64d2ff", "#0a84ff", "#32d74b", "#ff9f0a", "#ff453a", "#ffd60a"),
            new Palette("tokyonight", "Tokyo Night", "#101018", "#16161e", "#1f2130", "#2a2d40", "#c0caf5", "#7aa2f7", "#bb9af7", "#9ece6a", "#ff9e64", "#f7768e", "#e0af68"),
            new Palette("catppuccin", "Catppuccin", "#11111b", "#181825", "#232336", "#313244", "#cdd6f4", "#cba6f7", "#89b4fa", "#a6e3a1", "#fab387", "#f38ba8", "#f9e2af"),
            new Palette("nord", "Nord", "#1c2028", "#242933", "#2e3440", "#3b4252", "#eceff4", "#88c0d0", "#81a1c1", "#a3be8c", "#d08770", "#bf616a", "#ebcb8b"),
            new Palette("rosepine", "Rosé Pine", "#120f1d", "#191724", "#211f30", "#2c2a40", "#e0def4", "#c4a7e7", "#ebbcba", "#9ccfd8", "#f6c177", "#eb6f92", "#f6c177"),
            new Palette("aurora", "Aurora", "#05070d", "#0b0f1a", "#131a2c", "#1c2540", "#e6ecff", "#5eead4", "#818cf8", "#4ade80", "#fb923c", "#f87171", "#facc15"),
            new Palette("sunset", "Sunset", "#0f080b", "#170e12", "#23161c", "#311f28", "#fde8e4", "#fb923c", "#f472b6", "#86efac", "#fdba74", "#fb7185", "#fde047"),
            new Palette("forest", "Forest", "#060a07", "#0d140f", "#152018", "#1e2d22", "#e3f1e6", "#86efac", "#2dd4bf", "#4ade80", "#fdba74", "#fca5a5", "#fde68a"),
        };

        public static Palette Current = Palettes[0];
        static SolidColorBrush NB() { return new SolidColorBrush(Colors.Black); }
        public static readonly SolidColorBrush Bg = NB(), Surface = NB(), Raised = NB(), Hover = NB(), Text = NB(), Text2 = NB(), Text3 = NB(),
            Hairline = NB(), Highlight = NB(), Accent = NB(), Accent2 = NB(), Green = NB(), Orange = NB(), Red = NB(), Yellow = NB(), OnAccent = NB(),
            AccentSoft = NB(), Accent2Soft = NB(), RedSoft = NB(), OrangeSoft = NB(), GreenSoft = NB();
        public static readonly LinearGradientBrush AccentFill = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        static readonly GradientStop g0 = new GradientStop(Colors.White, 0), g1 = new GradientStop(Colors.White, 1);
        public static event Action Changed;

        static Theme() { AccentFill.GradientStops.Add(g0); AccentFill.GradientStops.Add(g1); }

        public static Color Hex(string s) { try { return (Color)ColorConverter.ConvertFromString(s); } catch { return Colors.Gray; } }
        public static string ToHex(Color c) { return string.Format("#{0:x2}{1:x2}{2:x2}", c.R, c.G, c.B); }
        public static Color A(Color c, double a) { return Color.FromArgb((byte)Math.Round(255 * Math.Max(0, Math.Min(1, a))), c.R, c.G, c.B); }
        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        public static Color Hsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360; s = Math.Max(0, Math.Min(1, s)); v = Math.Max(0, Math.Min(1, v));
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
        public static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
            v = mx; s = mx == 0 ? 0 : d / mx; h = 0;
            if (d == 0) return;
            if (mx == r) h = 60 * (((g - b) / d) % 6); else if (mx == g) h = 60 * ((b - r) / d + 2); else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        // Temperature -> colour (cool accent, warm orange, hot red).
        public static Color TempColor(double t)
        {
            var p = Current;
            if (t <= 55) return Mix(p.Green, p.Accent, (t - 30) / 25);
            if (t <= 75) return Mix(p.Accent, p.Orange, (t - 55) / 20);
            return Mix(p.Orange, p.Red, (t - 75) / 15);
        }

        static void Set(SolidColorBrush b, Color c, bool anim)
        {
            // one bad (frozen) brush must never stop the rest of the theme from switching
            if (b.IsFrozen) { Log.Write("theme: a shared brush is frozen; skipped (current " + ToHex(b.Color) + " a=" + b.Color.A + ", wanted " + ToHex(c) + ")"); return; }
            try
            {
                if (!anim) { b.BeginAnimation(SolidColorBrush.ColorProperty, null); b.Color = c; return; }
                b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            catch (Exception ex) { Log.Write("theme: " + ex.Message); }
        }

        public static void Apply(Palette p, bool anim)
        {
            Current = p;
            Set(Bg, p.Bg, anim); Set(Surface, p.Surface, anim); Set(Raised, p.Raised, anim); Set(Hover, p.Hover, anim);
            Set(Text, p.Text, anim); Set(Text2, A(p.Text, 0.62), anim); Set(Text3, A(p.Text, 0.38), anim);
            Set(Hairline, A(p.Text, 0.08), anim); Set(Highlight, A(p.Text, 0.05), anim);
            Set(Accent, p.Accent, anim); Set(Accent2, p.Accent2, anim);
            Set(Green, p.Green, anim); Set(Orange, p.Orange, anim); Set(Red, p.Red, anim); Set(Yellow, p.Yellow, anim);
            Set(AccentSoft, A(p.Accent, 0.16), anim); Set(Accent2Soft, A(p.Accent2, 0.16), anim); Set(RedSoft, A(p.Red, 0.16), anim);
            Set(OrangeSoft, A(p.Orange, 0.16), anim); Set(GreenSoft, A(p.Green, 0.16), anim);
            double lum = 0.2126 * p.Accent.R / 255 + 0.7152 * p.Accent.G / 255 + 0.0722 * p.Accent.B / 255;
            Set(OnAccent, lum > 0.6 ? p.Bg : Colors.White, anim);
            g0.Color = p.Accent; g1.Color = p.Accent;   // flat accent (user: no gradients on buttons/logo)
            if (Changed != null) Changed();
        }
    }

    // ===================================================================== helpers
    static class K
    {
        public static FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static FontFamily Display = new FontFamily("Segoe UI Variable Display, Segoe UI");
        public static FontFamily Bundled(string file, string family, FontFamily fallback)
        {
            try
            {
                string dir = System.IO.Path.Combine(AppDir.Path, "fonts");
                if (System.IO.File.Exists(System.IO.Path.Combine(dir, file)))
                    return new FontFamily(new Uri(dir.Replace('\\', '/') + "/", UriKind.Absolute), "./#" + family + ", Segoe UI Variable Text, Segoe UI");
            }
            catch { }
            return fallback;
        }
        public static void CrispText(FrameworkElement w)
        {
            w.UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(w, TextFormattingMode.Ideal);
            TextOptions.SetTextHintingMode(w, TextHintingMode.Auto);
            TextOptions.SetTextRenderingMode(w, TextRenderingMode.ClearType);
        }
        public static readonly FontFamily ICO = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public static readonly CultureInfo EN = CultureInfo.GetCultureInfo("en-US");
        public static readonly CultureInfo ID = CultureInfo.GetCultureInfo("id-ID");
        public static readonly IEasingFunction OutQuint = new QuinticEase { EasingMode = EasingMode.EaseOut };
        public static readonly IEasingFunction OutCubic = new CubicEase { EasingMode = EasingMode.EaseOut };
        public static readonly IEasingFunction InOutCubic = new CubicEase { EasingMode = EasingMode.EaseInOut };
        public static readonly IEasingFunction OutBack = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        public static readonly IEasingFunction Elastic = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 };

        public static readonly DependencyProperty HoverTo = DependencyProperty.RegisterAttached("HoverTo", typeof(Color), typeof(K), new PropertyMetadata(Colors.Transparent));
        public static readonly DependencyProperty HoverFrom = DependencyProperty.RegisterAttached("HoverFrom", typeof(Color), typeof(K), new PropertyMetadata(Colors.Transparent));

        static double animScale = 1;          // Settings -> animation speed. 0 = instant + no ambient motion.
        public static event Action AnimChanged;
        public static double AnimScale { get { return animScale; } set { animScale = value; if (AnimChanged != null) AnimChanged(); } }
        public static int Dur(int ms) { return (int)Math.Round(ms * AnimScale); }

        public static Brush Solid(Color c) { var x = new SolidColorBrush(c); x.Freeze(); return x; }

        public static TextBlock TB(string text, double size, Brush fg, FontWeight w)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = fg, FontWeight = w };
            t.TextTrimming = TextTrimming.CharacterEllipsis; t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }
        public static TextBlock Glyph(string g, double size, Brush fg)
        {
            var t = new TextBlock { Text = g, FontSize = size, Foreground = fg, FontFamily = ICO };
            t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
            return t;
        }
        public static void Tabular(TextBlock t) { System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular); }

        public static DoubleAnimation Anim(double to, int ms, IEasingFunction ease)
        {
            return new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(Math.Max(1, Dur(ms))))) { EasingFunction = ease };
        }
        public static DoubleAnimation AnimRaw(double to, int ms, IEasingFunction ease)
        {
            return new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(Math.Max(1, ms)))) { EasingFunction = ease };
        }
        public static void Pop(ScaleTransform st, double to, int ms, IEasingFunction ease)
        {
            var a = Anim(to, ms, ease);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        public static void PopIn(UIElement el, double from, int ms)
        {
            var st = new ScaleTransform(from, from); el.RenderTransform = st; el.RenderTransformOrigin = new Point(0.5, 0.5);
            Pop(st, 1, ms, OutBack);
        }
        public static void Fade(UIElement el, double to, int ms) { el.BeginAnimation(UIElement.OpacityProperty, Anim(to, ms, OutCubic)); }

        public static Border IconBtn(string glyph, double size, double fs, Brush fg, Action click)
        {
            var t = Glyph(glyph, fs, fg);
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Brushes.Transparent, Child = t, RenderTransformOrigin = new Point(0.5, 0.5), Cursor = Cursors.Hand };
            Pressable(b, click, true);
            return b;
        }

        public static Border PillBtn(string text, Brush bg, Brush fg, double h, Action click)
        {
            var t = TB(text, 13, fg, FontWeights.SemiBold); t.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Border { Height = h, CornerRadius = new CornerRadius(h / 2), Background = bg, Child = t, Padding = new Thickness(16, 0, 16, 0), RenderTransformOrigin = new Point(0.5, 0.5), Cursor = Cursors.Hand };
            Pressable(b, click, false);
            return b;
        }

        // Hover tint + spring press, like the island's buttons.
        public static void Pressable(Border b, Action click, bool hoverBg)
        {
            var st = b.RenderTransform as ScaleTransform;
            if (st == null) { st = new ScaleTransform(1, 1); b.RenderTransform = st; }
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            Brush orig = null; SolidColorBrush fx = null;
            b.MouseEnter += delegate
            {
                if (!hoverBg) { b.BeginAnimation(UIElement.OpacityProperty, AnimRaw(0.86, 140, OutCubic)); return; }
                if (fx == null || b.Background != fx)
                {
                    orig = b.Background;
                    var sc = orig as SolidColorBrush;
                    bool clear = sc == null || sc.Color.A == 0;
                    Color from = clear ? Theme.A(Theme.Current.Text, 0) : sc.Color;
                    fx = new SolidColorBrush(from);
                    b.Background = fx;
                    Color to = clear ? Theme.A(Theme.Current.Text, 0.07) : Theme.Mix(sc.Color, Theme.Current.Text, 0.07);
                    fx.SetValue(HoverTo, to); fx.SetValue(HoverFrom, from);
                }
                fx.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation((Color)fx.GetValue(HoverTo), TimeSpan.FromMilliseconds(160)) { EasingFunction = OutCubic });
            };
            b.MouseLeave += delegate
            {
                if (hoverBg && fx != null)
                {
                    var anim = new ColorAnimation((Color)fx.GetValue(HoverFrom), TimeSpan.FromMilliseconds(220)) { EasingFunction = OutCubic };
                    var keep = fx; var o = orig;
                    anim.Completed += delegate { if (b.Background == keep && !b.IsMouseOver) { b.Background = o; fx = null; } };
                    fx.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                }
                else if (!hoverBg) b.BeginAnimation(UIElement.OpacityProperty, AnimRaw(1, 200, OutCubic));
                Pop(st, 1, 200, OutQuint);
            };
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; Pop(st, 0.94, 90, OutQuint); };
            b.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                Pop(st, 1, 520, Elastic);
                if (click != null) click();
            };
        }

        public static Border Card(UIElement child, Thickness pad)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(20), Background = Theme.Surface, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1),
                Padding = pad, Child = child
            };
            HoverEdge(b);
            return b;
        }

        // The card's edge brightens a little under the pointer (only while it shows the shared hairline).
        public static void HoverEdge(Border b)
        {
            SolidColorBrush fx = null;
            b.MouseEnter += delegate
            {
                if (b.BorderBrush != Theme.Hairline && b.BorderBrush != fx) return;
                if (fx == null) { fx = new SolidColorBrush(Theme.Hairline.Color); }
                b.BorderBrush = fx;
                fx.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Theme.A(Theme.Current.Text, 0.17), TimeSpan.FromMilliseconds(Dur(180))) { EasingFunction = OutCubic });
            };
            b.MouseLeave += delegate
            {
                if (fx == null || b.BorderBrush != fx) return;
                var a = new ColorAnimation(Theme.Hairline.Color, TimeSpan.FromMilliseconds(Dur(320))) { EasingFunction = OutCubic };
                var keep = fx;
                a.Completed += delegate { if (b.BorderBrush == keep && !b.IsMouseOver) b.BorderBrush = Theme.Hairline; };
                fx.BeginAnimation(SolidColorBrush.ColorProperty, a);
            };
        }

        // Dark rounded tooltips everywhere (the default is a white system box).
        // NOTE: a Style seals (freezes) any brush placed in it, which would break theme switching for the
        // shared theme brushes. So the style only holds resource *references*; the brushes live in the
        // application resources (added by Program) and stay animatable.
        public static Style ToolTipStyle()
        {
            var st = new Style(typeof(ToolTip));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetResourceReference(Border.BackgroundProperty, "TipBg");
            border.SetResourceReference(Border.BorderBrushProperty, "TipEdge");
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 7));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            border.AppendChild(cp);
            st.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ToolTip)) { VisualTree = border }));
            st.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TipFg")));
            st.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
            st.Setters.Add(new Setter(ToolTip.HasDropShadowProperty, false));
            st.Setters.Add(new Setter(ToolTipService.InitialShowDelayProperty, 350));
            return st;
        }

        public static string Bytes(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return (b >= 100 || i == 0 ? b.ToString("0", EN) : b.ToString("0.0", EN)) + " " + u[i];
        }
    }

    // Damped spring: interruptible, keeps momentum.
    class Spring
    {
        public double Value, Target, Vel, Kk, D, Eps;
        public Spring(double v, double eps) { Value = Target = v; Eps = eps; Tune(260, 0.82); }
        public void Tune(double k, double zeta) { Kk = k; D = 2 * zeta * Math.Sqrt(k); }
        public void Step(double dt)
        {
            int n = (int)Math.Ceiling(dt / 0.004); double h = dt / Math.Max(1, n);
            for (int i = 0; i < n; i++) { double a = Kk * (Target - Value) - D * Vel; Vel += a * h; Value += Vel * h; }
        }
        public bool Settle()
        {
            if (Math.Abs(Target - Value) < Eps && Math.Abs(Vel) < Eps * 4) { Value = Target; Vel = 0; return true; }
            return false;
        }
        public void Snap(double v) { Value = Target = v; Vel = 0; }
    }

    // One per-frame clock for every running spring/spinner (stops itself when idle).
    static class Frame
    {
        static readonly List<Func<double, bool>> subs = new List<Func<double, bool>>();
        static bool hooked; static TimeSpan last;
        public static void Add(Func<double, bool> tick)
        {
            if (!subs.Contains(tick)) subs.Add(tick);
            if (!hooked) { hooked = true; last = TimeSpan.Zero; CompositionTarget.Rendering += OnFrame; }
        }
        static void OnFrame(object s, EventArgs e)
        {
            var t = ((RenderingEventArgs)e).RenderingTime;
            if (t == last) return;
            double dt = last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (t - last).TotalSeconds);
            last = t;
            for (int i = subs.Count - 1; i >= 0; i--)
            {
                bool keep;
                try { keep = subs[i](dt); } catch { keep = false; }
                if (!keep) subs.RemoveAt(i);
            }
            if (subs.Count == 0) { CompositionTarget.Rendering -= OnFrame; hooked = false; }
        }
    }

    // ===================================================================== controls
    class Toggle : Border
    {
        readonly Border knob, fill; readonly TranslateTransform tt; bool on;
        public event Action<bool> Changed;
        public bool IsOn { get { return on; } }
        public Toggle(bool v)
        {
            Width = 46; Height = 28; CornerRadius = new CornerRadius(14); Cursor = Cursors.Hand; Background = Theme.Hover;
            var g = new Grid();
            fill = new Border { CornerRadius = new CornerRadius(14), Background = Theme.AccentFill };
            knob = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
            knob.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.25 };
            tt = new TranslateTransform(); knob.RenderTransform = tt;
            g.Children.Add(fill); g.Children.Add(knob);
            Child = g;
            Set(v, false);
            MouseLeftButtonDown += (s, e) => e.Handled = true;
            MouseLeftButtonUp += (s, e) => { e.Handled = true; Set(!on, true); if (Changed != null) Changed(on); };
        }
        public void Set(bool v, bool anim)
        {
            on = v;
            double x = v ? 18 : 0;
            if (!anim) { fill.BeginAnimation(OpacityProperty, null); tt.BeginAnimation(TranslateTransform.XProperty, null); fill.Opacity = v ? 1 : 0; tt.X = x; return; }
            fill.BeginAnimation(OpacityProperty, K.Anim(v ? 1 : 0, 200, K.OutCubic));
            tt.BeginAnimation(TranslateTransform.XProperty, K.Anim(x, 420, new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6 }));
        }
    }

    // Segmented control with a spring-driven sliding indicator (cells size to the available width).
    class Segmented : Border
    {
        readonly Border ind; readonly TranslateTransform tt = new TranslateTransform();
        readonly List<TextBlock> labels = new List<TextBlock>(); readonly Grid cells = new Grid(); int sel; readonly int n;
        readonly Spring sp = new Spring(0, 0.3); bool live;
        public event Action<int> Changed;
        public int Selected { get { return sel; } }
        public Segmented(string[] items, int selected, double height)
        {
            n = items.Length; sel = Math.Max(0, Math.Min(n - 1, selected));
            CornerRadius = new CornerRadius(height / 2 > 14 ? 14 : height / 2); Background = Theme.Raised; Padding = new Thickness(3); Height = height;
            sp.Tune(380, 0.78);
            var g = new Grid();
            ind = new Border { CornerRadius = new CornerRadius(Math.Max(4, CornerRadius.TopLeft - 3)), Background = Theme.Hover, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = tt };
            ind.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.25 };
            g.Children.Add(ind);
            for (int i = 0; i < n; i++)
            {
                cells.ColumnDefinitions.Add(new ColumnDefinition());
                int idx = i;
                var t = K.TB(items[i], 13, i == sel ? Theme.Text : Theme.Text2, FontWeights.SemiBold);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                var cell = new Border { Background = Brushes.Transparent, Child = t, Cursor = Cursors.Hand };
                Grid.SetColumn(cell, i);
                cell.MouseEnter += delegate { if (idx != sel) t.Foreground = Theme.Text; };
                cell.MouseLeave += delegate { if (idx != sel) t.Foreground = Theme.Text2; };
                cell.MouseLeftButtonDown += (s, e) => e.Handled = true;
                cell.MouseLeftButtonUp += (s, e) => { e.Handled = true; Select(idx, true); };
                labels.Add(t); cells.Children.Add(cell);
            }
            g.Children.Add(cells);
            Child = g;
            g.SizeChanged += delegate { double w = g.ActualWidth / n; ind.Width = w; sp.Snap(Math.Max(0, sel) * w); tt.X = sp.Value; };
        }
        public void SetLabelSize(double size) { foreach (var l in labels) l.FontSize = size; }
        // Icon-only segments (labels are icon glyphs), with a tooltip per segment.
        public void UseIcons(string[] tips)
        {
            for (int i = 0; i < labels.Count; i++)
            {
                labels[i].FontFamily = K.ICO; labels[i].FontWeight = FontWeights.Normal; labels[i].FontSize = 16;
                if (tips != null && i < tips.Length) ((Border)labels[i].Parent).ToolTip = tips[i];
            }
        }
        // No option highlighted (value outside the presets).
        public void Clear()
        {
            sel = -1; ind.BeginAnimation(OpacityProperty, K.Anim(0, 160, K.OutCubic));
            foreach (var l in labels) l.Foreground = Theme.Text2;
        }
        public void Select(int i, bool fire)
        {
            if (i < 0 || i >= n) return;
            if (i == sel) { if (fire && Changed != null) Changed(i); return; }
            if (sel < 0) { ind.BeginAnimation(OpacityProperty, K.Anim(1, 200, K.OutCubic)); double cw = ActualWidth > 0 ? (ActualWidth - 6) / n : 0; sp.Snap(i * cw); tt.X = sp.Value; }
            sel = i;
            double w = ActualWidth > 0 ? (ActualWidth - 6) / n : 0;
            sp.Target = i * w;
            if (!live) { live = true; Frame.Add(Tick); }
            for (int j = 0; j < labels.Count; j++) labels[j].Foreground = j == i ? Theme.Text : Theme.Text2;
            if (fire && Changed != null) Changed(i);
        }
        bool Tick(double dt)
        {
            if (K.AnimScale <= 0) sp.Snap(sp.Target); else sp.Step(dt * Math.Max(0.5, 1 / Math.Max(0.01, K.AnimScale)));
            tt.X = sp.Value;
            if (sp.Settle()) { tt.X = sp.Value; live = false; return false; }
            return true;
        }
    }

    // Arc gauge (270°) with a gradient-coloured value arc and a soft glow.
    class Gauge : Grid
    {
        readonly Path track, arc, glow; readonly double size, thick; double shown = -1;
        readonly Spring sp = new Spring(0, 0.0008); bool live;
        public Gauge(double size, double thick)
        {
            this.size = size; this.thick = thick; Width = size; Height = size;
            track = new Path { Stroke = Theme.Raised, StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            glow = new Path { StrokeThickness = thick * 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0.35 };
            glow.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = thick * 1.6 };
            arc = new Path { StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            track.Data = Geo(1);
            Children.Add(track); Children.Add(glow); Children.Add(arc);
            sp.Tune(90, 0.9);
            SetColor(Theme.Current.Accent);
        }
        public void SetColor(Color c)
        {
            var b = new SolidColorBrush(c);
            arc.Stroke = b; glow.Stroke = b;
        }
        Geometry Geo(double v)
        {
            v = Math.Max(0, Math.Min(0.9999, v));
            double r = (size - thick) / 2 - thick * 0.6, c = size / 2;
            double a0 = 135 * Math.PI / 180, a1 = a0 + v * 270 * Math.PI / 180;
            var p0 = new Point(c + r * Math.Cos(a0), c + r * Math.Sin(a0));
            var p1 = new Point(c + r * Math.Cos(a1), c + r * Math.Sin(a1));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p0, false, false);
                ctx.ArcTo(p1, new Size(r, r), 0, v * 270 > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }
        public void SetValue01(double v)
        {
            sp.Target = Math.Max(0, Math.Min(1, v));
            if (K.AnimScale <= 0) { sp.Snap(sp.Target); Draw(); return; }
            if (!live) { live = true; Frame.Add(Tick); }
        }
        bool Tick(double dt)
        {
            sp.Step(dt); Draw();
            if (sp.Settle()) { Draw(); live = false; return false; }
            return true;
        }
        void Draw()
        {
            double v = sp.Value;
            if (Math.Abs(v - shown) < 0.0005) return;
            shown = v;
            var g = v <= 0.002 ? null : Geo(v);
            arc.Data = g; glow.Data = g;
        }
    }

    // Number that springs to its new value (tabular digits).
    class SpringNumber : TextBlock
    {
        readonly Spring sp = new Spring(0, 0.4); bool live, first = true; string fmt, suffix;
        public SpringNumber(double size, Brush fg, FontWeight w, string format, string suffix)
        {
            FontSize = size; Foreground = fg; FontWeight = w; fmt = format; this.suffix = suffix;
            K.Tabular(this); VerticalAlignment = VerticalAlignment.Center;
            sp.Tune(120, 1.0);
            Text = "–";
        }
        bool textMode;
        public void Set(double v)
        {
            if (first || K.AnimScale <= 0 || textMode) { first = false; textMode = false; sp.Snap(v); Render(); return; }
            sp.Target = v;
            if (!live) { live = true; Frame.Add(Tick); }
        }
        public void SetText(string s) { textMode = true; sp.Snap(sp.Target); Text = s; }
        bool Tick(double dt) { if (textMode) { live = false; return false; } sp.Step(dt); Render(); if (sp.Settle()) { Render(); live = false; return false; } return true; }
        void Render() { Text = sp.Value.ToString(fmt, K.EN) + suffix; }
    }

    // Live line chart: smooth Catmull-Rom curve, gradient area, auto/fixed range, value axis and a
    // hover crosshair with a tooltip (values of every series + how long ago).
    class LiveChart : Grid
    {
        class Series { public List<double> Data = new List<double>(); public Path Line, Area; public Color Col; public string Name; public Ellipse Dot; }
        readonly List<Series> series = new List<Series>();
        readonly Canvas canvas = new Canvas { ClipToBounds = false, Background = Brushes.Transparent };
        readonly int cap; public double Min, Max; public bool AutoMax; public double AutoStep = 500;
        public string Unit = ""; public string Format = "0"; public double SampleSeconds = 1;
        readonly List<Line> grid = new List<Line>();
        readonly List<TextBlock> labels = new List<TextBlock>();
        readonly Line cross; readonly Border tip; readonly StackPanel tipBody; readonly TextBlock tipHead;
        const double PadL = 40;
        double shownMax;
        int hoverIdx = -1;

        public LiveChart(int capacity, double min, double max)
        {
            cap = capacity; Min = min; Max = max;
            Children.Add(canvas);
            for (int i = 0; i < 3; i++)
            {
                var l = new Line { Stroke = Theme.Hairline, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 } };
                grid.Add(l); canvas.Children.Add(l);
                var t = K.TB("", 10.5, Theme.Text3, FontWeights.SemiBold); K.Tabular(t);
                labels.Add(t); canvas.Children.Add(t);
            }
            cross = new Line { Stroke = Theme.Text3, StrokeThickness = 1, Opacity = 0, IsHitTestVisible = false };
            canvas.Children.Add(cross);
            tipHead = K.TB("", 11, Theme.Text3, FontWeights.SemiBold);
            tipBody = new StackPanel();
            var tsp = new StackPanel(); tsp.Children.Add(tipHead); tsp.Children.Add(tipBody);
            tip = new Border { Background = Theme.Raised, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 7), Child = tsp, Opacity = 0, IsHitTestVisible = false };
            Panel.SetZIndex(tip, 50);
            SizeChanged += delegate { Redraw(); };
            MouseMove += (s, e) => { Hover(e.GetPosition(canvas).X); };
            MouseLeave += delegate { hoverIdx = -1; cross.Opacity = 0; tip.BeginAnimation(OpacityProperty, K.Anim(0, 140, K.OutCubic)); foreach (var se in series) se.Dot.Opacity = 0; };
        }

        public int AddSeries(Color c, string name)
        {
            var s = new Series { Col = c, Name = name };
            var fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fill.GradientStops.Add(new GradientStop(Theme.A(c, 0.30), 0)); fill.GradientStops.Add(new GradientStop(Theme.A(c, 0), 1));
            s.Area = new Path { Fill = fill, IsHitTestVisible = false };
            s.Line = new Path { Stroke = new SolidColorBrush(c), StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
            s.Dot = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(c), Stroke = Theme.Surface, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false };
            canvas.Children.Insert(canvas.Children.IndexOf(cross), s.Area);
            canvas.Children.Insert(canvas.Children.IndexOf(cross), s.Line);
            canvas.Children.Add(s.Dot);
            if (!canvas.Children.Contains(tip)) canvas.Children.Add(tip);
            series.Add(s);
            return series.Count - 1;
        }
        public int AddSeries(Color c) { return AddSeries(c, ""); }

        public void SetColor(int i, Color c)
        {
            var s = series[i]; s.Col = c;
            ((SolidColorBrush)s.Line.Stroke).Color = c; ((SolidColorBrush)s.Dot.Fill).Color = c;
            var fill = (LinearGradientBrush)s.Area.Fill;
            fill.GradientStops[0].Color = Theme.A(c, 0.30); fill.GradientStops[1].Color = Theme.A(c, 0);
        }
        public void Push(int i, double v)
        {
            var d = series[i].Data;
            d.Add(v); while (d.Count > cap) d.RemoveAt(0);
        }
        public double Last(int i) { var d = series[i].Data; return d.Count == 0 ? double.NaN : d[d.Count - 1]; }

        double Wd { get { return Math.Max(1, ActualWidth - PadL); } }
        double YOf(double v, double max) { double h = ActualHeight; return h - 3 - Math.Max(0, Math.Min(1, (v - Min) / Math.Max(1e-6, max - Min))) * (h - 8); }

        public void Redraw()
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double max = Max;
            if (AutoMax)
            {
                double m = 1;
                foreach (var s in series) foreach (var v in s.Data) if (v > m) m = v;
                // a multiple of 4 steps, so the ¼ grid lines land on round numbers too
                max = Math.Max(4 * AutoStep, Math.Ceiling(m * 1.1 / (4 * AutoStep)) * 4 * AutoStep);
            }
            shownMax = max;
            // grid lines sit exactly on round values (¼, ½, ¾ of the range)
            for (int i = 0; i < grid.Count; i++)
            {
                double val = Min + (max - Min) * (i + 1) / (grid.Count + 1);
                double y = Math.Round(YOf(val, max)) + 0.5;
                grid[i].X1 = PadL; grid[i].X2 = w; grid[i].Y1 = y; grid[i].Y2 = y;
                labels[i].Text = val.ToString(Format, K.EN) + Unit;
                Canvas.SetLeft(labels[i], 0); Canvas.SetTop(labels[i], y - 8);
            }
            double step = Wd / (cap - 1);
            foreach (var s in series)
            {
                int n = s.Data.Count;
                if (n < 2) { s.Line.Data = null; s.Area.Data = null; continue; }
                var pts = new Point[n];
                double x0 = w - (n - 1) * step;
                for (int k = 0; k < n; k++) pts[k] = new Point(x0 + k * step, YOf(s.Data[k], max));
                var line = new StreamGeometry();
                using (var ctx = line.Open()) { ctx.BeginFigure(pts[0], false, false); Curve(ctx, pts); }
                line.Freeze();
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(pts[0].X, h), true, true);
                    ctx.LineTo(pts[0], false, false);
                    Curve(ctx, pts);
                    ctx.LineTo(new Point(pts[n - 1].X, h), false, false);
                }
                area.Freeze();
                s.Line.Data = line; s.Area.Data = area;
            }
            if (hoverIdx >= 0) ShowHover();
        }

        // crosshair: snap to the nearest sample under the pointer
        void Hover(double x)
        {
            if (series.Count == 0 || series[0].Data.Count < 2 || x < PadL) return;
            double step = Wd / (cap - 1);
            int n = series[0].Data.Count;
            double x0 = ActualWidth - (n - 1) * step;
            int idx = (int)Math.Round((x - x0) / step);
            if (idx < 0 || idx >= n) { hoverIdx = -1; cross.Opacity = 0; tip.Opacity = 0; foreach (var se in series) se.Dot.Opacity = 0; return; }
            if (tip.Opacity < 0.5) tip.BeginAnimation(OpacityProperty, K.Anim(1, 120, K.OutCubic));
            hoverIdx = idx;
            ShowHover();
        }

        void ShowHover()
        {
            int n = series[0].Data.Count;
            if (hoverIdx >= n) hoverIdx = n - 1;
            double step = Wd / (cap - 1), x0 = ActualWidth - (n - 1) * step;
            double x = x0 + hoverIdx * step;
            cross.X1 = cross.X2 = x; cross.Y1 = 0; cross.Y2 = ActualHeight; cross.Opacity = 0.6;
            int ago = (int)Math.Round((n - 1 - hoverIdx) * SampleSeconds);
            tipHead.Text = ago == 0 ? "now" : ago < 60 ? ago + " s ago" : (ago / 60) + " min " + (ago % 60) + " s ago";
            tipBody.Children.Clear();
            foreach (var s in series)
            {
                if (s.Data.Count != n) continue;
                double v = s.Data[hoverIdx];
                s.Dot.Opacity = 1;
                Canvas.SetLeft(s.Dot, x - 4.5); Canvas.SetTop(s.Dot, YOf(v, shownMax) - 4.5);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                row.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(s.Col), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                var t = K.TB(s.Name + "  " + v.ToString(Format == "0" ? "#,0" : Format, K.EN) + Unit, 12, Theme.Text, FontWeights.SemiBold); K.Tabular(t);
                row.Children.Add(t);
                tipBody.Children.Add(row);
            }
            tip.Measure(new Size(400, 200));
            double tx = x + 12; if (tx + tip.DesiredSize.Width > ActualWidth) tx = x - 12 - tip.DesiredSize.Width;
            Canvas.SetLeft(tip, tx); Canvas.SetTop(tip, 4);
        }

        static void Curve(StreamGeometryContext ctx, Point[] p)
        {
            for (int i = 0; i < p.Length - 1; i++)
            {
                Point p0 = i > 0 ? p[i - 1] : p[i], p1 = p[i], p2 = p[i + 1], p3 = i + 2 < p.Length ? p[i + 2] : p2;
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                ctx.BezierTo(c1, c2, p2, true, true);
            }
        }
    }

    // Fan rotor whose spin speed follows the real RPM (eased, so speed changes are smooth).
    class FanRotor : Grid
    {
        readonly RotateTransform rot = new RotateTransform();
        readonly Spring speed = new Spring(0, 0.5); bool live; double angle;
        readonly Path blades;
        public FanRotor(double size)
        {
            Width = size; Height = size;
            double c = size / 2, r = size / 2 - 1;
            Children.Add(new Ellipse { Stroke = Theme.Hairline, StrokeThickness = 1.5 });
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                for (int i = 0; i < 7; i++)
                {
                    double a = i * 2 * Math.PI / 7;
                    Func<double, double, Point> P = (rad, ang) => new Point(c + rad * Math.Cos(a + ang), c + rad * Math.Sin(a + ang));
                    ctx.BeginFigure(P(r * 0.22, -0.30), true, true);
                    ctx.BezierTo(P(r * 0.55, -0.55), P(r * 0.88, -0.35), P(r * 0.90, 0.05), true, true);
                    ctx.BezierTo(P(r * 0.70, 0.22), P(r * 0.45, 0.22), P(r * 0.22, 0.25), true, true);
                }
            }
            g.Freeze();
            blades = new Path { Data = g, Fill = Theme.Text2, RenderTransform = rot, RenderTransformOrigin = new Point(0.5, 0.5) };
            blades.CacheMode = new BitmapCache { RenderAtScale = 2 };   // rotate a cached bitmap, not the vector path
            blades.Width = size; blades.Height = size; blades.Stretch = Stretch.None;
            Children.Add(blades);
            Children.Add(new Ellipse { Width = size * 0.26, Height = size * 0.26, Fill = Theme.Raised, Stroke = Theme.Hairline, StrokeThickness = 1 });
            speed.Tune(6, 1.0);
            IsVisibleChanged += delegate { if (IsVisible && !live && speed.Target > 0) { live = true; Frame.Add(Tick); } };
        }
        public void SetFill(Brush b) { blades.Fill = b; }
        public void SetRpm(double rpm)
        {
            // visual speed: a calm fraction of the real rpm (real speed strobes on screen)
            speed.Target = rpm <= 0 ? 0 : 40 + rpm * 0.12;   // degrees per second
            if (!live && IsVisible && K.AnimScale > 0 && (speed.Target > 0 || speed.Value > 0.5)) { live = true; Frame.Add(Tick); }
        }
        bool Tick(double dt)
        {
            // never keep the render loop alive for an invisible rotor (hidden page / tray) or with motion off
            if (!IsVisible || K.AnimScale <= 0) { live = false; return false; }
            speed.Step(dt);
            angle = (angle + speed.Value * dt) % 360;
            rot.Angle = angle;
            if (speed.Target == 0 && speed.Value < 0.5) { live = false; return false; }
            return true;
        }
    }

    // Horizontal slider: track + accent fill + knob, drag/click/wheel; optional snapping steps.
    class SliderBar : Grid
    {
        readonly Border fill, knob, track; readonly double min, max, step; double val;
        public event Action<double> Changed;      // live while dragging
        public event Action<double> Committed;    // on release
        public double Value { get { return val; } }
        public SliderBar(double min, double max, double step, double value, double height)
        {
            this.min = min; this.max = max; this.step = step;
            Height = Math.Max(height, 24); Background = Brushes.Transparent; Cursor = Cursors.Hand;
            track = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), Background = Theme.Raised, VerticalAlignment = VerticalAlignment.Center };
            fill = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), Background = Theme.AccentFill, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            knob = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            knob.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3 };
            Children.Add(track); Children.Add(fill); Children.Add(knob);
            SizeChanged += delegate { Layout(false); };
            // a click glides to the spot; while dragging the knob follows the cursor 1:1 (restarting an
            // ease on every mouse move made it lag and jitter)
            MouseLeftButtonDown += (s, e) => { e.Handled = true; CaptureMouse(); FromPos(e.GetPosition(this).X, false, true); };
            MouseMove += (s, e) => { if (IsMouseCaptured) FromPos(e.GetPosition(this).X, false, false); };
            MouseLeftButtonUp += (s, e) => { e.Handled = true; if (IsMouseCaptured) { ReleaseMouseCapture(); FromPos(e.GetPosition(this).X, true, false); } };
            MouseWheel += (s, e) => { e.Handled = true; SetValue(val + (e.Delta > 0 ? 1 : -1) * Math.Max(step, (max - min) / 20), true); if (Committed != null) Committed(val); };
            SetValue(value, false);
        }
        public void SetTrack(Brush b) { track.Background = b; fill.Opacity = 0; }
        public void SetKnob(Brush b) { knob.Background = b; }
        void FromPos(double x, bool commit, bool anim)
        {
            double w = Math.Max(1, ActualWidth - knob.Width);
            double v = min + Math.Max(0, Math.Min(1, (x - knob.Width / 2) / w)) * (max - min);
            Set(v, true, anim);
            if (commit && Committed != null) Committed(val);
        }
        public void SetValue(double v, bool fire) { Set(v, fire, fire); }
        void Set(double v, bool fire, bool anim)
        {
            if (step > 0) v = Math.Round((v - min) / step) * step + min;
            v = Math.Max(min, Math.Min(max, v));
            bool changed = v != val; val = v;
            if (anim || changed || !IsMouseCaptured) Layout(anim);
            if (fire && changed && Changed != null) Changed(val);
        }
        void Layout(bool anim)
        {
            double w = Math.Max(0, ActualWidth - knob.Width);
            double t = (val - min) / Math.Max(1e-9, max - min);
            double x = t * w;
            if (anim && step > 0 && K.AnimScale > 0)
            {
                knob.BeginAnimation(MarginProperty, null);
                var tm = new ThicknessAnimation(new Thickness(x, 0, 0, 0), TimeSpan.FromMilliseconds(K.Dur(220))) { EasingFunction = K.OutCubic };
                knob.BeginAnimation(MarginProperty, tm);
                fill.BeginAnimation(WidthProperty, K.Anim(x + knob.Width / 2, 220, K.OutCubic));
            }
            else
            {
                knob.BeginAnimation(MarginProperty, null); fill.BeginAnimation(WidthProperty, null);
                knob.Margin = new Thickness(x, 0, 0, 0); fill.Width = x + knob.Width / 2;
            }
        }
    }

    static class Motion
    {
        // Children slide up and fade in one after another.
        public static void Stagger(Panel p, double fromY, int step)
        {
            if (p == null || K.AnimScale <= 0) return;
            int i = 0;
            foreach (UIElement c in p.Children)
            {
                if (c.Visibility != Visibility.Visible) continue;
                var begin = TimeSpan.FromMilliseconds(K.Dur(40 + i * step));
                var tt = new TranslateTransform(0, fromY);
                c.RenderTransform = tt;
                var a = K.Anim(0, 520, K.OutQuint); a.BeginTime = begin;
                tt.BeginAnimation(TranslateTransform.YProperty, a);
                c.Opacity = 0;
                var o = K.Anim(1, 360, K.OutCubic); o.BeginTime = begin;
                c.BeginAnimation(UIElement.OpacityProperty, o);
                i++;
            }
        }
    }
}
