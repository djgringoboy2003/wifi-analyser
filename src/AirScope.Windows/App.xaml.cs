using System.Windows;
using AirScope.Windows.Services;

namespace AirScope.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private BackgroundMonitor? _background;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--admin-task")
        {
            await ElevationService.RunHelperAsync(e.Args[1]); Shutdown(); return;
        }
        if (e.Args.Contains("--smoke-test"))
        {
            try
            {
                var window = new MainWindow(smokeTest: true); MainWindow = window;
                window.Show(); window.UpdateLayout();
                await window.SmokeTestAsync(e.Args.Contains("--live"));
                Shutdown(0);
            }
            catch (Exception error)
            {
                string output = e.Args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? Path.Combine(JsonStore.Root, "smoke-error.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                await File.WriteAllTextAsync(output, error.ToString()); Shutdown(1);
            }
            return;
        }
        bool protectedWorker = e.Args.Length == 2 && e.Args[0] == "--protected-worker";
        bool background = e.Args.Contains("--background") || protectedWorker || e.Args.Contains("--background-smoke");
        _mutex = new Mutex(true, protectedWorker ? "Local\\AirScope-Protected-Worker" : "Local\\AirScope-Desktop", out bool created);
        if (!created) { if (!background) MessageBox.Show("AirScope is already running. Open it from the system tray.", "AirScope"); Shutdown(); return; }
        if (background)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var config = protectedWorker ? await ProtectedWorkerService.LoadAsync(e.Args[1]) : null;
                bool smoke = e.Args.Contains("--background-smoke"); _background = new(config, smoke);
                if (smoke)
                {
                    await Task.Delay(3500);
                    string output = e.Args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "AirScope-background.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                    await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(new { success = true, windows = Windows.Count, workingSetMb = Environment.WorkingSet / 1_000_000d }));
                    _background.Dispose(); Shutdown();
                }
            }
            catch (Exception error)
            {
                Directory.CreateDirectory(JsonStore.Root); await File.AppendAllTextAsync(Path.Combine(JsonStore.Root, "background-errors.log"), DateTimeOffset.Now + " " + error.Message + Environment.NewLine); Shutdown(1);
            }
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                Directory.CreateDirectory(JsonStore.Root);
                File.AppendAllText(Path.Combine(JsonStore.Root, "errors.log"), DateTimeOffset.Now + " " + args.Exception + Environment.NewLine);
            }
            catch (IOException) { }
            MessageBox.Show(args.Exception.Message, "AirScope", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        MainWindow = new MainWindow(); MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _background?.Dispose(); _mutex?.Dispose(); base.OnExit(e); }
}
