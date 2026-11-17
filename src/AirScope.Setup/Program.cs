using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace AirScope.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--verify-payload")
        {
            try
            {
                using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("AirScope.Setup.Payload.zip") ?? throw new IOException("The setup payload is missing.");
                using var zip = new ZipArchive(payload); var entries = zip.Entries.Where(e => e.FullName == "AirScope.exe").ToArray();
                if (entries.Length != 1 || entries[0].Length is < 10000 or > 500_000_000) throw new IOException("Invalid app payload.");
                string path = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new { success = true, appBytes = entries[0].Length, entries = zip.Entries.Count })); return 0;
            }
            catch (Exception error) { File.WriteAllText(args[1], error.ToString()); return 1; }
        }
        ApplicationConfiguration.Initialize(); Application.Run(new SetupForm()); return 0;
    }
}

internal sealed class SetupForm : Form
{
    private readonly TextBox _path = new() { Dock = DockStyle.Fill, Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AirScope") };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(163, 184, 205), Text = "Installs for this Windows user. Existing settings and history are preserved. No administrator rights are required." };
    private readonly CheckBox _launch = new() { Text = "Open AirScope after installation", Checked = true, AutoSize = true, ForeColor = Color.White };
    private readonly CheckBox _shortcuts = new() { Text = "Add a Start menu shortcut", Checked = true, AutoSize = true, ForeColor = Color.White };
    private readonly Button _install = new() { Text = "Install AirScope", AutoSize = true, Padding = new(14, 8, 14, 8), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(81, 224, 196), ForeColor = Color.FromArgb(8, 26, 24) };
    private readonly Button _cancel = new() { Text = "Close", AutoSize = true, Padding = new(14, 8, 14, 8), FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
    private bool _running;
    public SetupForm()
    {
        Text = "AirScope setup"; Size = new(740, 460); MinimumSize = new(640, 430); StartPosition = FormStartPosition.CenterScreen;
        Font = new("Segoe UI", 11); BackColor = Color.FromArgb(9, 15, 25); ForeColor = Color.White; AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(28), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new(SizeType.Absolute, 55)); layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.RowStyles.Add(new(SizeType.Absolute, 47));
        layout.RowStyles.Add(new(SizeType.Absolute, 32)); layout.RowStyles.Add(new(SizeType.Absolute, 32)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 60));
        layout.Controls.Add(new Label { Text = "airscope", ForeColor = Color.FromArgb(81, 224, 196), Font = new("Segoe UI", 27, FontStyle.Bold), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = "Wi-Fi intelligence for your Windows PC", AutoSize = true }, 0, 1);
        var destination = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; destination.ColumnStyles.Add(new(SizeType.Percent, 100)); destination.ColumnStyles.Add(new(SizeType.Absolute, 100));
        _path.AccessibleName = "Installation folder"; destination.Controls.Add(_path);
        var browse = new Button { Text = "Browse…", Dock = DockStyle.Fill }; browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { SelectedPath = _path.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath; };
        destination.Controls.Add(browse); layout.Controls.Add(destination, 0, 2); layout.Controls.Add(_shortcuts, 0, 3); layout.Controls.Add(_launch, 0, 4); layout.Controls.Add(_status, 0, 5);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(_install); buttons.Controls.Add(_cancel); layout.Controls.Add(buttons, 0, 6);
        _cancel.Click += (_, _) => { if (!_running) Close(); }; _install.Click += async (_, _) => await InstallAsync();
        FormClosing += (_, e) => { if (_running) e.Cancel = true; }; Controls.Add(layout);
    }
    private async Task InstallAsync()
    {
        _running = true; _install.Enabled = _cancel.Enabled = false;
        string temp = Path.Combine(Path.GetTempPath(), "AirScope-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            _status.Text = "Preparing application files…";
            await ExtractResourceAsync("AirScope.Setup.Payload.zip", Path.Combine(temp, "app.zip"));
            await ExtractResourceAsync("AirScope.Setup.Install.ps1", Path.Combine(temp, "Install.ps1"));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(temp, "Install.ps1"), "-PayloadPath", Path.Combine(temp, "app.zip"), "-InstallDirectory", _path.Text, "-NoLaunch" }) start.ArgumentList.Add(arg);
            if (!_shortcuts.Checked) start.ArgumentList.Add("-NoShortcuts");
            using var process = Process.Start(start) ?? throw new IOException("The per-user installer could not start.");
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); string message = await output, failure = await errors;
            if (process.ExitCode != 0) throw new IOException(failure.Length > 0 ? failure : "Installation did not complete.");
            _status.Text = message.Trim() + Environment.NewLine + "Use the Start menu or the installed Uninstall-AirScope.cmd to remove the app. History and recovery copies are preserved.";
            _install.Text = "Installed";
            if (_launch.Checked) Process.Start(new ProcessStartInfo(Path.Combine(Path.GetFullPath(_path.Text), "AirScope.exe")) { UseShellExecute = true });
        }
        catch (Exception error) { _status.Text = "Installation needs attention: " + error.Message; _install.Enabled = true; }
        finally
        {
            _running = false; _cancel.Enabled = true;
            foreach (string name in new[] { "app.zip", "Install.ps1" }) { string file = Path.Combine(temp, name); try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } }
            try { if (!Directory.EnumerateFileSystemEntries(temp).Any()) Directory.Delete(temp); } catch (IOException) { }
        }
    }
    private static async Task ExtractResourceAsync(string name, string path)
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new IOException("This development setup has no packaged app. Build the release setup first.");
        await using var output = File.Create(path); await input.CopyToAsync(output);
    }
}
