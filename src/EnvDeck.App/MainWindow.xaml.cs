using System.Collections.ObjectModel;
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
    object? _dragCandidate;

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
        if (!e.Cancel && _vm.CurrentEnv.HasPending)
        {
            var answer = Dialogs.Ask(L.T("EnvPendingCloseHeading"), L.T("EnvPendingCloseBody", _vm.CurrentEnv.PendingCount),
                L.T("Apply"), L.T("Discard"));
            // Stay open when the apply was cancelled or refused, so the edits are not lost.
            if (answer == DialogChoice.Cancel || (answer == DialogChoice.Primary && !_vm.CurrentEnv.ApplyPending()))
                e.Cancel = true;
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

    // ---------------------------------------------------------------- sorting

    /// <summary>Header clicks cycle ascending, descending, unsorted (WPF alone would go ascending again).</summary>
    void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (e.Column.SortDirection != ListSortDirection.Descending) return;
        var grid = (DataGrid)sender;
        e.Handled = true;
        grid.Items.SortDescriptions.Clear();
        foreach (var column in grid.Columns) column.SortDirection = null;
    }

    /// <summary>Sorting only changes how rows are shown, so moving a row while sorted would look like it did nothing.</summary>
    bool RejectIfSorted(DataGrid grid)
    {
        if (grid.Items.SortDescriptions.Count == 0) return false;
        _vm.Status = L.T("SortedNoReorder");
        return true;
    }

    // ---------------------------------------------------------------- reorder rows (profile variables, list entries)

    const string DragFormat = "EnvDeck.Row";

    void Grip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragCandidate = (sender as FrameworkElement)?.DataContext;
    }

    void Grip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is not { } item || e.LeftButton != MouseButtonState.Pressed
            || FindAncestor<DataGrid>((DependencyObject)sender) is not { } grid)
            return;
        var moved = e.GetPosition(this) - _dragStart;
        if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance
            && Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;

        _dragCandidate = null;
        if (RejectIfSorted(grid)) return;
        // Moving items is not allowed while a cell is being edited.
        grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        grid.SelectedItem = item;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DragFormat, item), DragDropEffects.Move);
        grid.SelectedItem = item;
    }

    /// <summary>Moves the dragged row live as the pointer passes over other rows of the same grid.</summary>
    void ReorderGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var dragged = e.Data.GetData(DragFormat);
        var grid = (DataGrid)sender;
        if (dragged is null || !grid.Items.Contains(dragged))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;

        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is { } target
            && !ReferenceEquals(target, dragged))
        {
            switch (grid.ItemsSource)
            {
                case ObservableCollection<VariableViewModel> variables:
                    Move(variables, (VariableViewModel)dragged, (VariableViewModel)target);
                    break;
                case ObservableCollection<EnvEntryViewModel> entries:
                    Move(entries, (EnvEntryViewModel)dragged, (EnvEntryViewModel)target);
                    break;
            }
        }
    }

    void ReorderGrid_Drop(object sender, DragEventArgs e) => e.Handled = true;

    static void Move<T>(ObservableCollection<T> items, T dragged, T target)
    {
        var from = items.IndexOf(dragged);
        var to = items.IndexOf(target);
        if (from >= 0 && to >= 0) items.Move(from, to);
    }

    /// <summary>Keyboard alternative to dragging: Alt+Up / Alt+Down moves the selected variable.</summary>
    void VariablesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.SystemKey is not (Key.Up or Key.Down)) return;
        if (RejectIfSorted(VariablesGrid)) { e.Handled = true; return; }
        VariablesGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        var command = e.SystemKey == Key.Up ? _vm.Profiles.MoveUpCommand : _vm.Profiles.MoveDownCommand;
        command.Execute(null);
        if (_vm.Profiles.SelectedVariable is { } selected) FocusItem(VariablesGrid, selected);
        e.Handled = true;
    }

    /// <summary>Moving a row drops keyboard focus; put it back on the moved row so Alt+Up/Down can repeat.</summary>
    static void FocusItem(DataGrid grid, object item)
    {
        var column = grid.CurrentColumn ?? grid.Columns.LastOrDefault();
        grid.ScrollIntoView(item);
        grid.UpdateLayout();
        if (column?.GetCellContent(item)?.Parent is DataGridCell cell) cell.Focus();
    }

    // ---------------------------------------------------------------- environment tab

    /// <summary>
    /// Locked (managed by a profile that is on) and deleted rows are read-only; scope and name are fixed once a
    /// variable exists; a PATH-like value with locked entries is edited entry by entry below.
    /// </summary>
    void EnvGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not EnvRowViewModel row) return;
        var column = e.Column.SortMemberPath;
        e.Cancel = row.IsLocked || row.IsDeleted
                   || (!row.IsNew && column is nameof(EnvRowViewModel.Scope) or nameof(EnvRowViewModel.Name))
                   || (row.IsPartlyLocked && column == nameof(EnvRowViewModel.Value));
    }

    void EntriesGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e) =>
        e.Cancel = e.Row.Item is EnvEntryViewModel { IsLocked: true };

    /// <summary>Keyboard alternative to dragging: Alt+Up / Alt+Down moves the selected entry.</summary>
    void EntriesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt || e.SystemKey is not (Key.Up or Key.Down)) return;
        EntriesGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        _vm.CurrentEnv.MoveEntry(e.SystemKey == Key.Up ? -1 : 1);
        if (_vm.CurrentEnv.SelectedEntry is { } selected) FocusItem(EntriesGrid, selected);
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
