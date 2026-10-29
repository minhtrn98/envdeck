using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace EnvDeck.App.Localization;

public sealed record LanguageOption(string Code, string Name);

/// <summary>
/// Current UI language. XAML binds through <see cref="TrExtension"/>, so switching language
/// updates every label at once without restarting.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    // Must stay above Instance: static initializers run in declaration order and the constructor reads it.
    public static IReadOnlyList<LanguageOption> Languages { get; } = [new("en", "English"), new("vi", "Tiếng Việt")];

    public static Loc Instance { get; } = new();

    public string Language { get; private set; } = Normalize(null);

    public string this[string key] => Strings.Get(Language, key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed, for text that view models build themselves.</summary>
    public event Action? LanguageChanged;

    /// <param name="code">"en", "vi", or null to follow the Windows display language.</param>
    public void SetLanguage(string? code)
    {
        var language = Normalize(code);
        if (language == Language) return;
        Language = language;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    static string Normalize(string? code) =>
        Languages.FirstOrDefault(l => l.Code == code)?.Code
        ?? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi" ? "vi" : "en");
}

/// <summary>Translations for text built in code: <c>L.T("SavedAsVersion", name, version)</c>.</summary>
public static class L
{
    public static string T(string key) => Loc.Instance[key];

    public static string T(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Loc.Instance[key], args);
}

/// <summary>XAML: <c>Content="{l:Tr Save}"</c>, a binding that follows language changes.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary>A combo box item whose label is translated, e.g. the "All / User / Machine" scope filter.</summary>
public sealed class Choice : INotifyPropertyChanged
{
    readonly string _key;

    public Choice(string value, string key)
    {
        Value = value;
        _key = key;
        Loc.Instance.LanguageChanged += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }

    public string Value { get; }

    public string Label => L.T(_key);

    public event PropertyChangedEventHandler? PropertyChanged;
}
