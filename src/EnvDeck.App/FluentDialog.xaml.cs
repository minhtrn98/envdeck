using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EnvDeck.App;

public enum DialogKind { Question, Info, Warning, Danger, Error }

public enum DialogChoice { Primary, Secondary, Cancel }

/// <summary>
/// Windows 11 style replacement for MessageBox (which is the classic Win32 box and ignores dark mode):
/// a heading, optional details and verb buttons, following the system theme and accent color.
/// </summary>
public partial class FluentDialog : Window
{
    public FluentDialog(DialogKind kind, string heading, string? body, string primary, string? secondary, string? cancel)
    {
        InitializeComponent();
        Heading.Text = heading;
        Body.Text = body ?? "";
        Body.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
        SetGlyph(kind);

        // Destructive actions get a red button and are never the Enter default.
        var primaryButton = AddButton(primary, DialogChoice.Primary,
            kind == DialogKind.Danger ? (Style)Resources["DangerButton"] : TryFindResource("AccentButtonStyle") as Style);
        if (secondary is not null) AddButton(secondary, DialogChoice.Secondary, null);
        var cancelButton = cancel is null ? null : AddButton(cancel, DialogChoice.Cancel, null);

        var defaultButton = kind == DialogKind.Danger && cancelButton is not null ? cancelButton : primaryButton;
        defaultButton.IsDefault = true;
        (cancelButton ?? primaryButton).IsCancel = true;
        Loaded += (_, _) => defaultButton.Focus();

        // Like MessageBox: Ctrl+C copies the text.
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) =>
            Secrecy.Copy(string.IsNullOrEmpty(Body.Text) ? Heading.Text : $"{Heading.Text}{Environment.NewLine}{Body.Text}", secret: false)));
    }

    public DialogChoice Choice { get; private set; } = DialogChoice.Cancel;

    Button AddButton(string text, DialogChoice choice, Style? style)
    {
        var button = new Button { Content = text, Margin = new Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (style is not null) button.Style = style;
        button.Click += (_, _) =>
        {
            Choice = choice;
            DialogResult = choice != DialogChoice.Cancel;
        };
        ButtonRow.Children.Add(button);
        return button;
    }

    void SetGlyph(DialogKind kind)
    {
        var (glyph, brushKey, fallback) = kind switch
        {
            DialogKind.Info => ("", "AccentTextFillColorPrimaryBrush", "#0067C0"),
            DialogKind.Warning => ("", "SystemFillColorCautionBrush", "#9D5D00"),
            DialogKind.Danger => ("", "SystemFillColorCriticalBrush", "#C42B1C"),
            DialogKind.Error => ("", "SystemFillColorCriticalBrush", "#C42B1C"),
            _ => (null, null, null), // confirmations stay icon-less, like WinUI's ContentDialog
        };
        if (glyph is null) return;
        Glyph.Text = glyph;
        Glyph.Foreground = TryFindResource(brushKey!) as Brush
                           ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback!));
        Glyph.Visibility = Visibility.Visible;
    }
}
