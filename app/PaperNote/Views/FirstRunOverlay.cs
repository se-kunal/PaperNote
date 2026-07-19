using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaperNote.Views;

// One-time first-launch nudge: get the user's hands on the signature move once.
// Bottom-center toast, closes itself after a few seconds or on click.
public sealed class FirstRunOverlay : Window
{
    public FirstRunOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;

        var text = new TextBlock
        {
            FontSize = 15,
            Foreground = (Brush)Application.Current.Resources["InkBrush"],
            Margin = new Thickness(20, 14, 20, 14)
        };
        text.Inlines.Add("Press ");
        text.Inlines.Add(new Run("Ctrl+Alt+N")
        {
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["AccentBrush"]
        });
        text.Inlines.Add(" — an instant note, from anywhere in Windows. Try it now.");

        Content = new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            BorderBrush = (Brush)Application.Current.Resources["LineBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = text,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, Opacity = 0.30, BlurRadius = 24, ShadowDepth = 4, Direction = 270
            }
        };

        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Bottom - ActualHeight - 48;
        };

        MouseDown += (_, _) => Close();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }
}
