using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Kagenui;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 无界面自检：--test-route <秒> --name curl --proxy 127.0.0.1:7890 --log <file>
        if (args.Length > 0 && args[0] == "--test-route")
        {
            int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10;
            var names = new System.Collections.Generic.List<string>();
            string host = "127.0.0.1";
            int port = 7890;
            string? logFile = null;
            bool verbose = true;
            string? resync = null;   // QA：--resync <秒>:<逗号分隔的进程名>，模拟运行中增删目标
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--name": names.Add(args[++i]); break;
                    case "--proxy": { var parts = args[++i].Split(':'); host = parts[0]; port = int.Parse(parts[1]); break; }
                    case "--log": logFile = args[++i]; break;
                    case "-q": verbose = false; break;
                    case "--resync": resync = args[++i]; break;
                }
            }
            void Sink(string m)
            {
                var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m;
                if (logFile != null) { try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { } }
            }
            Redirector.Sink = Sink;
            Engine.Ensure(Sink);
            Engine.Preload(Sink);
            var ok = Redirector.Start(new Redirector.Options { Names = names.ToArray(), ProxyHost = host, ProxyPort = port, Verbose = verbose });
            Sink("start => " + ok);

            // QA 钩子：模拟 UI 在运行期改动目标列表（走的是 MainForm 同一个 SyncTargets）
            if (ok && resync != null)
            {
                var seg = resync.Split(':', 2);
                int delay = int.TryParse(seg[0], out var d) ? d : 5;
                var list = (seg.Length > 1 ? seg[1] : "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                new Thread(() =>
                {
                    Thread.Sleep(delay * 1000);
                    Redirector.SyncTargets(list);
                }) { IsBackground = true }.Start();
            }
            Thread.Sleep(seconds * 1000);
            Redirector.Stop();
            Thread.Sleep(300);
            return ok ? 0 : 1;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
