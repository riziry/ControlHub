// Read-only probe: calls only the read functions CC40 itself calls routinely.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

static class Probe
{
    static readonly string Dir = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, @"..\lib");

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string p);
    [DllImport("InsydeDCHU.dll")] static extern int GetDCHU_Data_Integer(int cmd, ref int data);
    [DllImport("InsydeDCHU.dll")] static extern int GetDCHU_Data_Buffer(int cmd, ref byte buf);
    [DllImport("InsydeDCHU.dll")] static extern int ReadAppSettings(int page, int off, int len, ref byte buf);

    static StringBuilder sb = new StringBuilder();
    static void L(string s) { sb.AppendLine(s); Console.WriteLine(s); }

    static string Hex(byte[] b, int n)
    {
        var s = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            if (i % 16 == 0) s.Append("\n  " + i.ToString("X2") + ": ");
            s.Append(b[i].ToString("X2")).Append(' ');
        }
        return s.ToString();
    }

    static int Main(string[] args)
    {
        string outp = args.Length > 0 ? args[0] : "probe_out.txt";
        try
        {
            SetDllDirectory(Dir);
            L("time " + DateTime.Now.ToString("s"));
            foreach (int cmd in new[] { 12, 13 })
            {
                var b = new byte[256];
                int r = GetDCHU_Data_Buffer(cmd, ref b[0]);
                L("BUF " + cmd + " ret=" + r + Hex(b, 256));
            }
            foreach (int cmd in new[] { 122, 16, 96 })
            {
                int v = 0; int r = GetDCHU_Data_Integer(cmd, ref v);
                L("INT " + cmd + " ret=" + r + " val=" + v + " (0x" + v.ToString("X8") + ")");
            }
            foreach (int page in new[] { 1, 2, 4, 7 })
            {
                var b = new byte[256];
                int r = ReadAppSettings(page, 0, 256, ref b[0]);
                L("APP page " + page + " ret=" + r + Hex(b, 256));
            }
            // fan sample over a few seconds
            for (int i = 0; i < 3; i++)
            {
                var b = new byte[256];
                GetDCHU_Data_Buffer(12, ref b[0]);
                int t1 = b[3] + (b[2] << 8), t2 = b[5] + (b[4] << 8);
                L(string.Format("FAN sample cpuT={0} gpuT={1} f1raw={2} f2raw={3} rpm1={4:0} rpm2={5:0} duty1={6} duty2={7}",
                    b[18], b[21], t1, t2, t1 == 0 ? 0 : 120.0 / (5.565217391304348E-05 * t1), t2 == 0 ? 0 : 120.0 / (5.565217391304348E-05 * t2), b[16], b[19]));
                System.Threading.Thread.Sleep(1000);
            }
            DumpReg(Registry.Users.OpenSubKey(@"S-1-5-18\Software\ControlCenter2.0"), "HKU\\S-1-5-18\\Software\\ControlCenter2.0", 0);
            DumpReg(Registry.CurrentUser.OpenSubKey(@"Software\ControlCenter3.0"), "HKCU\\Software\\ControlCenter3.0", 0);
            DumpReg(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\ControlCenterAP"), "HKLM32\\SOFTWARE\\ControlCenterAP", 0);
        }
        catch (Exception ex) { L("ERROR " + ex); }
        File.WriteAllText(outp, sb.ToString());
        return 0;
    }

    static void DumpReg(RegistryKey k, string name, int depth)
    {
        if (k == null) { L("REG " + name + " (missing)"); return; }
        if (depth > 4) return;
        var line = new StringBuilder("REG " + name + " ::");
        foreach (var v in k.GetValueNames())
        {
            object o = k.GetValue(v);
            string s = o is byte[] ? "bin[" + ((byte[])o).Length + "]" : Convert.ToString(o);
            if (s.Length > 60) s = s.Substring(0, 60) + "â€¦";
            line.Append(" " + v + "=" + s + ";");
        }
        L(line.ToString());
        foreach (var sub in k.GetSubKeyNames())
        {
            using (var sk = k.OpenSubKey(sub)) DumpReg(sk, name + "\\" + sub, depth + 1);
        }
    }
}

