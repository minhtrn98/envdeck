using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using EnvDeck.App.Localization;
using EnvDeck.App.ViewModels;

namespace EnvDeck.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    Point _dragStart;
    VariableViewModel? _dragCandidate;

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
            var answer = Dialogs.Ask(L.T("SaveChangesHeading", editor.Name), L.T("SaveBeforeCloseBody"),
                L.T("Save"), L.T("DontSave"));
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

    // ---------------------------------------------------------------- reorder variables

    void Grip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragCandidate = (sender as FrameworkElement)?.DataContext as VariableViewModel;
    }

    void Grip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is not { } item || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(this) - _dragStart;
        if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance
            && Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;

        _dragCandidate = null;
        // Moving items is not allowed while a cell is being edited.
        VariablesGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        _vm.Profiles.SelectedVariable = item;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(VariableViewModel), item), DragDropEffects.Move);
        _vm.Profiles.SelectedVariable = item;
    }

    /// <summary>Moves the dragged row live as the pointer passes over other rows.</summary>
    void VariablesGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(VariableViewModel)) is not VariableViewModel dragged
            || _vm.Profiles.Editor is not { } editor)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;

        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is VariableViewModel target
            && !ReferenceEquals(target, dragged))
        {
            var from = editor.Variables.IndexOf(dragged);
            var to = editor.Variables.IndexOf(target);
            if (from >= 0 && to >= 0) editor.Variables.Move(from, to);
        }
    }

    void VariablesGrid_Drop(object sender, DragEventArgs e) => e.Handled = true;

    /// <summary>Keyboard alternative to dragging: Alt+Up / Alt+Down moves the selected variable.</summary>
    void VariablesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.SystemKey is not (Key.Up or Key.Down)) return;
        VariablesGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        var command = e.SystemKey == Key.Up ? _vm.Profiles.MoveUpCommand : _vm.Profiles.MoveDownCommand;
        command.Execute(null);
        if (_vm.Profiles.SelectedVariable is { } selected) VariablesGrid.ScrollIntoView(selected);
        e.Handled = true;
    }

    static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        return element as T;
    }
}
