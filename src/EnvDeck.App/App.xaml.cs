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
            MessageBox.Show(ex.Message, "EnvDeck could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "EnvDeck: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
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
