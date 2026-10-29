using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
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
            var answer = Dialogs.Ask($"Save changes to '{editor.Name}'?",
                "EnvDeck is closing; unsaved edits are lost otherwise.", "Save", "Don't save");
            if (answer == DialogChoice.Cancel) e.Cancel = true;
            else if (answer == DialogChoice.Primary) _vm.Profiles.SaveCommand.Execute(null);
        }
        base.OnClosing(e);
    }

    void SearchGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        _vm.Search.OpenCommand.Execute(SearchGrid.SelectedItem);

    /// <summary>
    /// Reacts to the switch however it was flipped (mouse, Space, UI Automation); a ToggleButton's Command only
    /// runs on click. Changes coming from the view model (switch already matches the state) are ignored.
    /// </summary>
    void ProfileSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: ProfileItemViewModel item } toggle && toggle.IsChecked != item.IsActive)
            _vm.Profiles.ToggleActiveCommand.Execute(item);
    }
}
