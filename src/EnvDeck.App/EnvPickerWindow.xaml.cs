using System.Windows;
using EnvDeck.App.ViewModels;
using EnvDeck.Core;

namespace EnvDeck.App;

public partial class EnvPickerWindow : Window
{
    readonly EnvPickerViewModel _vm;

    public EnvPickerWindow(EnvPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Loaded += (_, _) => FilterBox.Focus();
    }

    public List<ProfileVariable> Result { get; private set; } = [];

    void Add_Click(object sender, RoutedEventArgs e)
    {
        Result = _vm.BuildResult();
        if (Result.Count == 0)
        {
            Dialogs.Info("Nothing selected", "Tick at least one variable or list entry.");
            return;
        }
        DialogResult = true;
    }
}
