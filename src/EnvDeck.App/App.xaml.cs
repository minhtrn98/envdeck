using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using EnvDeck.Core;

namespace EnvDeck.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (DeckException ex)
        {
            ShowError("EnvDeck could not start", ex.Message);
            Shutdown(1);
        }
    }

    static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowError("Something unexpected happened", e.Exception.ToString());
    }

    static void ShowError(string heading, string details)
    {
        try
        {
            Dialogs.Error(details, heading);
        }
        catch (Exception)
        {
            // The UI itself may be what is broken; the Win32 box always works.
            MessageBox.Show(details, heading, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is null) ^ Invert ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
