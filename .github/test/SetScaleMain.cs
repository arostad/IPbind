// Tiny driver for ScaleControl (throwaway; not shipped).
using System;
using System.IO;

namespace IPbindTest
{
    static class SetScaleMain
    {
        static int Main(string[] args)
        {
            if (args.Length >= 3 && args[0] == "setscale")
            {
                string r = ScaleControl.Set(int.Parse(args[1]));
                File.WriteAllText(args[2], r);
                Console.WriteLine(r);
                return r.StartsWith("OK") ? 0 : 1;
            }
            Console.WriteLine("usage: setscale.exe setscale <pct> <report>");
            return 2;
        }
    }
}
