using EnvDeck.Core;

namespace EnvDeck.App;

/// <summary>What the UI shows in place of a secret's value. The real value stays in the model; only the display is masked.</summary>
static class Secrecy
{
    public const string Mask = SecretNames.Mask;

    static DeckService? _service;

    public static void Use(DeckService service) => _service = service;

    public static bool IsSecret(string? name) =>
        !string.IsNullOrWhiteSpace(name) && (_service?.IsSecret(name.Trim()) ?? SecretNames.Looks(name));

    /// <summary>The value as it may be displayed: masked when the variable is a secret and the value is not empty.</summary>
    public static string Show(string? name, string? value) =>
        string.IsNullOrEmpty(value) ? "" : IsSecret(name) ? Mask : value;

    public static void Set(string name, bool secret) => _service?.SetSecret(name, secret);

    public const int ClipboardSeconds = 30;

    /// <summary>
    /// Copies text; a secret is removed from the clipboard again after a while, if nothing else replaced it.
    /// The clipboard can be held by another program (clipboard managers, remote sessions): it is tried a few times,
    /// and false means it could not be written.
    /// </summary>
    public static bool Copy(string value, bool secret)
    {
        if (!TrySetText(value)) return false;
        if (!secret) return true;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(ClipboardSeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (System.Windows.Clipboard.ContainsText() && System.Windows.Clipboard.GetText() == value)
                    System.Windows.Clipboard.Clear();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // The clipboard is busy; it is not worth retrying.
            }
        };
        timer.Start();
        return true;
    }

    static bool TrySetText(string value)
    {
        for (var attempt = 0; attempt < 2; attempt++) // WPF itself retries for about a second
        {
            try
            {
                System.Windows.Clipboard.SetText(value);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(60);
            }
        }
        return false;
    }
}
