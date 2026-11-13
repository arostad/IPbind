// Proves a prod build (BuildInfo.IsBeta = false) never creates a log file or folder.
// Compile this with a prod Version.cs and IPbind.cs.
using System;
using System.IO;
using IPbind;

namespace IPbindTest
{
    static class DebugLogTest
    {
        static int Main()
        {
            if (BuildInfo.IsBeta)
            {
                Console.WriteLine("FAIL: DebugLogTest must be compiled as prod (IsBeta is true)");
                return 1;
            }
            if (DebugLog.Enabled)
            {
                Console.WriteLine("FAIL: DebugLog.Enabled is true in a prod build");
                return 1;
            }

            string prodDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IPbind", "logs");
            string betaDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IPbind Beta", "logs");
            bool prodExisted = Directory.Exists(prodDir);
            string[] prodBefore = prodExisted ? Directory.GetFiles(prodDir) : new string[0];
            bool betaExisted = Directory.Exists(betaDir);
            string[] betaBefore = betaExisted ? Directory.GetFiles(betaDir) : new string[0];

            DebugLog.Start();
            DebugLog.Write("INFO", "prod must not persist this line");
            DebugLog.Info("prod must not persist this either");
            DebugLog.Flush();
            DebugLog.Stop();

            if (!prodExisted && Directory.Exists(prodDir))
            {
                Console.WriteLine("FAIL: prod Start() created " + prodDir);
                return 1;
            }
            if (!betaExisted && Directory.Exists(betaDir))
            {
                Console.WriteLine("FAIL: prod Start() created " + betaDir);
                return 1;
            }
            if (Directory.Exists(prodDir))
            {
                foreach (string f in Directory.GetFiles(prodDir))
                {
                    bool known = false;
                    foreach (string b in prodBefore)
                        if (string.Equals(b, f, StringComparison.OrdinalIgnoreCase)) known = true;
                    if (!known)
                    {
                        Console.WriteLine("FAIL: prod Start() wrote new file " + f);
                        return 1;
                    }
                }
            }
            if (Directory.Exists(betaDir))
            {
                foreach (string f in Directory.GetFiles(betaDir))
                {
                    bool known = false;
                    foreach (string b in betaBefore)
                        if (string.Equals(b, f, StringComparison.OrdinalIgnoreCase)) known = true;
                    if (!known)
                    {
                        Console.WriteLine("FAIL: prod Start() wrote new file " + f);
                        return 1;
                    }
                }
            }

            Console.WriteLine("OK: prod write nothing (Enabled=" + DebugLog.Enabled + ", AppName=" + BuildInfo.AppName + ")");
            return 0;
        }
    }
}
