// Measures the cost of the keyboard LED write by re-sending the CURRENT level value (no visible change).
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

static class LedTiming
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string p);
    [DllImport("InsydeDCHU.dll")] static extern int SetDCHU_Data(int cmd, byte[] buf, int len);

    static void Main(string[] args)
    {
        SetDllDirectory(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, @"..\lib"));
        uint value = uint.Parse(args[0]);
        var b = BitConverter.GetBytes(value); b[3] = 244;
        SetDCHU_Data(103, b, 4);
        var p = Process.GetCurrentProcess();
        var cpu0 = p.TotalProcessorTime; var sw = Stopwatch.StartNew();
        int n = 20;
        for (int i = 0; i < n; i++) { var c = (byte[])b.Clone(); SetDCHU_Data(103, c, 4); }
        sw.Stop(); p.Refresh();
        Console.WriteLine("SetDCHU_Data(103,244,{0}): wall {1:N1} ms/call, cpu {2:N1} ms/call", value, sw.Elapsed.TotalMilliseconds / n, (p.TotalProcessorTime - cpu0).TotalMilliseconds / n);
    }
}
