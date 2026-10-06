using System.Windows;
using System.Windows.Controls;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class CoveragePlannerPage : UserControl
{
    private CoveragePlannerViewModel? Vm => DataContext as CoveragePlannerViewModel;

    public CoveragePlannerPage()
    {
        InitializeComponent();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.Load();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e) => Vm?.OpenSettings?.Invoke();

    private void BtnAssignGap_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (((Button)sender).Tag is not GapFitRow row) return;

        var confirm = MessageBox.Show(
            $"Assign site {row.Campus} ({row.Cluster}) to {row.PmName}?\n\n" +
            $"Their busiest week during {row.Nbd:MMM d, yyyy} – {row.Pfho:MMM d, yyyy} would be {row.PeakAfter:0.00} " +
            $"({row.SpareAfter:0.00} spare). This assigns the whole site, the same as the Sites page.",
            "Assign Site", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (!vm.AssignFromGap(row))
            MessageBox.Show("That site could not be found — it may have been deleted. Click Recalculate.",
                "Assign Site", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
