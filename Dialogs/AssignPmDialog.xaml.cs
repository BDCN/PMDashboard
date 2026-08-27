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
    {
        InitializeComponent();
        TxtSiteName.Text  = $"Site: {site.Name}";
        PmGrid.ItemsSource = pms;

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
