using System.Windows;

namespace EnvDeck.App;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string label, string initial, IEnumerable<string> suggestions)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        Input.ItemsSource = suggestions.ToList();
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            if (Input.Template.FindName("PART_EditableTextBox", Input) is System.Windows.Controls.TextBox box) box.SelectAll();
        };
    }

    public string Value => Input.Text.Trim();

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
