// Read-only: FlexiCharger state (package cmd 4 sub 30) + app 302.
using System;
using System.Runtime.InteropServices;

static class Charge
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string p);
    [DllImport("InsydeDCHU.dll")] static extern int SetDCHU_DataEx(int cmd, byte[] buf, int len, ref byte outBuf);
    [DllImport("InsydeDCHU.dll")] static extern int ReadAppSettings(int page, int off, int len, ref byte buf);

    static void Main()
    {
        SetDllDirectory(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, @"..\lib"));
        var input = new byte[256]; input[0] = 30;
        var o = new byte[256];
        int r = SetDCHU_DataEx(4, input, 256, ref o[0]);
        Console.WriteLine("ret=" + r + " status=" + o[0] + " start=" + o[1] + " stop=" + o[2]);
        Console.Write("allowed start:"); for (int i = 16; i < 32; i++) if (o[i] != 0) Console.Write(" " + o[i]); Console.WriteLine();
        Console.Write("allowed stop: "); for (int i = 32; i < 48; i++) if (o[i] != 0) Console.Write(" " + o[i]); Console.WriteLine();
        var b = new byte[1]; ReadAppSettings(1, 46, 1, ref b[0]);
        Console.WriteLine("app302 (choice) = " + b[0]);
    }
}
