// Read-only: decode GetDCHU_Data_Buffer(18) the way CC40's CPU OC page does.
using System;
using System.Runtime.InteropServices;

static class Wmi18
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string p);
    [DllImport("InsydeDCHU.dll")] static extern int GetDCHU_Data_Buffer(int cmd, ref byte buf);

    static ushort U16(byte[] a, int i) { return (ushort)((a[i + 1] << 8) + a[i]); }
    static uint U32(byte[] a, int i) { return (uint)((a[i + 3] << 24) + (a[i + 2] << 16) + (a[i + 1] << 8) + a[i]); }

    static void Main()
    {
        SetDllDirectory(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, @"..\lib"));
        var a = new byte[256];
        int r = GetDCHU_Data_Buffer(18, ref a[0]);
        Console.WriteLine("ret=" + r + " id=" + U16(a, 0));
        Console.WriteLine("PL1 raw=" + U32(a, 8) + "  PL2 raw=" + U32(a, 12) + "  u16@16=" + U16(a, 16) + " @18=" + U16(a, 18) + " @20=" + U16(a, 20));
        Console.Write("list@22: "); for (int i = 22; i < 46; i += 2) Console.Write(U16(a, i) + " "); Console.WriteLine();
        foreach (int b in new[] { 48, 80, 112, 144 })
        {
            Console.Write("block@" + b + ": ");
            for (int k = 0; k < 12; k++) Console.Write(U16(a, b + k * 2) + " ");
            Console.WriteLine();
        }
        Console.WriteLine("PLtime@219=" + U16(a, 219) + " VR@221=" + U32(a, 221) + " ratios@208.. " + a[208] + " " + a[209] + " " + a[210]);
        for (int i = 0; i < 256; i += 16) { Console.Write(i.ToString("X2") + ": "); for (int j = 0; j < 16; j++) Console.Write(a[i + j].ToString("X2") + " "); Console.WriteLine(); }
    }
}
