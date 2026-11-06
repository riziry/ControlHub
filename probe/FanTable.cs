// Read-only: decode the EC fan tables exactly like CC40/FnKey (package cmd 4, sub 40).
using System;
using System.Runtime.InteropServices;

static class FanTable
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string p);
    [DllImport("InsydeDCHU.dll")] static extern int SetDCHU_DataEx(int cmd, byte[] buf, int len, ref byte outBuf);

    static void Main()
    {
        SetDllDirectory(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, @"..\lib"));
        var input = new byte[256]; input[0] = 40;
        var o = new byte[256];
        int r = SetDCHU_DataEx(4, input, 256, ref o[0]);
        Console.WriteLine("ret=" + r);
        foreach (int baseOff in new[] { 0, 128 })
        {
            Console.WriteLine(baseOff == 0 ? "== CPU fan block" : "== GPU fan block");
            Console.WriteLine(string.Format("id=0x{0:X2} hdr1..7={1}  rpmA={2} FanMax_RPM={3} FanMax_Turbo_RPM={4}",
                o[baseOff], BitConverter.ToString(o, baseOff + 1, 7), (o[baseOff + 8] << 8) + o[baseOff + 9], (o[baseOff + 10] << 8) + o[baseOff + 11], (o[baseOff + 12] << 8) + o[baseOff + 13]));
            for (int j = 0; j < 10; j++)
            {
                int n = baseOff;
                Console.WriteLine(string.Format("  pt{0}: temp {1,3}-{2,3}  rpm {3,5}/{4,5}  b82={5} b92={6} b102=0x{7:X2}",
                    j, o[n + 22 + j], o[n + 32 + j], (o[n + 42 + j * 2] << 8) + o[n + 43 + j * 2], (o[n + 62 + j * 2] << 8) + o[n + 63 + j * 2], o[n + 82 + j], o[n + 92 + j], o[n + 102 + j]));
            }
        }
    }
}
