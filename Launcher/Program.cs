using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flow.Launcher;

internal static class Program
{
    internal static string InstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "flow_launcher");

    [STAThread]
    private static void Main()
    {
        var app = new Application();
        var content = new Grid();
        content.Children.Add(new TextBlock
        {
            Text = $"Sunlit Valley · Minecraft 1.20.1 · Forge 47.4.0\n{InstallRoot}\n개발용 프로젝트 골격",
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 17
        });
        content.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/Sleeping.png")),
            Width = 132,
            Height = 128,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16),
            IsHitTestVisible = false
        });
        app.Run(new Window
        {
            Title = "FLOW Launcher",
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/Flow.ico")),
            Width = 600,
            Height = 340,
            MinWidth = 420,
            MinHeight = 290,
            Content = content
        });
    }
}

