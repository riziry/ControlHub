using System; using System.Runtime.InteropServices;
static class P { [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string p);
[DllImport("InsydeDCHU.dll")] static extern int ReadAppSettings(int page, int off, int len, ref byte buf);
static void Main(){ SetDllDirectory(System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, @"..\lib")); foreach (int id in new[]{1029,1094,1095,1096,1097}) { var b=new byte[1]; ReadAppSettings(id/256,id%256,1,ref b[0]); Console.WriteLine("app "+id+" = "+b[0]); } } }
