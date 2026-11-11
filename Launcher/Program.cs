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
        var status = new TextBlock
        {
            Text = $"Sunlit Valley · Minecraft 1.20.1 · Forge 47.4.0\n{InstallRoot}\nSunlit Valley 원본을 가져오세요.",
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 17
        };
        content.Children.Add(status);
        var import = new Button
        {
            Content = "Sunlit Valley 가져오기",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 110, 0, 0),
            Padding = new Thickness(12, 6, 12, 6)
        };
        import.Click += async (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Sunlit Valley 원본 인스턴스 폴더 선택"
            };
            if (picker.ShowDialog() != true)
                return;

            import.IsEnabled = false;
            status.Text = "Sunlit Valley 복사 중…";
            try
            {
                var destination = await Task.Run(() => InstanceImport.Copy(picker.FolderName, InstallRoot));
                status.Text = $"Sunlit Valley 가져오기 완료\n{destination}";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                status.Text = error.Message;
            }
            finally
            {
                import.IsEnabled = true;
            }
        };
        content.Children.Add(import);
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



