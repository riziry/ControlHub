// Main window: custom chrome, sidebar with a spring indicator, page transitions, the "island" toast.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace ControlHub
{
    class ModeInfo
    {
        public int Value; public string Name, Short, Glyph, Desc; public Func<Color> Col;
        public ModeInfo(int v, string name, string sh, string glyph, string desc, Func<Color> col) { Value = v; Name = name; Short = sh; Glyph = glyph; Desc = desc; Col = col; }
    }

    static class Modes
    {
        // Ordered from most frugal to fastest. The Quiet button of this machine's Control Center
        // sends 4 (quiet_plus); 0 (plain quiet) is shown as Quiet too.
        public static readonly ModeInfo[] All =
        {
            new ModeInfo(1, "Battery Saver", "Saver", "", "Maximizes battery life.", () => Theme.Current.Yellow),
            new ModeInfo(4, "Quiet", "Quiet", "", "Limits performance and fan noise for quiet places.", () => Theme.Current.Green),
            new ModeInfo(3, "Balanced", "Balanced", "", "Balances performance, surface temperature and fan noise.", () => Theme.Current.Accent),
            new ModeInfo(2, "Performance", "Performance", "", "Maximum system performance. Fans run faster.", () => Theme.Current.Red),
        };
        // live brushes per mode (full colour and 16% tint) that follow theme changes
        static readonly Dictionary<int, SolidColorBrush> full = new Dictionary<int, SolidColorBrush>(), soft = new Dictionary<int, SolidColorBrush>();
        static bool hooked;
        static void Ensure()
        {
            if (full.Count == 0) foreach (var m in All) { full[m.Value] = new SolidColorBrush(); soft[m.Value] = new SolidColorBrush(); }
            foreach (var m in All) { var c = m.Col(); full[m.Value].Color = c; soft[m.Value].Color = Theme.A(c, 0.16); }
            if (!hooked) { hooked = true; Theme.Changed += Ensure; }
        }
        public static SolidColorBrush BrushOf(ModeInfo m) { if (full.Count == 0) Ensure(); return full[m.Value]; }
        public static SolidColorBrush SoftOf(ModeInfo m) { if (soft.Count == 0) Ensure(); return soft[m.Value]; }

        public static ModeInfo Of(int v)
        {
            if (v == 0) v = 4;
            foreach (var m in All) if (m.Value == v) return m;
            return null;
        }
        public static int IndexOf(int v)
        {
            if (v == 0) v = 4;
            for (int i = 0; i < All.Length; i++) if (All[i].Value == v) return i;
            return -1;
        }
    }

    interface IPage
    {
        string Title { get; }
        string Subtitle { get; }
        FrameworkElement View { get; }
        Panel StaggerRoot { get; }
        void Update(Snapshot s);
        void OnShown();
    }

    // Dynamic-Island style pill: grows from a dot to fit its content, then shrinks away.
    class Island : Border
    {
        readonly TextBlock icon, text; readonly StackPanel row;
        readonly Spring w = new Spring(0, 0.3), o = new Spring(0, 0.002); bool live;
        System.Windows.Threading.DispatcherTimer hide;
        public Island()
        {
            HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(0, 10, 0, 0); Height = 40; CornerRadius = new CornerRadius(20);
            Background = K.Solid(Color.FromRgb(0, 0, 0)); BorderBrush = Theme.Hairline; BorderThickness = new Thickness(1);
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45 };
            IsHitTestVisible = false; ClipToBounds = true;
            icon = K.Glyph("", 15, Theme.Accent); icon.Margin = new Thickness(0, 0, 10, 0);
            text = K.TB("", 13.5, K.Solid(Colors.White), FontWeights.SemiBold);
            row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(icon); row.Children.Add(text);
            Child = row;
            Width = 0; Opacity = 0;
            w.Tune(260, 0.72); o.Tune(200, 1);
        }
        public void Show(string glyph, string msg, Brush accent)
        {
            icon.Text = glyph; text.Text = msg; icon.Foreground = accent ?? Theme.Accent;
            row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            w.Target = row.DesiredSize.Width + 36; o.Target = 1;
            if (w.Value < 1) w.Value = 40;
            row.Opacity = 0; row.BeginAnimation(OpacityProperty, K.Anim(1, 260, K.OutCubic));
            Kick();
            if (hide == null) { hide = new System.Windows.Threading.DispatcherTimer(); hide.Tick += delegate { hide.Stop(); w.Target = 40; o.Target = 0; row.BeginAnimation(OpacityProperty, K.Anim(0, 160, K.OutCubic)); Kick(); }; }
            hide.Stop(); hide.Interval = TimeSpan.FromMilliseconds(2400); hide.Start();
        }
        void Kick()
        {
            if (K.AnimScale <= 0) { w.Snap(w.Target); o.Snap(o.Target); Width = w.Value; Opacity = o.Value; return; }
            if (!live) { live = true; Frame.Add(Tick); }
        }
        bool Tick(double dt)
        {
            w.Step(dt); o.Step(dt);
            Width = Math.Max(0, w.Value); Opacity = Math.Max(0, Math.Min(1, o.Value));
            bool done = w.Settle() & o.Settle();
            if (done) { live = false; return false; }
            return true;
        }
    }

    class MainWindow : Window
    {
        readonly Grid root = new Grid(), content = new Grid();
        readonly Border navIndicator; readonly TranslateTransform navTT = new TranslateTransform();
        readonly Spring navY = new Spring(0, 0.2); bool navLive;
        readonly StackPanel nav = new StackPanel();
        readonly List<Border> navItems = new List<Border>();
        readonly List<TextBlock> navGlyphs = new List<TextBlock>(), navLabels = new List<TextBlock>();
        readonly List<IPage> pages = new List<IPage>();
        readonly TextBlock title, subtitle, statusTemp, statusMode;
        readonly Border statusDot; readonly System.Windows.Shapes.Ellipse statusHalo; readonly RadialGradientBrush haloBrush;
        public readonly Island Island = new Island();
        int current = -1;
        public int PendingMode = -1; DateTime pendingAt;

        public DashboardPage Dash; public PowerPage Power; public FansPage FanPage; public BatteryPage Batt; public KeyboardPage Keys; public PrefsPage Pref; public SettingsPage Prefs;

        public MainWindow()
        {
            Title = "ControlHub"; Width = 1200; Height = 800; MinWidth = 980; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Bg; FontFamily = K.Font; Foreground = Theme.Text;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 52, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            K.CrispText(this);
            try { Icon = ToImage(Program.MakeIcon(Theme.Current.Accent, Theme.Current.Accent2)); } catch { }

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
            root.ColumnDefinitions.Add(new ColumnDefinition());

            // ---------------- sidebar
            var side = new Grid { Background = Theme.Surface };
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition());
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var sideLine = new Border { Width = 1, Background = Theme.Hairline, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRowSpan(sideLine, 3); side.Children.Add(sideLine);

            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 26, 0, 26) };
            var logo = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Background = Theme.AccentFill };
            logo.Effect = new DropShadowEffect { Color = Theme.Current.Accent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 };
            Theme.Changed += delegate { ((DropShadowEffect)logo.Effect).Color = Theme.Current.Accent; };
            logo.Child = K.Glyph("", 20, K.Solid(Colors.White));
            brand.Children.Add(logo);
            var bt = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            bt.Children.Add(K.TB("ControlHub", 17, Theme.Text, FontWeights.Bold));
            bt.Children.Add(K.TB("Axioo Pongo", 12, Theme.Text3, FontWeights.Medium));
            brand.Children.Add(bt);
            side.Children.Add(brand);

            var navHost = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            navIndicator = new Border { Height = 46, CornerRadius = new CornerRadius(14), Background = Theme.AccentSoft, VerticalAlignment = VerticalAlignment.Top, RenderTransform = navTT, Opacity = 0 };
            var bar = new Border { Width = 3, Height = 20, CornerRadius = new CornerRadius(2), Background = Theme.AccentFill, HorizontalAlignment = HorizontalAlignment.Left };
            navIndicator.Child = bar;
            navHost.Children.Add(navIndicator);
            navHost.Children.Add(nav);
            Grid.SetRow(navHost, 1); side.Children.Add(navHost);
            navY.Tune(320, 0.78);

            // status card at the bottom of the sidebar
            var st = new Border { Margin = new Thickness(14, 0, 14, 18), CornerRadius = new CornerRadius(16), Background = Theme.Raised, Padding = new Thickness(14, 12, 14, 12) };
            var stg = new Grid();
            stg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); stg.ColumnDefinitions.Add(new ColumnDefinition());
            // status light: solid dot + a radial-gradient halo that breathes (no Effect, so each frame is cheap)
            var dotHost = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-6, 0, 6, 0) };
            statusHalo = new System.Windows.Shapes.Ellipse { Width = 22, Height = 22 };
            haloBrush = new RadialGradientBrush();
            haloBrush.GradientStops.Add(new GradientStop(Theme.A(Theme.Current.Green, 0.75), 0));
            haloBrush.GradientStops.Add(new GradientStop(Theme.A(Theme.Current.Green, 0), 1));
            statusHalo.Fill = haloBrush;
            statusDot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Theme.Green, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dotHost.Children.Add(statusHalo); dotHost.Children.Add(statusDot);
            var pulse = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(1.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            Timeline.SetDesiredFrameRate(pulse, 30);
            statusHalo.BeginAnimation(OpacityProperty, pulse);
            // no animation clocks while the window can't be seen (tray / minimized)
            Action pulseState = () =>
            {
                bool seen = IsVisible && WindowState != WindowState.Minimized;
                statusHalo.BeginAnimation(OpacityProperty, seen && K.AnimScale > 0 ? pulse : null);
                if (K.AnimScale <= 0) statusHalo.Opacity = 0.6;
                Hw.SetBackground(!seen);
            };
            IsVisibleChanged += delegate { pulseState(); };
            StateChanged += delegate { pulseState(); };
            K.AnimChanged += delegate { pulseState(); };
            stg.Children.Add(dotHost);
            var sts = new StackPanel(); Grid.SetColumn(sts, 1);
            statusMode = K.TB("Connecting…", 13, Theme.Text, FontWeights.SemiBold);
            statusTemp = K.TB("", 12, Theme.Text3, FontWeights.Medium);
            sts.Children.Add(statusMode); sts.Children.Add(statusTemp);
            stg.Children.Add(sts);
            st.Child = stg;
            Grid.SetRow(st, 2); side.Children.Add(st);
            root.Children.Add(side);

            // ---------------- main area
            var main = new Grid();
            Grid.SetColumn(main, 1);
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(104) });
            main.RowDefinitions.Add(new RowDefinition());
            var head = new StackPanel { Margin = new Thickness(36, 30, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            title = K.TB("", 28, Theme.Text, FontWeights.Bold); title.FontFamily = K.Display;
            subtitle = K.TB("", 13.5, Theme.Text3, FontWeights.Medium); subtitle.Margin = new Thickness(1, 4, 0, 0);
            head.Children.Add(title); head.Children.Add(subtitle);
            main.Children.Add(head);

            var caption = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 12, 0) };
            caption.Children.Add(CapBtn("", () => WindowState = WindowState.Minimized, false));
            caption.Children.Add(CapBtn("", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized, false));
            caption.Children.Add(CapBtn("", () => Close(), true));
            main.Children.Add(caption);

            Grid.SetRow(content, 1);
            main.Children.Add(content);
            root.Children.Add(main);

            var overlay = new Grid { IsHitTestVisible = false };
            Grid.SetColumnSpan(overlay, 2);
            overlay.Children.Add(Island);
            root.Children.Add(overlay);

            Content = root;

            Dash = new DashboardPage(this); Power = new PowerPage(this); FanPage = new FansPage(this); Batt = new BatteryPage(this); Keys = new KeyboardPage(this); Pref = new PrefsPage(this); Prefs = new SettingsPage(this);
            AddPage(Dash, "", "Overview");
            AddPage(Power, "", "Power Mode");
            AddPage(FanPage, "", "Cooling");
            AddPage(Batt, "", "Battery");
            AddPage(Keys, "", "Keyboard");
            AddPage(Pref, "", "Preferences");
            AddPage(Prefs, "", "Settings");

            // parts that copy theme colours (instead of using the shared brushes) repaint on a theme change
            Theme.Changed += delegate
            {
                Action[] fixes = { Dash.ThemeChanged, Power.ThemeChanged, FanPage.ThemeChanged, Keys.ThemeChanged, Pref.ThemeChanged };
                foreach (var a in fixes) { try { a(); } catch (Exception ex) { Log.Write("theme repaint: " + ex.Message); } }
                try
                {
                    if (Program.Tray != null) Program.Tray.Icon = Program.MakeIcon(Theme.Current.Accent, Theme.Current.Accent2);
                    Icon = ToImage(Program.MakeIcon(Theme.Current.Accent, Theme.Current.Accent2));
                }
                catch { }
            };

            Loaded += delegate { Navigate(0, false); };
            SourceInitialized += delegate
            {
                var h = new WindowInteropHelper(this).Handle;
                int v = 1; Win.DwmSetWindowAttribute(h, 20, ref v, 4);          // dark title/frame
                int round = 2; Win.DwmSetWindowAttribute(h, 33, ref round, 4);  // rounded corners
                int border = unchecked((int)0xFFFFFFFE); Win.DwmSetWindowAttribute(h, 34, ref border, 4); // no accent border
            };
            StateChanged += delegate { root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0); };
            Closing += (s, e) =>
            {
                SaveBounds();
                if (!Program.Quitting && Settings.Bool("closeToTray", true)) { e.Cancel = true; AnimatedHide(); }
            };

            // open: fade + settle from a slightly smaller scale (also when coming back from the tray)
            root.RenderTransformOrigin = new Point(0.5, 0.5);
            root.RenderTransform = new ScaleTransform(1, 1);
            IsVisibleChanged += delegate
            {
                if (!IsVisible || K.AnimScale <= 0) return;
                root.BeginAnimation(OpacityProperty, null); root.Opacity = 0;
                root.BeginAnimation(OpacityProperty, K.Anim(1, 260, K.OutCubic));
                var rs = (ScaleTransform)root.RenderTransform; rs.ScaleX = rs.ScaleY = 0.975;
                K.Pop(rs, 1, 460, K.OutQuint);
            };

            RestoreBounds0();

            // Ctrl+1..7 jumps to a page, Ctrl+Tab / Ctrl+Shift+Tab cycles
            PreviewKeyDown += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                if (e.Key >= Key.D1 && e.Key <= Key.D9) { int n = e.Key - Key.D1; if (n < pages.Count) { Navigate(n, true); e.Handled = true; } }
                else if (e.Key == Key.Tab)
                {
                    int n = (current + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? pages.Count - 1 : 1)) % pages.Count;
                    Navigate(n, true); e.Handled = true;
                }
            };
            Hw.Updated += s => Dispatcher.BeginInvoke(new Action(() => OnSnapshot(s)));
            Hw.KbChanged += k => Dispatcher.BeginInvoke(new Action(() => Keys.OnKb(k)));
        }

        public void ScrollTo(double y) { if (scrollSv != null) scrollSv.ScrollToVerticalOffset(y); }

        // IPC test helper: deliver a key to the window's PreviewKeyDown handlers (no real input injection)
        public void RaiseKey(Key k)
        {
            var src = PresentationSource.FromVisual(this);
            if (src == null) return;
            RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }

        bool hiding;
        void AnimatedHide()
        {
            if (hiding) return;
            if (K.AnimScale <= 0 || WindowState == WindowState.Minimized) { Hide(); return; }
            hiding = true;
            var f = K.Anim(0, 150, K.OutCubic);
            f.Completed += delegate { Hide(); root.BeginAnimation(OpacityProperty, null); root.Opacity = 1; hiding = false; };
            root.BeginAnimation(OpacityProperty, f);
            K.Pop((ScaleTransform)root.RenderTransform, 0.98, 160, K.OutCubic);
        }

        // remember where the window was
        public void SaveBounds()
        {
            var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (r.Width < 100 || double.IsInfinity(r.Width)) return;
            Settings.Set("win", string.Format(K.EN, "{0:0},{1:0},{2:0},{3:0},{4}", r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized ? 1 : 0));
        }
        void RestoreBounds0()
        {
            var p = Settings.Get("win", "").Split(',');
            if (p.Length != 5) return;
            double x, y, w, h;
            if (!double.TryParse(p[0], System.Globalization.NumberStyles.Float, K.EN, out x) || !double.TryParse(p[1], System.Globalization.NumberStyles.Float, K.EN, out y)
                || !double.TryParse(p[2], System.Globalization.NumberStyles.Float, K.EN, out w) || !double.TryParse(p[3], System.Globalization.NumberStyles.Float, K.EN, out h)) return;
            // only if it is still on a visible screen
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!vs.IntersectsWith(new Rect(x + 40, y + 10, Math.Max(10, w - 80), 40))) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = x; Top = y; Width = Math.Max(MinWidth, w); Height = Math.Max(MinHeight, h);
            if (p[4] == "1") WindowState = WindowState.Maximized;
        }

        static ImageSource ToImage(System.Drawing.Icon ic)
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze(); return src;
        }

        Border CapBtn(string glyph, Action click, bool danger)
        {
            var b = new Border { Width = 40, Height = 32, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(2, 0, 0, 0) };
            b.Child = K.Glyph(glyph, 11, Theme.Text2);
            WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            if (danger)
            {
                b.MouseEnter += delegate { b.Background = Theme.Red; ((TextBlock)b.Child).Foreground = Brushes.White; };
                b.MouseLeave += delegate { b.Background = Brushes.Transparent; ((TextBlock)b.Child).Foreground = Theme.Text2; };
                b.MouseLeftButtonUp += (s, e) => click();
            }
            else K.Pressable(b, click, true);
            return b;
        }

        void AddPage(IPage p, string glyph, string label)
        {
            int idx = pages.Count;
            pages.Add(p);
            var g = new Grid { Height = 46 };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var gl = K.Glyph(glyph, 17, Theme.Text2); gl.Width = 22;
            var lb = K.TB(label, 14, Theme.Text2, FontWeights.SemiBold); lb.Margin = new Thickness(14, 0, 0, 0);
            sp.Children.Add(gl); sp.Children.Add(lb);
            g.Children.Add(sp);
            var b = new Border { Child = g, Background = Brushes.Transparent, CornerRadius = new CornerRadius(14), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 4) };
            K.Pressable(b, () => Navigate(idx, true), true);
            navItems.Add(b); navGlyphs.Add(gl); navLabels.Add(lb);
            nav.Children.Add(b);
            p.View.Visibility = Visibility.Collapsed;
            content.Children.Add(p.View);
        }

        public void Navigate(string name)
        {
            string n = name.ToLowerInvariant();
            int i = n.StartsWith("dash") || n == "home" || n.StartsWith("over") ? 0 : n.StartsWith("pow") || n.StartsWith("mode") ? 1 : n.StartsWith("fan") || n.StartsWith("cool") ? 2 : n.StartsWith("bat") || n.StartsWith("charge") ? 3 : n.StartsWith("key") || n.StartsWith("kb") ? 4 : n.StartsWith("pref") ? 5 : n.StartsWith("set") ? 6 : -1;
            if (i < 0) int.TryParse(n, out i);
            Navigate(i, true);
        }

        public void Navigate(int i, bool anim)
        {
            if (i < 0 || i >= pages.Count) return;
            if (i == current) return;
            var prev = current >= 0 ? pages[current] : null;
            current = i;
            var p = pages[i];
            for (int j = 0; j < navItems.Count; j++)
            {
                navGlyphs[j].Foreground = j == i ? Theme.Accent : Theme.Text2;
                navLabels[j].Foreground = j == i ? Theme.Text : Theme.Text2;
            }
            navY.Target = i * 50;
            if (!anim || navIndicator.Opacity == 0 || K.AnimScale <= 0) { navY.Snap(navY.Target); navTT.Y = navY.Value; navIndicator.Opacity = 1; }
            else if (!navLive) { navLive = true; Frame.Add(NavTick); }

            title.Text = p.Title; subtitle.Text = p.Subtitle;
            if (anim && K.AnimScale > 0)
            {
                var tt = new TranslateTransform(0, 6); title.RenderTransform = tt;
                title.Opacity = 0; title.BeginAnimation(OpacityProperty, K.Anim(1, 320, K.OutCubic));
                tt.BeginAnimation(TranslateTransform.YProperty, K.Anim(0, 420, K.OutQuint));
                subtitle.Opacity = 0; var so = K.Anim(1, 320, K.OutCubic); so.BeginTime = TimeSpan.FromMilliseconds(K.Dur(60)); subtitle.BeginAnimation(OpacityProperty, so);
            }
            // pages move with the sidebar: going down the list slides content up, going back slides it down
            int dir = prev == null ? 1 : (i > pages.IndexOf(prev) ? 1 : -1);
            foreach (var other in pages)
                if (other != p && other != prev) { other.View.BeginAnimation(OpacityProperty, null); other.View.Visibility = Visibility.Collapsed; }
            if (prev != null)
            {
                var pv = prev.View;
                if (anim && K.AnimScale > 0)
                {
                    pv.IsHitTestVisible = false;
                    var ott = new TranslateTransform(); pv.RenderTransform = ott;
                    var fade = K.Anim(0, 160, K.OutCubic);
                    fade.Completed += delegate
                    {
                        if (pages[current].View != pv) pv.Visibility = Visibility.Collapsed;
                        pv.BeginAnimation(OpacityProperty, null); pv.Opacity = 1; pv.RenderTransform = null; pv.IsHitTestVisible = true;
                    };
                    pv.BeginAnimation(OpacityProperty, fade);
                    ott.BeginAnimation(TranslateTransform.YProperty, K.Anim(-16 * dir, 200, K.OutCubic));
                }
                else pv.Visibility = Visibility.Collapsed;
            }
            p.View.BeginAnimation(OpacityProperty, null); p.View.Opacity = 1; p.View.RenderTransform = null; p.View.IsHitTestVisible = true;
            p.View.Visibility = Visibility.Visible;
            p.Update(Hw.Last);
            p.OnShown();
            if (anim) { Motion.Stagger(p.StaggerRoot, 22 * dir, 45); K.PopIn(navGlyphs[i], 0.55, 520); }
            AttachScroll(p.View as ScrollViewer);
        }

        // ---------------------------------------------------------------- overlay scrollbar
        // A thin thumb that appears while scrolling (or when hovered) and fades away; draggable.
        Border thumb; ScrollViewer scrollSv; System.Windows.Threading.DispatcherTimer thumbHide; bool thumbDrag; double dragY0, dragOff0;

        void BuildThumb()
        {
            thumb = new Border { Width = 6, CornerRadius = new CornerRadius(3), Background = Theme.Text3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 8, 0), Opacity = 0, Cursor = Cursors.Hand };
            Grid.SetRow(thumb, 0);
            content.Children.Add(thumb);
            Panel.SetZIndex(thumb, 100);
            thumbHide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            thumbHide.Tick += delegate { thumbHide.Stop(); if (!thumb.IsMouseOver && !thumbDrag) thumb.BeginAnimation(OpacityProperty, K.Anim(0, 380, K.OutCubic)); };
            thumb.MouseEnter += delegate { thumb.BeginAnimation(OpacityProperty, K.Anim(0.9, 140, K.OutCubic)); thumb.BeginAnimation(WidthProperty, K.Anim(8, 160, K.OutCubic)); };
            thumb.MouseLeave += delegate { thumb.BeginAnimation(WidthProperty, K.Anim(6, 220, K.OutCubic)); if (!thumbDrag) ShowThumb(); };
            thumb.MouseLeftButtonDown += (s, e) => { if (scrollSv == null) return; thumbDrag = true; dragY0 = e.GetPosition(content).Y; dragOff0 = scrollSv.VerticalOffset; thumb.CaptureMouse(); e.Handled = true; };
            thumb.MouseMove += (s, e) =>
            {
                if (!thumbDrag || scrollSv == null) return;
                double track = content.ActualHeight - 16 - thumb.ActualHeight;
                if (track <= 0) return;
                double dy = e.GetPosition(content).Y - dragY0;
                scrollSv.ScrollToVerticalOffset(dragOff0 + dy / track * scrollSv.ScrollableHeight);
            };
            thumb.MouseLeftButtonUp += (s, e) => { thumbDrag = false; thumb.ReleaseMouseCapture(); ShowThumb(); };
            content.SizeChanged += delegate { PlaceThumb(); };
        }

        readonly List<ScrollViewer> hooked = new List<ScrollViewer>();
        void AttachScroll(ScrollViewer sv)
        {
            if (thumb == null) BuildThumb();
            scrollSv = sv;
            if (sv != null && !hooked.Contains(sv))
            {
                hooked.Add(sv);
                sv.ScrollChanged += (s, e) => { if (s == scrollSv) { PlaceThumb(); if (e.VerticalChange != 0) ShowThumb(); } };
            }
            thumb.BeginAnimation(OpacityProperty, null); thumb.Opacity = 0;
            PlaceThumb();
        }

        void PlaceThumb()
        {
            var sv = scrollSv;
            if (thumb == null) return;
            if (sv == null || sv.ScrollableHeight <= 1) { thumb.Visibility = Visibility.Collapsed; return; }
            thumb.Visibility = Visibility.Visible;
            double track = content.ActualHeight - 16;
            double h = Math.Max(36, track * sv.ViewportHeight / Math.Max(1, sv.ExtentHeight));
            thumb.Height = h;
            thumb.Margin = new Thickness(0, 8 + (track - h) * sv.VerticalOffset / sv.ScrollableHeight, 8, 0);
        }

        void ShowThumb()
        {
            if (thumb.Visibility != Visibility.Visible) return;
            thumb.BeginAnimation(OpacityProperty, K.Anim(thumb.IsMouseOver || thumbDrag ? 0.9 : 0.5, 120, K.OutCubic));
            thumbHide.Stop(); thumbHide.Start();
        }

        // ---------------------------------------------------------------- in-app dialog
        public void Confirm(string title, string body, string yes, Action<bool> done)
        {
            var layer = new Grid();
            Grid.SetColumnSpan(layer, 2);
            var dim = new Border { Background = K.Solid(Color.FromArgb(150, 0, 0, 0)), Opacity = 0 };
            layer.Children.Add(dim);
            var card = new Border { Width = 420, CornerRadius = new CornerRadius(22), Background = Theme.Surface, BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1), Padding = new Thickness(26, 24, 26, 22), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            card.Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.5 };
            var sp = new StackPanel();
            var t = K.TB(title, 18, Theme.Text, FontWeights.Bold); t.FontFamily = K.Display;
            var b = K.TB(body, 13, Theme.Text2, FontWeights.Normal); b.TextWrapping = TextWrapping.Wrap; b.TextTrimming = TextTrimming.None; b.Margin = new Thickness(0, 8, 0, 22);
            sp.Children.Add(t); sp.Children.Add(b);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bool closed = false;
            Action<bool> close = null;
            close = ok =>
            {
                if (closed) return; closed = true;
                PreviewKeyDown -= keyHandler;
                var f = K.Anim(0, 160, K.OutCubic);
                f.Completed += delegate { root.Children.Remove(layer); };
                dim.BeginAnimation(OpacityProperty, f);
                card.BeginAnimation(OpacityProperty, K.Anim(0, 140, K.OutCubic));
                K.Pop((ScaleTransform)card.RenderTransform, 0.96, 160, K.OutCubic);
                if (done != null) done(ok);
            };
            var no = K.PillBtn("Cancel", Theme.Raised, Theme.Text, 38, () => close(false)); no.Margin = new Thickness(0, 0, 10, 0);
            var yesB = K.PillBtn(yes, Theme.AccentFill, Theme.OnAccent, 38, () => close(true));
            btns.Children.Add(no); btns.Children.Add(yesB);
            sp.Children.Add(btns);
            card.Child = sp;
            layer.Children.Add(card);
            dim.MouseLeftButtonUp += (s, e) => close(false);
            root.Children.Add(layer);
            Panel.SetZIndex(layer, 200);
            dialogKeys = (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; close(false); } else if (e.Key == Key.Enter) { e.Handled = true; close(true); } };
            keyHandler = dialogKeys;
            PreviewKeyDown += keyHandler;
            dim.BeginAnimation(OpacityProperty, K.Anim(1, 200, K.OutCubic));
            card.BeginAnimation(OpacityProperty, K.Anim(1, 200, K.OutCubic));
            K.PopIn(card, 0.92, 420);
        }
        KeyEventHandler keyHandler, dialogKeys;

        bool NavTick(double dt)
        {
            navY.Step(dt); navTT.Y = navY.Value;
            double stretch = Math.Min(10, Math.Abs(navY.Vel) * 0.012);
            navIndicator.Height = 46 + stretch;
            if (navY.Settle()) { navTT.Y = navY.Value; navIndicator.Height = 46; navLive = false; return false; }
            return true;
        }

        void OnSnapshot(Snapshot s)
        {
            foreach (var p in pages) p.Update(s);
            RefreshPolicy.Observe(s);
            var m = Modes.Of(s.Mode);
            statusMode.Text = s.Ok ? (m != null ? m.Name + " mode" : "Mode " + s.Mode) : "Sensors unavailable";
            statusTemp.Text = s.Ok ? string.Format(K.EN, "CPU {0}°  ·  Fan {1:N0} rpm", s.CpuTemp, s.Fans[0].Rpm) : Dchu.Error;
            statusDot.Background = s.Ok ? (s.CpuTemp >= 85 ? Theme.Red : s.CpuTemp >= 70 ? Theme.Orange : Theme.Green) : Theme.Red;
            var dc = ((SolidColorBrush)statusDot.Background).Color;
            haloBrush.GradientStops[0].Color = Theme.A(dc, 0.75); haloBrush.GradientStops[1].Color = Theme.A(dc, 0);
            if (Program.Tray != null)
            {
                string tip = s.Ok ? string.Format("ControlHub · {0} · CPU {1}°C", m != null ? m.Name : "", s.CpuTemp) : "ControlHub";
                Program.Tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            if (PendingMode >= 0)
            {
                bool match = s.Mode == PendingMode || (PendingMode == 4 && s.Mode == 0);
                if (match)
                {
                    var pm = Modes.Of(PendingMode);
                    PendingMode = -1;
                    Island.Show(pm.Glyph, pm.Name + " mode active", K.Solid(pm.Col()));
                }
                else if ((DateTime.Now - pendingAt).TotalSeconds > 8)
                {
                    PendingMode = -1;
                    Island.Show("", "Mode did not change — check the FnKey service", Theme.Orange);
                }
            }
        }

        public void RequestMode(int mode)
        {
            var m = Modes.Of(mode);
            if (m == null) return;
            if (Modes.Of(Hw.Last.Mode) == m) { Island.Show(m.Glyph, "Already in " + m.Name + " mode", K.Solid(m.Col())); return; }
            PendingMode = mode; pendingAt = DateTime.Now;
            Island.Show("", "Applying " + m.Name + " mode…", K.Solid(m.Col()));
            Hw.SetPowerMode(mode);
            Hw.Poke();
            Power.Optimistic(mode); Dash.Optimistic(mode);
        }

        // Renders the window (works while it is behind other windows).
        public string Shot(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) path = Path.Combine(AppDir.Data, "shot.png");
                var src = PresentationSource.FromVisual(this);
                double dpi = src != null ? 96 * src.CompositionTarget.TransformToDevice.M11 : 96;
                var el = (FrameworkElement)Content;
                int w = (int)Math.Ceiling(el.ActualWidth * dpi / 96), h = (int)Math.Ceiling(el.ActualHeight * dpi / 96);
                var rtb = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Theme.Bg, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                    dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                }
                rtb.Render(dv);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var f = File.Create(path)) enc.Save(f);
                return path;
            }
            catch (Exception ex) { return "shot error: " + ex.Message; }
        }
    }
}
