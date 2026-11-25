using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GTWModManager;

/// <summary>A simple question or warning box. Avalonia has no built-in MessageBox.</summary>
public sealed class MessageDialog : Window
{
    private MessageDialog(string message, bool confirm)
    {
        Title = "GTW Mod Manager";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = this.GetResourceObservable("CardBackground").ToBinding();
        this[!ForegroundProperty] = this.GetResourceObservable("TextPrimary").ToBinding();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (confirm)
        {
            var yes = new Button { Content = "Yes", MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
            yes.Classes.Add("accent");
            yes.Click += (_, _) => Close(true);
            var no = new Button { Content = "No", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
            no.Click += (_, _) => Close(false);
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
        }
        else
        {
            var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => Close(false);
            buttons.Children.Add(ok);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                buttons,
            },
        };
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>. Returns true only when "Yes" was chosen.</summary>
    public static async Task<bool> ShowAsync(Window owner, string message, bool confirm)
    {
        var dialog = new MessageDialog(message, confirm);
        return await dialog.ShowDialog<bool>(owner);
    }
}
