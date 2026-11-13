using System;
using System.Runtime.InteropServices;
namespace IPbindTest
{
    // Live monitor scale (Settings > Display > Scale) for the primary display,
    // through the undocumented DisplayConfig DPI-scale device-info types (-3/-4).
    static class ScaleControl
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct Header { public int type; public int size; public LUID adapterId; public uint id; }
        [StructLayout(LayoutKind.Sequential)] struct DpiGet { public Header header; public int minRel; public int curRel; public int maxRel; }
        [StructLayout(LayoutKind.Sequential)] struct DpiSet { public Header header; public int scaleRel; }
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, IntPtr pathArray, ref uint modes, IntPtr modeArray, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DpiGet p);
        [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref DpiSet p);
        static readonly int[] Steps = new int[] { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

        static bool Source(out LUID adapter, out uint id)
        {
            adapter = new LUID(); id = 0;
            uint np, nm;
            if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0 || np == 0) return false;
            IntPtr paths = Marshal.AllocHGlobal((int)np * 72), modes = Marshal.AllocHGlobal((int)nm * 64);
            try
            {
                if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
                adapter.Low = (uint)Marshal.ReadInt32(paths, 0);
                adapter.High = Marshal.ReadInt32(paths, 4);
                id = (uint)Marshal.ReadInt32(paths, 8);
                return true;
            }
            finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
        }

        static bool Get(out int min, out int cur, out int max, out LUID adapter, out uint id)
        {
            min = cur = max = 0;
            if (!Source(out adapter, out id)) return false;
            DpiGet g = new DpiGet();
            g.header.type = -3; g.header.size = Marshal.SizeOf(typeof(DpiGet)); g.header.adapterId = adapter; g.header.id = id;
            if (DisplayConfigGetDeviceInfo(ref g) != 0) return false;
            min = g.minRel; cur = g.curRel; max = g.maxRel;
            return true;
        }

        public static string Describe()
        {
            int min, cur, max; LUID a; uint id;
            if (!Get(out min, out cur, out max, out a, out id)) return "unavailable";
            int rec = Math.Abs(min);
            return "current " + Pct(rec + cur) + "% recommended " + Pct(rec) + "% max " + Pct(rec + max) + "%";
        }

        static int Pct(int idx) { return idx >= 0 && idx < Steps.Length ? Steps[idx] : -1; }

        public static string Set(int pct)
        {
            int min, cur, max; LUID a; uint id;
            if (!Get(out min, out cur, out max, out a, out id)) return "FAIL get";
            int rec = Math.Abs(min);
            int idx = Array.IndexOf(Steps, pct);
            if (idx < 0) return "FAIL bad pct";
            int rel = idx - rec;
            if (rel < min || rel > max) return "UNAVAILABLE " + pct + "% (" + Describe() + ")";
            DpiSet s = new DpiSet();
            s.header.type = -4; s.header.size = Marshal.SizeOf(typeof(DpiSet)); s.header.adapterId = a; s.header.id = id; s.scaleRel = rel;
            int rc = DisplayConfigSetDeviceInfo(ref s);
            return rc == 0 ? "OK " + Describe() : "FAIL set rc=" + rc;
        }
    }
}
