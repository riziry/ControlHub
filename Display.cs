// Optional ControlHub behaviour: keep the low refresh rate of Quiet / Battery Saver on the charger too.
// The OEM FnKey service only lowers it on battery (modes 1 and 4) and restores the highest rate in
// modes 2 and 3; this just extends that to "always", using the normal Windows display API with a
// dynamic (not saved) change, so a restart always returns to the Windows setting.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ControlHub
{
    static class Display
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
        const int ENUM_CURRENT = -1, DM_DISPLAYFREQUENCY = 0x400000;

        static DEVMODE New() { var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return d; }

        public static int Current()
        {
            var d = New();
            return EnumDisplaySettings(null, ENUM_CURRENT, ref d) ? d.dmDisplayFrequency : 0;
        }

        // refresh rates offered at the current resolution and colour depth
        public static List<int> Rates()
        {
            var cur = New(); var list = new List<int>();
            if (!EnumDisplaySettings(null, ENUM_CURRENT, ref cur)) return list;
            for (int i = 0; ; i++)
            {
                var d = New();
                if (!EnumDisplaySettings(null, i, ref d)) break;
                if (d.dmPelsWidth == cur.dmPelsWidth && d.dmPelsHeight == cur.dmPelsHeight && d.dmBitsPerPel == cur.dmBitsPerPel && !list.Contains(d.dmDisplayFrequency) && d.dmDisplayFrequency > 1)
                    list.Add(d.dmDisplayFrequency);
            }
            list.Sort();
            return list;
        }

        public static bool Set(int hz)
        {
            var d = New();
            if (!EnumDisplaySettings(null, ENUM_CURRENT, ref d)) return false;
            if (d.dmDisplayFrequency == hz) return true;
            d.dmDisplayFrequency = hz; d.dmFields = DM_DISPLAYFREQUENCY;
            int r = ChangeDisplaySettingsEx(null, ref d, IntPtr.Zero, 0, IntPtr.Zero);   // 0 = dynamic, not saved
            Log.Write("refresh rate -> " + hz + " Hz, result " + r);
            return r == 0;
        }
    }

    static class RefreshPolicy
    {
        public static bool Always { get { return Settings.Bool("lowRefreshAlways", false); } }
        static int lastMode = -1; static bool lastAc; static DispatcherTimer pending;

        static bool LowMode(int m) { return m == 0 || m == 1 || m == 4; }

        // called with every snapshot on the UI thread
        public static void Observe(Snapshot s)
        {
            if (!s.Ok || s.Mode < 0) return;
            bool changed = s.Mode != lastMode || s.OnAc != lastAc;
            lastMode = s.Mode; lastAc = s.OnAc;
            if (changed && Always && s.OnAc) Schedule();
        }

        public static void SetAlways(bool on)
        {
            Settings.Set("lowRefreshAlways", on);
            var s = Hw.Last;
            if (!s.Ok || !s.OnAc) return;          // on battery FnKey already does the right thing
            if (on) Schedule();
            else if (LowMode(s.Mode)) Apply(false); // back to what FnKey does on the charger: full rate
        }

        // wait for FnKey to finish its own refresh-rate step after a mode / charger change
        static void Schedule()
        {
            if (pending == null) { pending = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) }; pending.Tick += delegate { pending.Stop(); var s = Hw.Last; if (Always && s.OnAc) Apply(LowMode(s.Mode)); }; }
            pending.Stop(); pending.Start();
        }

        static void Apply(bool low)
        {
            var rates = Display.Rates();
            if (rates.Count < 2) return;
            int target = low ? (rates.Contains(60) ? 60 : rates[0]) : rates[rates.Count - 1];
            if (Display.Current() != target) Display.Set(target);
        }
    }
}
