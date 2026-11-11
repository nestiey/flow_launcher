using System.Windows;
using System.Windows.Controls;

namespace Flow.Publisher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        app.Run(new Window
        {
            Title = "FLOW Publisher",
            Width = 600,
            Height = 340,
            Content = new TextBlock
            {
                Text = "Sunlit Valley · 관리자 게시\n개발용 프로젝트 골격",
                Margin = new Thickness(24),
                FontSize = 20
            }
        });
    }
}
