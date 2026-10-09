using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PMDashboard.Data.Models;

namespace PMDashboard.Dialogs;

public partial class AssignPmDialog : Window
{
    public PM?  SelectedPm    { get; private set; }
    public bool WasUnassigned { get; private set; }

    public AssignPmDialog(Site site, List<PM> pms, PM? currentPm)
        : this($"Site: {site.Name}", pms, currentPm) { }

    /// <summary>Generalized constructor — used for ROMP-level assignment too (subjectLabel e.g. "ROMP: X").</summary>
    public AssignPmDialog(string subjectLabel, List<PM> pms, PM? currentPm)
    {
        InitializeComponent();
        TxtSiteName.Text  = subjectLabel;
        PmGrid.ItemsSource = pms;
        // Team column only when the caller marked PMs outside the team (Sites page pickers).
        ColTeam.Visibility = pms.Any(p => !string.IsNullOrEmpty(p.TeamLabel)) ? Visibility.Visible : Visibility.Collapsed;

        // Pre-select currently assigned PM
        if (currentPm != null)
            PmGrid.SelectedItem = pms.FirstOrDefault(p => p.Id == currentPm.Id);

        PmGrid.SelectionChanged += (_, _) =>
            BtnAssign.IsEnabled = PmGrid.SelectedItem is PM;
    }

    private void PmGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PmGrid.SelectedItem is PM) Assign();
    }

    private void BtnAssign_Click(object sender, RoutedEventArgs e)   => Assign();
    private void BtnUnassign_Click(object sender, RoutedEventArgs e)  => Unassign();
    private void BtnCancel_Click(object sender, RoutedEventArgs e)    => DialogResult = false;

    private void Assign()
    {
        if (PmGrid.SelectedItem is not PM pm) return;
        SelectedPm   = pm;
        DialogResult = true;
    }

    private void Unassign()
    {
        WasUnassigned = true;
        DialogResult  = true;
    }
}
