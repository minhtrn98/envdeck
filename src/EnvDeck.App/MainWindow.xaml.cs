using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using EnvDeck.App.ViewModels;

namespace EnvDeck.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm = new MainViewModel();
        Activated += (_, _) => _vm.OnActivated();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.Profiles.Editor is { IsDirty: true } editor)
        {
            var answer = MessageBox.Show(this, $"Save changes to '{editor.Name}'?", "EnvDeck",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) e.Cancel = true;
            else if (answer == MessageBoxResult.Yes) _vm.Profiles.SaveCommand.Execute(null);
        }
        base.OnClosing(e);
    }

    void SearchGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        _vm.Search.OpenCommand.Execute(SearchGrid.SelectedItem);
}
