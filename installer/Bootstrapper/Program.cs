using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing;
using System.Windows.Forms;

namespace MJJsteamtools.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
    }
}

internal sealed class InstallerForm : Form
{
    private readonly TextBox _installPath = new();
    private readonly Button _installButton = new();
    private readonly CheckBox _launchAfterInstall = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly string _defaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "MJJsteamtools");

    public InstallerForm()
    {
        Text = "MJJsteamtools 安装程序";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 390);
        MinimumSize = new Size(640, 390);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = TryLoadIcon();

        var header = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Color.FromArgb(24, 39, 55) };
        var title = new Label { AutoSize = true, Text = "安装 MJJsteamtools", Font = new Font("Segoe UI", 20, FontStyle.Bold), ForeColor = Color.White, Location = new Point(28, 20) };
        var subtitle = new Label { AutoSize = true, Text = "Steam 游戏库与 Lua/Bin 管理工具 · 版本 2.7.0", Font = new Font("Segoe UI", 10), ForeColor = Color.FromArgb(190, 205, 220), Location = new Point(31, 63) };
        header.Controls.Add(title); header.Controls.Add(subtitle);

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(30, 22, 30, 20) };
        var intro = new Label { AutoSize = true, Text = "选择安装位置", Font = new Font("Segoe UI", 12, FontStyle.Bold), Location = new Point(30, 20) };
        var hint = new Label { AutoSize = true, Text = "安装程序会将 MJJsteamtools 安装到所选文件夹，并创建开始菜单快捷方式。", ForeColor = Color.FromArgb(95, 105, 116), Location = new Point(30, 50) };
        var pathLabel = new Label { AutoSize = true, Text = "安装目录", Location = new Point(30, 100) };
        _installPath.Text = _defaultPath; _installPath.Location = new Point(30, 125); _installPath.Width = 455; _installPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var browse = new Button { Text = "浏览…", Location = new Point(495, 123), Width = 85, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        browse.Click += (_, _) => Browse();
        _launchAfterInstall.Text = "安装完成后启动 MJJsteamtools"; _launchAfterInstall.Checked = true; _launchAfterInstall.AutoSize = true; _launchAfterInstall.Location = new Point(30, 168);
        _progress.Location = new Point(30, 210); _progress.Width = 550; _progress.Height = 8; _progress.Style = ProgressBarStyle.Continuous; _progress.Visible = false;
        _status.AutoSize = true; _status.ForeColor = Color.FromArgb(95, 105, 116); _status.Location = new Point(30, 230);
        _installButton.Text = "安装"; _installButton.Width = 110; _installButton.Height = 36; _installButton.Location = new Point(470, 270); _installButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; _installButton.Click += (_, _) => Install();
        var cancel = new Button { Text = "取消", Width = 90, Height = 36, Location = new Point(365, 270), Anchor = AnchorStyles.Bottom | AnchorStyles.Right }; cancel.Click += (_, _) => Close();
        content.Controls.AddRange(new Control[] { intro, hint, pathLabel, _installPath, browse, _launchAfterInstall, _progress, _status, cancel, _installButton });
        Controls.Add(content); Controls.Add(header);
    }

    private Icon? TryLoadIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { return null; }
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择 MJJsteamtools 安装目录", SelectedPath = _installPath.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) _installPath.Text = dialog.SelectedPath;
    }

    private async void Install()
    {
        var target = _installPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(target)) { MessageBox.Show(this, "请选择安装目录。", "无法安装", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        _installButton.Enabled = false; _progress.Visible = true; _status.Text = "正在解包文件…";
        try
        {
            await Task.Run(() => ExtractPayload(target));
            CreateShortcuts(target);
            _progress.Value = 100; _status.Text = "安装完成。";
            var result = MessageBox.Show(this, "MJJsteamtools 已安装完成。", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (_launchAfterInstall.Checked) Process.Start(new ProcessStartInfo(Path.Combine(target, "MJJsteamtools.exe")) { WorkingDirectory = target, UseShellExecute = true });
            if (result == DialogResult.OK) Close();
        }
        catch (Exception ex)
        {
            _status.Text = "安装失败。"; _installButton.Enabled = true;
            MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExtractPayload(string target)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("找不到安装程序路径。");
        using var file = File.OpenRead(self);
        if (file.Length < sizeof(long)) throw new InvalidDataException("安装包数据不完整。");
        file.Seek(-sizeof(long), SeekOrigin.End);
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        var length = reader.ReadInt64();
        if (length <= 0 || length > file.Length - sizeof(long)) throw new InvalidDataException("安装包数据无效。");
        file.Seek(-sizeof(long) - length, SeekOrigin.End);
        var data = new byte[length];
        file.ReadExactly(data);
        Directory.CreateDirectory(target);
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包包含无效路径。");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void CreateShortcuts(string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "MJJsteamtools.lnk");
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MJJsteamtools.lnk");
        foreach (var link in new[] { startMenu, desktop })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = Path.Combine(target, "MJJsteamtools.exe");
            shortcut.WorkingDirectory = target;
            shortcut.Description = "Steam Lua/Bin 游戏入库管理工具";
            shortcut.Save();
        }
        Marshal.FinalReleaseComObject(shell);
    }
}
