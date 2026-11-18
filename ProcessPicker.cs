using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Kagenui;

/// <summary>枚举 Alt+Tab 里可见的窗口进程，供选择。</summary>
internal static class WindowEnum
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public sealed record WindowInfo(int Pid, string ProcessName, string Title);

    public static List<WindowInfo> Enumerate()
    {
        var result = new List<WindowInfo>();
        var seen = new HashSet<string>();
        // Alt+Tab 会跳过的外壳类窗口
        var skipClass = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow" };

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (GetWindowTextLength(h) == 0) return true;

            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            if (skipClass.Contains(cls.ToString())) return true;

            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == 0) return true;

            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { name = "(unknown)"; }

            var title = new StringBuilder(512);
            GetWindowText(h, title, title.Capacity);

            var key = name + "|" + pid;
            if (!seen.Add(key)) return true;

            result.Add(new WindowInfo((int)pid, name, title.ToString()));
            return true;
        }, IntPtr.Zero);

        return result.OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>选择要接管的进程：勾选窗口进程，或手动输入进程名。</summary>
public sealed class ProcessPicker : Form
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true,
        MultiSelect = true, HideSelection = false
    };
    private readonly TextBox _filter = new() { Dock = DockStyle.Top };
    private readonly TextBox _manual = new() { Width = 180 };
    private readonly CheckBox _all = new() { Text = "包含后台进程（无窗口）", AutoSize = true };

    public List<string> SelectedNames { get; } = new();

    public ProcessPicker(IEnumerable<string> current)
    {
        Text = "添加要接管的进程";
        Width = 720; Height = 560; StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 420);

        _list.Columns.Add("进程名", 200);
        _list.Columns.Add("PID", 70, HorizontalAlignment.Right);
        _list.Columns.Add("窗口标题", 400);

        var top = new Panel { Dock = DockStyle.Top, Height = 64 };
        var lblFilter = new Label { Text = "筛选", AutoSize = true, Top = 10, Left = 8 };
        _filter.SetBounds(52, 6, 300, 24);
        _filter.TextChanged += (_, _) => ReloadList();
        var lblManual = new Label { Text = "手动添加", AutoSize = true, Top = 38, Left = 8 };
        _manual.SetBounds(72, 34, 200, 24);
        var btnAdd = new Button { Text = "加入", Top = 33, Left = 282, Width = 70, Height = 26 };
        btnAdd.Click += (_, _) =>
        {
            var n = _manual.Text.Trim();
            if (n.Length == 0) return;
            if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
            foreach (var row in _list.Items.Cast<ListViewItem>())
                if (string.Equals(row.SubItems[0].Text, n, StringComparison.OrdinalIgnoreCase)) { row.Checked = true; _manual.Clear(); return; }
            var it = new ListViewItem(new[] { n, "-", "(手动)" }) { Checked = true };
            _list.Items.Insert(0, it);
            _manual.Clear();
        };
        _all.Top = 37; _all.Left = 370;
        _all.CheckedChanged += (_, _) => ReloadList();
        top.Controls.AddRange(new Control[] { lblFilter, _filter, lblManual, _manual, btnAdd, _all });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46 };
        var ok = new Button { Text = "确定", Width = 90, Height = 30, Top = 8, Left = 480, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "取消", Width = 90, Height = 30, Top = 8, Left = 580, DialogResult = DialogResult.Cancel };
        bottom.Controls.AddRange(new Control[] { ok, cancel });
        ok.Click += (_, _) => { Collect(); DialogResult = DialogResult.OK; Close(); };

        Controls.Add(_list);
        Controls.Add(top);
        Controls.Add(bottom);

        Load += (_, _) => { ReloadList(); foreach (var n in current) { var it = new ListViewItem(new[] { n, "-", "(已有)" }) { Checked = true }; _list.Items.Insert(0, it); } };
    }

    private void ReloadList()
    {
        var keep = _list.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => i.SubItems[0].Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _list.BeginUpdate();
        _list.Items.Clear();

        foreach (var w in WindowEnum.Enumerate())
        {
            if (_filter.Text.Length > 0 && w.ProcessName.IndexOf(_filter.Text, StringComparison.OrdinalIgnoreCase) < 0
                && w.Title.IndexOf(_filter.Text, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var it = new ListViewItem(new[] { w.ProcessName + ".exe", w.Pid.ToString(), w.Title }) { Checked = keep.Contains(w.ProcessName + ".exe") };
            _list.Items.Add(it);
        }

        if (_all.Checked)
        {
            var have = _list.Items.Cast<ListViewItem>().Select(i => i.SubItems[0].Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses().OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                var n = p.ProcessName + ".exe";
                if (have.Contains(n)) continue;
                if (_filter.Text.Length > 0 && p.ProcessName.IndexOf(_filter.Text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _list.Items.Add(new ListViewItem(new[] { n, p.Id.ToString(), "" }) { Checked = keep.Contains(n) });
            }
        }

        _list.EndUpdate();
    }

    private void Collect()
    {
        SelectedNames.Clear();
        foreach (var it in _list.Items.Cast<ListViewItem>())
            if (it.Checked) SelectedNames.Add(it.SubItems[0].Text);
    }
}
