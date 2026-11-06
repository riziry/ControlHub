// Preferences: special keys and devices, exactly like the OEM Control Center "Preference" box.
//   Fn-key lock / Win-key lock / Swap Win-Fn : app 292/293/294 (Fn & Swap: 1 on, 2 off; Win: 2 on, 1 off)
//                                             + package cmd 4 [25, fn, win, swap, 0, 0]
//   Touch pad  : Windows' own toggle shortcut Ctrl+Win+F24, FnKey notified ("clevofnkey^206^n"), app 266
//   Camera     : SetCamera (cmd 34) + app 262 (1 on, 0 off) + "cc40^f7"/"cc40^f6" for the OSD
//   Airplane   : 121/26 + InsydeAirplanemodehid.SetAirplaneMode + cmd 109
//   Caps/Num/Scroll lock : the lock keys themselves
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;

namespace ControlHub
{
    class PrefState
    {
        public bool FnLock, WinLock, Swap, Touchpad, Camera, Airplane, Ok;
    }

    static class Prefs
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        [DllImport("InsydeAirplanemodehid.dll", EntryPoint = "SetAirplaneMode")] static extern bool SetAirplaneMode(int on);

        public static event Action<PrefState> Changed;
        public static PrefState State = new PrefState();

        static bool TouchpadOn()
        {
            try { return Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status", "Enabled", 1)) == 1; }
            catch { return true; }
        }
        static bool AirplaneOn()
        {
            try { return Convert.ToInt32(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState", "", 0)) != 0; }
            catch { return false; }
        }

        static void ReadNow()
        {
            var p = new PrefState();
            if (Dchu.Available)
            {
                var k = Dchu.ReadApp(292, 3);
                p.FnLock = k[0] == 1; p.WinLock = k[1] == 2; p.Swap = k[2] == 1;
                p.Camera = Dchu.ReadApp(262, 1)[0] == 1;
                p.Ok = true;
            }
            p.Touchpad = TouchpadOn();
            p.Airplane = AirplaneOn();
            State = p;
            var h = Changed; if (h != null) h(p);
        }
        public static void Refresh() { Hw.Run(ReadNow); }

        // Fn lock, Win lock and Swap are always written together (CC40 does the same).
        public static void SetKeys(bool fnLock, bool winLock, bool swap)
        {
            Hw.Run(delegate
            {
                byte fn = (byte)(fnLock ? 1 : 2), win = (byte)(winLock ? 2 : 1), sw = (byte)(swap ? 1 : 2);
                Dchu.WriteApp(292, new[] { fn, win, sw });
                Dchu.Package(4, new byte[] { 25, fn, win, sw, 0, 0 });
                ReadNow();
            });
        }

        public static void SetTouchpad(bool on)
        {
            Hw.Run(delegate
            {
                if (TouchpadOn() != on)
                {
                    Log.Write("touchpad -> " + on + " (Ctrl+Win+F24)" + (Dchu.DryRun ? " [dry]" : ""));
                    if (!Dchu.DryRun)
                    {
                        keybd_event(17, 0, 0, IntPtr.Zero); keybd_event(91, 0, 0, IntPtr.Zero); keybd_event(135, 0, 0, IntPtr.Zero);
                        keybd_event(17, 0, 2, IntPtr.Zero); keybd_event(91, 0, 2, IntPtr.Zero); keybd_event(135, 0, 2, IntPtr.Zero);
                    }
                    Notify("clevofnkey^206^" + (on ? 1 : 0));
                    Dchu.WriteApp(266, new[] { (byte)(on ? 1 : 0) });
                }
                System.Threading.Thread.Sleep(300);
                ReadNow();
            });
        }

        public static void SetCamera(bool on)
        {
            Hw.Run(delegate
            {
                int v = on ? 1 : 0;
                Dchu.WriteApp(262, new[] { (byte)v });
                Dchu.Write(34, 0, (uint)v);
                Notify(on ? "cc40^f7" : "cc40^f6");
                ReadNow();
            });
        }

        public static void SetAirplane(bool on)
        {
            Hw.Run(delegate
            {
                if (AirplaneOn() != on)
                {
                    Dchu.Write(121, 26, on ? 1u : 0u);
                    Log.Write("SetAirplaneMode(" + (on ? 1 : 0) + ")" + (Dchu.DryRun ? " [dry]" : ""));
                    if (!Dchu.DryRun) { try { SetAirplaneMode(on ? 1 : 0); } catch (Exception ex) { Log.Write("airplane dll: " + ex.Message); } }
                    Dchu.Write(109, 0, 0);
                }
                System.Threading.Thread.Sleep(500);
                ReadNow();
            });
        }

        // CC40: keybd_event(vk, 69, EXTENDEDKEY) then (EXTENDEDKEY | KEYUP)
        public static void ToggleLock(byte vk)
        {
            Log.Write("toggle lock key " + vk);
            keybd_event(vk, 69, 1, IntPtr.Zero);
            keybd_event(vk, 69, 3, IntPtr.Zero);
        }

        static void Notify(string msg)
        {
            Log.Write("EVENT " + msg + (Dchu.DryRun ? " [dry]" : ""));
            if (Dchu.DryRun) return;
            try { using (var ev = new System.Diagnostics.EventLog { Source = "PowerBIOSServer_Out", Log = "OutLog" }) ev.WriteEntry(msg); }
            catch (Exception ex) { Log.Write("event failed: " + ex.Message); }
        }
    }

    // A big toggle tile: icon (glyph or keycap text), name, state; glows when on.
    class PrefTile : Border
    {
        readonly TextBlock icon, name, state; readonly Border iconBox;
        bool on; readonly bool keycap;
        public Action Clicked;
        public PrefTile(string glyphOrCap, bool keycap, string title)
        {
            this.keycap = keycap;
            Width = 150; Height = 150; CornerRadius = new CornerRadius(22); Margin = new Thickness(7);
            Background = Theme.Raised; BorderBrush = Theme.Hairline; BorderThickness = new Thickness(1.5); Cursor = Cursors.Hand;
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            iconBox = new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(keycap ? 12 : 29), HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(keycap ? 2 : 0) };
            if (keycap)
            {
                icon = K.TB(glyphOrCap, 15, Theme.Text2, FontWeights.Bold); icon.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else icon = K.Glyph(glyphOrCap, 24, Theme.Text2);
            iconBox.Child = icon;
            sp.Children.Add(iconBox);
            name = K.TB(title, 13.5, Theme.Text, FontWeights.SemiBold); name.HorizontalAlignment = HorizontalAlignment.Center; name.Margin = new Thickness(0, 14, 0, 0);
            state = K.TB("", 12, Theme.Text3, FontWeights.Medium); state.HorizontalAlignment = HorizontalAlignment.Center; state.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(name); sp.Children.Add(state);
            Child = sp;
            K.Pressable(this, () => { if (Clicked != null) Clicked(); }, true);
            Set(false, false);
        }
        public void Set(bool v, bool anim)
        {
            on = v;
            var a = Theme.Current.Accent;
            icon.Foreground = v ? (Brush)K.Solid(a) : Theme.Text3;
            iconBox.Background = v ? K.Solid(Theme.A(a, 0.16)) : K.Solid(Theme.A(Theme.Current.Text, 0.04));
            iconBox.BorderBrush = keycap ? (v ? K.Solid(Theme.A(a, 0.8)) : Theme.Hairline) : null;
            BorderBrush = v ? K.Solid(Theme.A(a, 0.55)) : Theme.Hairline;
            Effect = v ? new DropShadowEffect { Color = a, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.35 } : null;
            state.Text = v ? "On" : "Off";
            state.Foreground = v ? Theme.Accent : Theme.Text3;
            if (anim) K.PopIn(iconBox, 0.85, 420);
        }
        public bool IsOn { get { return on; } }
    }

    class PrefsPage : IPage
    {
        readonly MainWindow w;
        readonly StackPanel stack = new StackPanel { Margin = new Thickness(36, 0, 36, 36) };
        readonly ScrollViewer view;
        readonly PrefTile winLock, swap, fnLock, touch, camera, plane, caps, num, scr;
        readonly System.Windows.Threading.DispatcherTimer poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        PrefState shown;

        public string Title { get { return "Preferences"; } }
        public string Subtitle { get { return "Special keys and built-in devices"; } }
        public FrameworkElement View { get { return view; } }
        public Panel StaggerRoot { get { return stack; } }

        public PrefsPage(MainWindow w)
        {
            this.w = w;
            view = UI.Scroll(stack);

            var keys = new WrapPanel { Margin = new Thickness(-7, 10, 0, 0) };
            winLock = Tile(keys, "", false, "Win key lock", () => { var s = Prefs.State; Prefs.SetKeys(s.FnLock, !s.WinLock, s.Swap); Toast("Win key lock", !s.WinLock); });
            swap = Tile(keys, "", false, "Swap Win-Fn", () => { var s = Prefs.State; Prefs.SetKeys(s.FnLock, s.WinLock, !s.Swap); Toast("Swap Win-Fn", !s.Swap); });
            fnLock = Tile(keys, "Fn", true, "Fn-key lock", () => { var s = Prefs.State; Prefs.SetKeys(!s.FnLock, s.WinLock, s.Swap); Toast("Fn-key lock", !s.FnLock); });
            var kc = new StackPanel();
            kc.Children.Add(UI.Header("", "Keys", null));
            var kn = K.TB("Fn-key lock makes F1–F12 act as Fn shortcuts without holding Fn. Win key lock disables the Windows key (handy in games).", 12.5, Theme.Text3, FontWeights.Medium);
            kn.Margin = new Thickness(0, 10, 0, 0); kn.TextWrapping = TextWrapping.Wrap; kn.TextTrimming = TextTrimming.None;
            kc.Children.Add(kn);
            kc.Children.Add(keys);
            stack.Children.Add(K.Card(kc, new Thickness(22)));

            var dev = new WrapPanel { Margin = new Thickness(-7, 10, 0, 0) };
            touch = Tile(dev, "", false, "Touch pad", () => { bool v = !Prefs.State.Touchpad; Prefs.SetTouchpad(v); Toast("Touch pad", v); });
            camera = Tile(dev, "", false, "Camera", () => { bool v = !Prefs.State.Camera; Prefs.SetCamera(v); Toast("Camera", v); });
            plane = Tile(dev, "", false, "Airplane", () => { bool v = !Prefs.State.Airplane; Prefs.SetAirplane(v); Toast("Airplane mode", v); });
            var dc = new StackPanel();
            dc.Children.Add(UI.Header("", "Devices", null));
            dc.Children.Add(dev);
            var dcard = K.Card(dc, new Thickness(22)); dcard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(dcard);

            var locks = new WrapPanel { Margin = new Thickness(-7, 10, 0, 0) };
            caps = Tile(locks, "Caps", true, "Caps Lock", () => Prefs.ToggleLock(20));
            num = Tile(locks, "Num", true, "Num Lock", () => Prefs.ToggleLock(144));
            scr = Tile(locks, "Scr", true, "Scroll Lock", () => Prefs.ToggleLock(145));
            var lc = new StackPanel();
            lc.Children.Add(UI.Header("", "Lock keys", null));
            lc.Children.Add(locks);
            var lcard = K.Card(lc, new Thickness(22)); lcard.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(lcard);

            Prefs.Changed += p => w.Dispatcher.BeginInvoke(new Action(() => Show(p)));
            poll.Tick += delegate { Prefs.Refresh(); ShowLocks(); };
            view.IsVisibleChanged += delegate { if (view.IsVisible) { Prefs.Refresh(); ShowLocks(); poll.Start(); } else poll.Stop(); };
        }

        PrefTile Tile(Panel host, string glyph, bool cap, string title, Action click)
        {
            var t = new PrefTile(glyph, cap, title) { Clicked = click };
            host.Children.Add(t);
            return t;
        }

        void Toast(string what, bool on) { w.Island.Show(on ? "" : "", what + (on ? " on" : " off"), on ? Theme.Accent : Theme.Text3); }

        void Show(PrefState p)
        {
            bool first = shown == null;
            Apply(winLock, p.WinLock, first ? (bool?)null : shown.WinLock);
            Apply(swap, p.Swap, first ? (bool?)null : shown.Swap);
            Apply(fnLock, p.FnLock, first ? (bool?)null : shown.FnLock);
            Apply(touch, p.Touchpad, first ? (bool?)null : shown.Touchpad);
            Apply(camera, p.Camera, first ? (bool?)null : shown.Camera);
            Apply(plane, p.Airplane, first ? (bool?)null : shown.Airplane);
            shown = p;
        }
        static void Apply(PrefTile t, bool v, bool? before) { if (before == null || before.Value != v) t.Set(v, before != null); }

        void ShowLocks()
        {
            Lock(caps, Keyboard.IsKeyToggled(Key.CapsLock));
            Lock(num, Keyboard.IsKeyToggled(Key.NumLock));
            Lock(scr, Keyboard.IsKeyToggled(Key.Scroll));
        }
        static void Lock(PrefTile t, bool v) { if (t.IsOn != v) t.Set(v, true); }

        public void Update(Snapshot s) { }
        public void OnShown() { Prefs.Refresh(); ShowLocks(); }

        // tiles copy theme colours when they change state; repaint them for the new theme
        public void ThemeChanged()
        {
            foreach (var t in new[] { winLock, swap, fnLock, touch, camera, plane, caps, num, scr }) t.Set(t.IsOn, false);
        }

        public string Ipc(string arg)
        {
            var s = Prefs.State;
            var a = arg.Split(' ');
            if (a[0] == "set" && a.Length == 3)
            {
                bool v = a[2] == "on";
                if (a[1] == "fn") Prefs.SetKeys(v, s.WinLock, s.Swap);
                else if (a[1] == "win") Prefs.SetKeys(s.FnLock, v, s.Swap);
                else if (a[1] == "swap") Prefs.SetKeys(s.FnLock, s.WinLock, v);
                else return "pref set fn|win|swap on|off";
                return "ok";
            }
            if (arg == "state") return "fnlock=" + s.FnLock + " winlock=" + s.WinLock + " swap=" + s.Swap + " touchpad=" + s.Touchpad + " camera=" + s.Camera + " airplane=" + s.Airplane;
            return "pref state";
        }
    }
}
