using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
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
    private readonly Button _backButton = new();
    private readonly Button _nextButton = new();
    private readonly Button _installButton = new();
    private readonly CheckBox _launchAfterInstall = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Panel _body = new();
    private readonly Panel _welcomePage = new();
    private readonly Panel _locationPage = new();
    private readonly Panel _installPage = new();
    private readonly string _defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MJJsteamtools");
    private int _page;

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
        header.Controls.Add(new Label { AutoSize = true, Text = "安装 MJJsteamtools", Font = new Font("Segoe UI", 20, FontStyle.Bold), ForeColor = Color.White, Location = new Point(28, 20) });
        header.Controls.Add(new Label { AutoSize = true, Text = "Steam 游戏库与 Lua/Bin 管理工具 · 版本 2.7.0", Font = new Font("Segoe UI", 10), ForeColor = Color.FromArgb(190, 205, 220), Location = new Point(31, 63) });

        _body.Dock = DockStyle.Fill;
        _body.Padding = new Padding(30, 22, 30, 20);
        BuildWelcomePage(); BuildLocationPage(); BuildInstallPage();
        _body.Controls.AddRange(new Control[] { _welcomePage, _locationPage, _installPage });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 66, BackColor = Color.FromArgb(242, 244, 247) };
        var cancel = new Button { Text = "取消", Width = 90, Height = 36, Location = new Point(335, 15), Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        cancel.Click += (_, _) => Close();
        _backButton.Text = "上一步"; _backButton.Width = 90; _backButton.Height = 36; _backButton.Location = new Point(430, 15); _backButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; _backButton.Click += (_, _) => GoBack();
        _nextButton.Text = "下一步"; _nextButton.Width = 100; _nextButton.Height = 36; _nextButton.Location = new Point(530, 15); _nextButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; _nextButton.Click += (_, _) => GoNext();
        _installButton.Text = "安装"; _installButton.Width = 100; _installButton.Height = 36; _installButton.Location = new Point(530, 15); _installButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; _installButton.Click += (_, _) => Install(); _installButton.Visible = false;
        footer.Controls.AddRange(new Control[] { cancel, _backButton, _nextButton, _installButton });
        Controls.Add(_body); Controls.Add(footer); Controls.Add(header); ShowPage(0);
    }

    private void BuildWelcomePage()
    {
        _welcomePage.Dock = DockStyle.Fill;
        _welcomePage.Controls.Add(new Label { AutoSize = true, Text = "欢迎使用 MJJsteamtools 安装程序", Font = new Font("Segoe UI", 15, FontStyle.Bold), Location = new Point(0, 12) });
        _welcomePage.Controls.Add(new Label { AutoSize = false, Size = new Size(560, 90), Text = "此向导将帮助你安装 MJJsteamtools。\n\n点击“下一步”选择安装位置，或直接使用推荐位置快速安装。", Font = new Font("Segoe UI", 10), ForeColor = Color.FromArgb(75, 84, 94), Location = new Point(0, 58) });
        var quick = new Button { Text = "快速安装到推荐位置", Width = 190, Height = 38, Location = new Point(0, 185) };
        quick.Click += (_, _) => { _installPath.Text = _defaultPath; ShowPage(1); };
        _welcomePage.Controls.Add(quick);
    }

    private void BuildLocationPage()
    {
        _locationPage.Dock = DockStyle.Fill;
        _locationPage.Controls.Add(new Label { AutoSize = true, Text = "选择安装位置", Font = new Font("Segoe UI", 15, FontStyle.Bold), Location = new Point(0, 12) });
        _locationPage.Controls.Add(new Label { AutoSize = false, Size = new Size(560, 42), Text = "选择 MJJsteamtools 的安装目录。建议使用默认位置。", ForeColor = Color.FromArgb(75, 84, 94), Location = new Point(0, 52) });
        _locationPage.Controls.Add(new Label { AutoSize = true, Text = "安装目录", Location = new Point(0, 118) });
        _installPath.Text = _defaultPath; _installPath.Location = new Point(0, 145); _installPath.Width = 440; _installPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var browse = new Button { Text = "浏览…", Location = new Point(455, 143), Width = 95, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right }; browse.Click += (_, _) => Browse();
        var quick = new Button { Text = "使用推荐位置", Location = new Point(0, 190), Width = 125, Height = 30 }; quick.Click += (_, _) => _installPath.Text = _defaultPath;
        _launchAfterInstall.Text = "安装完成后启动 MJJsteamtools"; _launchAfterInstall.Checked = true; _launchAfterInstall.AutoSize = true; _launchAfterInstall.Location = new Point(0, 240);
        _locationPage.Controls.AddRange(new Control[] { _installPath, browse, quick, _launchAfterInstall });
    }

    private void BuildInstallPage()
    {
        _installPage.Dock = DockStyle.Fill;
        _installPage.Controls.Add(new Label { AutoSize = true, Text = "正在安装 MJJsteamtools", Font = new Font("Segoe UI", 15, FontStyle.Bold), Location = new Point(0, 12) });
        _progress.Location = new Point(0, 92); _progress.Width = 550; _progress.Height = 14; _progress.Style = ProgressBarStyle.Continuous;
        _status.AutoSize = true; _status.ForeColor = Color.FromArgb(75, 84, 94); _status.Location = new Point(0, 125); _installPage.Controls.AddRange(new Control[] { _progress, _status });
    }

    private Icon? TryLoadIcon() { try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { return null; } }

    private void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, 2);
        _welcomePage.Visible = _page == 0; _locationPage.Visible = _page == 1; _installPage.Visible = _page == 2;
        _backButton.Visible = _page == 1; _nextButton.Visible = _page == 0 || _page == 1; _installButton.Visible = _page == 2;
        if (_page == 0) ActiveControl = _nextButton; else if (_page == 1) ActiveControl = _installPath;
    }

    private void GoNext()
    {
        if (_page == 0) { ShowPage(1); return; }
        if (string.IsNullOrWhiteSpace(_installPath.Text)) { MessageBox.Show(this, "请选择安装目录。", "无法继续", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        ShowPage(2);
    }

    private void GoBack() { if (_page == 1) ShowPage(0); }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择 MJJsteamtools 安装目录", SelectedPath = Directory.Exists(_installPath.Text) ? _installPath.Text : _defaultPath, UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _installPath.Text = dialog.SelectedPath;
    }

    private async void Install()
    {
        var target = _installPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(target)) { MessageBox.Show(this, "请选择安装目录。", "无法安装", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        _installButton.Enabled = false; _backButton.Enabled = false; _progress.Value = 0; _status.Text = "正在解包文件…";
        try
        {
            await Task.Run(() => ExtractPayload(target)); CreateShortcuts(target); _progress.Value = 100; _status.Text = "安装完成。";
            var result = MessageBox.Show(this, "MJJsteamtools 已安装完成。", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (_launchAfterInstall.Checked) Process.Start(new ProcessStartInfo(Path.Combine(target, "MJJsteamtools.exe")) { WorkingDirectory = target, UseShellExecute = true });
            if (result == DialogResult.OK) Close();
        }
        catch (Exception ex) { _status.Text = "安装失败。"; _installButton.Enabled = true; _backButton.Enabled = true; MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ExtractPayload(string target)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("找不到安装程序路径。");
        using var file = File.OpenRead(self); if (file.Length < sizeof(long)) throw new InvalidDataException("安装包数据不完整。");
        file.Seek(-sizeof(long), SeekOrigin.End); using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true); var length = reader.ReadInt64();
        if (length <= 0 || length > file.Length - sizeof(long)) throw new InvalidDataException("安装包数据无效。");
        file.Seek(-sizeof(long) - length, SeekOrigin.End); var data = new byte[length]; file.ReadExactly(data); Directory.CreateDirectory(target);
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read); var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName)); if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包包含无效路径。");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void CreateShortcuts(string target)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell"); if (shellType is null) return; dynamic shell = Activator.CreateInstance(shellType)!;
        var links = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "MJJsteamtools.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MJJsteamtools.lnk") };
        foreach (var link in links)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!); dynamic shortcut = shell.CreateShortcut(link); shortcut.TargetPath = Path.Combine(target, "MJJsteamtools.exe"); shortcut.WorkingDirectory = target; shortcut.Description = "Steam Lua/Bin 游戏入库管理工具"; shortcut.Save();
        }
        Marshal.FinalReleaseComObject(shell);
    }
}
