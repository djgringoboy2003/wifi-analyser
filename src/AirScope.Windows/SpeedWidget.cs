using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AirScope.Windows;

public sealed class SpeedWidget : Window
{
    private readonly TextBlock _download, _upload;
    public SpeedWidget(Action showMain, Action hide)
    {
        Title = "AirScope traffic"; Width = 210; Height = 67; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false;
        AllowsTransparency = true; Background = Brushes.Transparent;
        Left = Math.Max(0, SystemParameters.WorkArea.Right - Width - 18); Top = SystemParameters.WorkArea.Bottom - Height - 18;
        var border = new Border { CornerRadius = new(12), BorderThickness = new(1), BorderBrush = new SolidColorBrush(Color.FromRgb(51, 79, 93)), Background = new SolidColorBrush(Color.FromArgb(245, 14, 25, 38)), Padding = new(14, 10, 14, 10) };
        var panel = new StackPanel(); border.Child = panel;
        panel.Children.Add(new TextBlock { Text = "AIRSCOPE   ·   WI-FI TRAFFIC", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(145, 164, 188)), Margin = new(0, 0, 0, 5) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _download = new TextBlock { Text = "↓ —", FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(81, 224, 196)), Width = 100 };
        _upload = new TextBlock { Text = "↑ —", FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(172, 156, 255)) };
        row.Children.Add(_download); row.Children.Add(_upload); panel.Children.Add(row); Content = border;
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) showMain(); else DragMove(); };
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open AirScope" }; open.Click += (_, _) => showMain(); menu.Items.Add(open);
        var close = new MenuItem { Header = "Hide widget" }; close.Click += (_, _) => hide(); menu.Items.Add(close); ContextMenu = menu;
    }
    public void Update(double? down, double? up)
    {
        _download.Text = down is double d ? $"↓ {d:F1}" : "↓ —";
        _upload.Text = up is double u ? $"↑ {u:F1}" : "↑ —";
        ToolTip = "Download / upload in Mbps · drag to move · double-click to open";
    }
}
