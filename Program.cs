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
        // 启动痕迹（用于确认参数是否送达）
        try
        {
            var bootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kagenui");
            Directory.CreateDirectory(bootDir);
            File.AppendAllText(Path.Combine(bootDir, "boot.txt"), DateTime.Now.ToString("HH:mm:ss") + "  args=[" + string.Join("|", args) + "]" + Environment.NewLine);
        }
        catch { }

        // 无界面自检：--test-route <秒> --name curl --proxy 127.0.0.1:7890 --log <file>
        if (args.Length > 0 && args[0] == "--test-route")
        {
            int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10;
            var names = new System.Collections.Generic.List<string>();
            string host = "127.0.0.1";
            int port = 7890;
            string? logFile = null;
            bool verbose = true;
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--name": names.Add(args[++i]); break;
                    case "--proxy": { var parts = args[++i].Split(':'); host = parts[0]; port = int.Parse(parts[1]); break; }
                    case "--log": logFile = args[++i]; break;
                    case "-q": verbose = false; break;
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
            Thread.Sleep(seconds * 1000);
            Redirector.Stop();
            Thread.Sleep(300);
            return ok ? 0 : 1;
        }

        // 验证探针：kagenui.exe --probe <url> <outfile>
        // 强制不走系统代理，只发一次请求，把结果写进文件
        if (args.Length > 0 && args[0] == "--probe")
        {
            var url = args.Length > 1 ? args[1] : "https://api.ip.sb/ip";
            var outFile = args.Length > 2 ? args[2] : "probe.txt";
            try
            {
                using var handler = new System.Net.Http.HttpClientHandler { UseProxy = false };
                using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "kagenui-probe/1.0");
                using var res = client.Send(req);
                var body = new System.IO.StreamReader(res.Content.ReadAsStream()).ReadToEnd().Trim();
                System.IO.File.WriteAllText(outFile, "OK|" + (int)res.StatusCode + "|" + body);
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(outFile, "ERR|0|" + ex.Message);
            }
            return 0;
        }

        if (args.Length > 0 && args[0] == "--picker-demo")
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new ProcessPicker(new[] { "短剧播放器.exe" }));
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
