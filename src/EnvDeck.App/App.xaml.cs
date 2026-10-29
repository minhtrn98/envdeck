using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using EnvDeck.App.Localization;
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
            ShowError("CouldNotStart", "EnvDeck could not start", ex.Message);
            Shutdown(1);
        }
    }

    static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowError("Unexpected", "Something unexpected happened", e.Exception.ToString());
    }

    static void ShowError(string headingKey, string fallbackHeading, string details)
    {
        try
        {
            Dialogs.Error(details, L.T(headingKey));
        }
        catch (Exception)
        {
            // The UI (or the translations) may be what is broken; the Win32 box always works.
            MessageBox.Show(details, fallbackHeading, MessageBoxButton.OK, MessageBoxImage.Error);
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
