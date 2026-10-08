using System.Windows;
using System.Windows.Controls;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class TeamsPage : UserControl
{
    private TeamsViewModel? Vm => DataContext as TeamsViewModel;

    public TeamsPage()
    {
        InitializeComponent();
        CboRoleFilter.ItemsSource = TeamsViewModel.RoleFilters;
    }

    // ── Add ──

    private void BtnAddDirector_Click(object sender, RoutedEventArgs e) => AddMember(TeamRoles.Director);
    private void BtnAddPe_Click(object sender, RoutedEventArgs e)       => AddMember(TeamRoles.Pe);
    private void BtnAddPa_Click(object sender, RoutedEventArgs e)       => AddMember(TeamRoles.Pa);

    private void AddMember(string role)
    {
        if (Vm is not { } vm) return;
        var dlg = new TeamMemberDialog(role, null, vm.Regions, vm.AllSubRegions(), vm.ActivePms())
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result != null) vm.SaveMember(dlg.Result);
    }

    private void BtnAddSubRegion_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new SubRegionDialog(null, vm.Regions, vm.SubRegions.Select(s => s.Name).ToList())
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result != null) vm.SaveSubRegion(dlg.Result);
    }

    // ── People ──

    private void BtnEditPerson_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || ((Button)sender).Tag is not PersonRow row) return;
        if (row.Pm is { } pm)
        {
            var pmDlg = new TeamMemberDialog(pm, vm.ActivePes()) { Owner = Window.GetWindow(this) };
            if (pmDlg.ShowDialog() == true && pmDlg.SelectedPeId != pm.PeId) vm.SetPmPe(pm, pmDlg.SelectedPeId);
            return;
        }
        if (row.Member is not { } m) return;
        var dlg = new TeamMemberDialog(m.Role, m, vm.Regions, vm.AllSubRegions(), vm.ActivePms())
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result != null) vm.SaveMember(dlg.Result);
    }

    private void BtnToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || ((Button)sender).Tag is not PersonRow { Member: { } m }) return;
        vm.SetMemberActive(m, !m.IsActive);
    }

    private void BtnDeletePerson_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || ((Button)sender).Tag is not PersonRow { Member: { } m }) return;
        var extra = m.Role == TeamRoles.Pe ? "\n\nPMs who report to them will have no team (and no sub-region) until moved to another PE." : "";
        if (MessageBox.Show($"Delete {TeamMemberDialog.RoleName(m.Role)} {m.FullName}?{extra}\n\nTo keep them on record, Deactivate instead.",
                "Delete Team Member", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            vm.DeleteMember(m);
    }

    private void CboBulkPe_DropDownOpened(object? sender, EventArgs e)
    {
        if (Vm is { } vm) CboBulkPe.ItemsSource = vm.PeOptions(); // fresh each time (PEs may have just been added)
    }

    private void BtnBulkSetPe_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var pms = PeopleGrid.SelectedItems.OfType<PersonRow>().Where(r => r.Pm != null).Select(r => r.Pm!).ToList();
        if (pms.Count == 0)
        {
            MessageBox.Show("Select one or more PMs in the list first (Ctrl+Click / Shift+Click). Tip: set Role to PM to list only PMs.",
                "No PMs Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CboBulkPe.SelectedItem is not SubRegionOption opt)
        {
            MessageBox.Show("Choose a PE to put them under.", "No PE", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Put {pms.Count} PM(s) under {opt.Name}?\n\n{string.Join(", ", pms.Select(p => p.FullName))}",
                "Set PE", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        vm.SetPmsPe(pms, opt.Id == 0 ? null : opt.Id);
    }

    // ── Sub-regions ──

    private void BtnEditSubRegion_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || ((Button)sender).Tag is not SubRegionRow row) return;
        var dlg = new SubRegionDialog(row.Sub, vm.Regions, vm.SubRegions.Select(s => s.Name).ToList())
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result != null) vm.SaveSubRegion(dlg.Result);
    }

    private void BtnDeleteSubRegion_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || ((Button)sender).Tag is not SubRegionRow row) return;
        if (MessageBox.Show($"Delete sub-region {row.Name}?\n\nIts {row.Clusters} cluster(s) will have no sub-region (any {row.Region} PM can then be assigned), " +
                            "and its PE(s) will have no sub-region until you pick another.",
                "Delete Sub-region", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            vm.DeleteSubRegion(row.Sub);
    }

    private void BtnBulkSetSubRegion_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var rows = ClustersGrid.SelectedItems.OfType<ClusterRow>().ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("Select one or more clusters in the list first (Ctrl+Click / Shift+Click).",
                "No Clusters Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CboBulkSubRegion.SelectedItem is not SubRegionOption opt)
        {
            MessageBox.Show("Choose a sub-region to apply.", "No Sub-region", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (rows.Count > 1 && MessageBox.Show($"Set {rows.Count} clusters to {opt.Name}?", "Set Sub-region",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        vm.SetClusterSubRegion(rows.Select(r => r.Location.Id), opt.Id == 0 ? null : opt.Id);
    }
}
