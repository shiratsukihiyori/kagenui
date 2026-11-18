using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Kagenui;

public sealed class MainForm : Form
{
    private readonly TextBox _proxyHost = new() { Text = "127.0.0.1", Width = 140 };
    private readonly TextBox _proxyPort = new() { Text = "7890", Width = 70 };
    private readonly ListBox _targets = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _launchPath = new() { Dock = DockStyle.Top };
    private readonly TextBox _log = new()
    {
        Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 9f)
    };
    private readonly CheckBox _verbose = new() { Text = "详细日志", Checked = true, AutoSize = true, Margin = new Padding(8, 6, 8, 0) };
    private readonly Label _status = new() { Text = "未运行", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(4, 8, 0, 0) };
    private readonly Button _btnStart = new() { Text = "启动", Width = 90, Height = 30 };
    private readonly Button _btnStop = new() { Text = "停止", Width = 90, Height = 30, Enabled = false };
    private readonly string _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kagenui");

    public MainForm()
    {
        Text = "Kagenui 影縫い — 进程流量转发进系统代理";
        Width = 900; Height = 720; StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 600);
        Directory.CreateDirectory(_logDir);

        Redirector.Sink = Log;
        Engine.Ensure(m => Debug.WriteLine(m));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
        for (int i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles[5] = new RowStyle(SizeType.Percent, 100);

        // 上游代理
        var rowUp = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        rowUp.Controls.Add(new Label { Text = "系统代理（HTTP CONNECT + SOCKS5）", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        rowUp.Controls.Add(_proxyHost);
        rowUp.Controls.Add(new Label { Text = ":", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        rowUp.Controls.Add(_proxyPort);
        rowUp.Controls.Add(new Label { Text = "  例：v2rayN 混合端口 127.0.0.1:7890", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 9, 0, 0) });
        root.Controls.Add(rowUp);

        // 进程列表
        root.Controls.Add(new Label { Text = "只接管这些进程的 TCP 与 UDP（不选就是全部放行）", AutoSize = true, Margin = new Padding(0, 10, 0, 4) });
        var group = new GroupBox { Height = 190, Dock = DockStyle.Top, Padding = new Padding(8) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.Controls.Add(_targets, 0, 0);

        var side = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 0, 0, 0) };
        var btnAdd = new Button { Text = "添加…", Width = 96, Height = 28 };
        var btnDel = new Button { Text = "删除", Width = 96, Height = 28 };
        var btnClear = new Button { Text = "清空", Width = 96, Height = 28 };
        side.Controls.AddRange(new Control[] { btnAdd, btnDel, btnClear });
        grid.Controls.Add(side, 1, 0);
        group.Controls.Add(grid);
        root.Controls.Add(group);

        // 随后启动
        var rowLaunch = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 0) };
        rowLaunch.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        rowLaunch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rowLaunch.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        rowLaunch.Controls.Add(new Label { Text = "随后启动", AutoSize = true, Margin = new Padding(0, 8, 0, 0) }, 0, 0);
        rowLaunch.Controls.Add(_launchPath, 1, 0);
        var btnPick = new Button { Text = "选择…", Height = 26, Dock = DockStyle.Top };
        rowLaunch.Controls.Add(btnPick, 2, 0);
        root.Controls.Add(rowLaunch);

        // 主按钮
        var rowBtn = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 6) };
        var btnLog = new Button { Text = "打开配置目录", Width = 110, Height = 30 };
        rowBtn.Controls.AddRange(new Control[] { _btnStart, _btnStop, btnLog, _verbose, _status });
        root.Controls.Add(rowBtn);

        // 日志
        var logBox = new GroupBox { Text = "日志", Dock = DockStyle.Fill, Padding = new Padding(6), MinimumSize = new Size(0, 220) };
        logBox.Controls.Add(_log);
        root.Controls.Add(logBox);

        Controls.Add(root);

        // 事件
        btnAdd.Click += (_, _) => AddProcesses();
        btnDel.Click += (_, _) => { foreach (var i in _targets.SelectedIndices.Cast<int>().OrderByDescending(x => x).ToList()) _targets.Items.RemoveAt(i); };
        btnClear.Click += (_, _) => _targets.Items.Clear();
        btnPick.Click += (_, _) => { using var d = new OpenFileDialog { Filter = "可执行文件|*.exe|所有文件|*.*" }; if (d.ShowDialog() == DialogResult.OK) _launchPath.Text = d.FileName; };
        btnLog.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", _logDir) { UseShellExecute = true });
        _btnStart.Click += (_, _) => Start();
        _btnStop.Click += (_, _) => { Redirector.Stop(); SetRunning(false); };
        FormClosing += (_, _) => Redirector.Stop();

        Log("WinDivert 引擎: " + Engine.DllPath);
        Log("提示：点「添加…」可从 Alt+Tab 窗口列表里挑进程");
    }

    private void AddProcesses()
    {
        using var picker = new ProcessPicker(_targets.Items.Cast<object>().Select(o => o.ToString() ?? ""));
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _targets.Items.Clear();
        foreach (var n in picker.SelectedNames.Distinct(StringComparer.OrdinalIgnoreCase))
            _targets.Items.Add(n);
        Log("已选 " + _targets.Items.Count + " 个进程");
    }

    private static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private void Start()
    {
        if (Redirector.IsRunning) { Log("已在运行"); return; }
        if (!IsAdmin())
        {
            Log("需要管理员权限（加载 WinDivert 驱动），正在提权重启…");
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { Verb = "runas", UseShellExecute = true }); Close(); }
            catch (Exception e) { Log("提权被拒绝: " + e.Message); }
            return;
        }
        if (!Engine.Ensure(Log) || !Engine.Preload(Log)) { Log("WinDivert 引擎加载失败"); return; }
        if (!int.TryParse(_proxyPort.Text.Trim(), out int port) || port <= 0) { Log("代理端口不合法"); return; }

        var names = _targets.Items.Cast<object>().Select(o => o.ToString()!).Where(s => s.Length > 0).ToArray();
        if (names.Length == 0) { Log("请先添加要接管的进程"); return; }

        var ok = Redirector.Start(new Redirector.Options
        {
            Names = names,
            ProxyHost = _proxyHost.Text.Trim(),
            ProxyPort = port,
            LaunchPath = _launchPath.Text.Trim().Length > 0 ? _launchPath.Text.Trim() : null,
            Verbose = _verbose.Checked,
            LogFile = Path.Combine(_logDir, "run.log"),
        });
        SetRunning(ok);
    }

    private void SetRunning(bool running)
    {
        if (InvokeRequired) { BeginInvoke(() => SetRunning(running)); return; }
        _btnStart.Enabled = !running;
        _btnStop.Enabled = running;
        _status.Text = running ? "● 已接管" : "○ 未运行";
        _status.ForeColor = running ? Color.SeaGreen : Color.DimGray;
    }

    private void Log(string msg)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(msg)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
    }
}
