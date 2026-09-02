using System.Windows;
using System.Windows.Controls;
using PMDashboard.Data.Models;
using PMDashboard.ViewModels;

namespace PMDashboard.Dialogs;

public partial class ManageColumnsDialog : Window
{
    private readonly CustomDataViewModel _vm;

    public ManageColumnsDialog(CustomDataViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void TxtColumnName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (((TextBox)sender).Tag is not CustomColumn col) return;
        var newName = ((TextBox)sender).Text.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == col.Name) return;
        _vm.RenameColumn(col, newName);
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is CustomColumn col) _vm.MoveColumnUp(col);
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is CustomColumn col) _vm.MoveColumnDown(col);
    }

    private void BtnDeleteColumn_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not CustomColumn col) return;

        // Name the data at stake — the delete purges values, and a re-added column gets a
        // new Key, so there is no way to recover them afterwards.
        var affected = _vm.CountRowsWithValuesFor(col);
        var detail = affected > 0
            ? $"\n\n{affected} row(s) have a value in this column. Those values will be permanently deleted."
            : "\n\nNo rows currently have a value in this column.";

        var confirm = MessageBox.Show(
            $"Delete column \"{col.Name}\" permanently?{detail}\n\nThis cannot be undone.",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes) _vm.DeleteColumn(col);
    }

    private void BtnAddColumn_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
        var name = TxtNewColumnName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TxtError.Text = "Column name is required.";
            TxtError.Visibility = Visibility.Visible;
            return;
        }
        var type = (CboNewColumnType.SelectedItem as ComboBoxItem)?.Content as string ?? "Text";
        _vm.AddColumn(name, type);
        TxtNewColumnName.Clear();
        CboNewColumnType.SelectedIndex = 0;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
