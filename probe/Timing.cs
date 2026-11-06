using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

static class Timing
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string p);
    [DllImport("InsydeDCHU.dll")] static extern int GetDCHU_Data_Buffer(int cmd, ref byte buf);
    [DllImport("InsydeDCHU.dll")] static extern int ReadAppSettings(int page, int off, int len, ref byte buf);

    static void Main()
    {
        SetDllDirectory(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, @"..\lib"));
        var b = new byte[256];
        GetDCHU_Data_Buffer(12, ref b[0]);
        var p = Process.GetCurrentProcess();
        Measure("GetDCHU_Data_Buffer(12)", () => GetDCHU_Data_Buffer(12, ref b[0]));
        Measure("ReadAppSettings(1,1,1)", () => ReadAppSettings(1, 1, 1, ref b[0]));
        var pc = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); pc.NextValue();
        Measure("PerfCounter NextValue", () => pc.NextValue());
    }

    static void Measure(string name, Action a)
    {
        var p = Process.GetCurrentProcess();
        var cpu0 = p.TotalProcessorTime; var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) a();
        sw.Stop(); p.Refresh();
        Console.WriteLine("{0,-26} wall {1,7:N1} ms/call   cpu {2,7:N1} ms/call", name, sw.Elapsed.TotalMilliseconds / 20, (p.TotalProcessorTime - cpu0).TotalMilliseconds / 20);
    }
}
