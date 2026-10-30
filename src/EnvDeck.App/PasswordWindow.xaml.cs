using System.Windows;
using EnvDeck.App.Localization;
using EnvDeck.Core;

namespace EnvDeck.App;

/// <summary>Asks for a password; with <c>confirm</c> it asks twice and insists on a minimum length.</summary>
public partial class PasswordWindow : Window
{
    readonly bool _confirm;

    public PasswordWindow(string title, string label, bool confirm, string? note)
    {
        InitializeComponent();
        _confirm = confirm;
        Title = title;
        LabelText.Text = label;
        if (confirm)
        {
            RepeatLabel.Visibility = Second.Visibility = Visibility.Visible;
        }
        if (note is not null)
        {
            NoteText.Text = note;
            NoteText.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => First.Focus();
    }

    public string Value => First.Password;

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var error = First.Password.Length == 0 ? L.T("PasswordEmpty")
            : _confirm && First.Password.Length < BundleCrypto.MinPasswordLength ? L.T("PasswordTooShort", BundleCrypto.MinPasswordLength)
            : _confirm && First.Password != Second.Password ? L.T("PasswordMismatch")
            : null;
        if (error is null)
        {
            DialogResult = true;
            return;
        }
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        (First.Password.Length == 0 || First.Password.Length < BundleCrypto.MinPasswordLength ? First : Second).Focus();
    }
}
