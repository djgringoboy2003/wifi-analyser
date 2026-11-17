using System.Windows;
using AirScope.Windows.Services;

namespace AirScope.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
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
        _mutex = new Mutex(true, "Local\\AirScope-Desktop", out bool created);
        if (!created) { MessageBox.Show("AirScope is already running. Open it from the system tray.", "AirScope"); Shutdown(); return; }
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
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
