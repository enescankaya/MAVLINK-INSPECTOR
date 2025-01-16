using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MavlinkInspector.Controls;

public partial class ModernMessageBox : Window
{
    public static MessageBoxResult ShowMessage(string message, string title = "Message",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Information,
        Window? owner = null)
    {
        var msgBox = new ModernMessageBox(message, title, buttons, icon)
        {
            Owner = owner ?? Application.Current.MainWindow
        };
        msgBox.ShowDialog();
        return msgBox.Result;
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    private ModernMessageBox(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();

        Title = title;
        MessageText.Text = message;

        ConfigureButtons(buttons);
        ConfigureIconAndColors(icon);

        // Handle Escape key
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape && CancelButton.Visibility == Visibility.Visible)
            {
                Result = MessageBoxResult.Cancel;
                Close();
            }
        };
    }

    private void ConfigureIconAndColors(MessageBoxImage icon)
    {
        var (iconPath, accentColor) = icon switch
        {
            MessageBoxImage.Error => ("/Resources/error.png", "#E53935"),
            MessageBoxImage.Warning => ("/Resources/warning.png", "#FBC02D"),
            MessageBoxImage.Information => ("/Resources/info.png", "#007ACC"),
            MessageBoxImage.Question => ("/Resources/question.png", "#4CAF50"),
            _ => ("/Resources/info.png", "#007ACC")
        };

        try
        {
            MessageIcon.Source = new BitmapImage(new Uri($"pack://application:,,,{iconPath}"));

            if (accentColor != null)
            {
                var converter = new BrushConverter();
                if (converter.ConvertFromString(accentColor) is SolidColorBrush brush)
                {
                    // Ana butonu vurgula
                    OkButton.BorderBrush = brush;
                    OkButton.Background = new SolidColorBrush(
                        Color.FromArgb(40, brush.Color.R, brush.Color.G, brush.Color.B));
                }
            }
        }
        catch
        {
            MessageIcon.Visibility = Visibility.Collapsed;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            Close();
        else
            DragMove();
    }

    private void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                CancelButton.Visibility = Visibility.Collapsed;
                OkButton.Focus();
                break;
            case MessageBoxButton.OKCancel:
                OkButton.Content = "OK";
                CancelButton.Content = "Cancel";
                CancelButton.Focus();
                break;
            case MessageBoxButton.YesNo:
                OkButton.Content = "Yes";
                CancelButton.Content = "No";
                CancelButton.Focus();
                break;
            case MessageBoxButton.YesNoCancel:
                // İhtiyaç duyulursa implement edilebilir
                break;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.OK;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Cancel;
        Close();
    }
}
