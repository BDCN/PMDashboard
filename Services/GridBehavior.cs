using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PMDashboard.Services;

/// <summary>
/// Attached-property wrapper so "select the existing text when a cell enters edit mode" can be
/// turned on for every DataGrid in the app via one Setter in AppDataGridStyle (App.xaml), instead
/// of each grid wiring its own event handler. DataGrid.PreparingCellForEdit is a plain CLR event,
/// not a RoutedEvent, so XAML's EventSetter can't target it directly — this is the standard WPF
/// workaround: an attached property whose change callback subscribes/unsubscribes in code.
/// </summary>
public static class GridBehavior
{
    public static readonly DependencyProperty AutoSelectOnEditProperty =
        DependencyProperty.RegisterAttached(
            "AutoSelectOnEdit", typeof(bool), typeof(GridBehavior),
            new PropertyMetadata(false, OnAutoSelectOnEditChanged));

    public static void SetAutoSelectOnEdit(DependencyObject element, bool value) =>
        element.SetValue(AutoSelectOnEditProperty, value);

    public static bool GetAutoSelectOnEdit(DependencyObject element) =>
        (bool)element.GetValue(AutoSelectOnEditProperty);

    private static void OnAutoSelectOnEditChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;
        grid.PreparingCellForEdit -= Grid_PreparingCellForEdit; // idempotent: never double-subscribe
        if ((bool)e.NewValue)
            grid.PreparingCellForEdit += Grid_PreparingCellForEdit;
    }

    /// <summary>
    /// The WPF default just drops the caret wherever the click landed, with nothing selected.
    /// EditingElement is the TextBox itself for every editable column in this app (both the
    /// built-in DataGridTextColumn editor and every DataGridTemplateColumn whose
    /// CellEditingTemplate is a bare TextBox) — a Dispatcher.BeginInvoke defers SelectAll until
    /// after the DataGrid finishes moving focus into it, since calling it inline here is too early.
    /// </summary>
    private static void Grid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is TextBox tb)
            tb.Dispatcher.BeginInvoke(new Action(tb.SelectAll), DispatcherPriority.Input);
    }
}
