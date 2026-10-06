using System.Windows;
using PMDashboard.Data.Models;

namespace PMDashboard.Dialogs;

/// <summary>
/// Re-associates a site (and, with it, its ROMPs) to a campus in the same cluster — an existing
/// one from the list, or a new one typed in. The caller does the move via CampusService.
/// </summary>
public partial class MoveSiteDialog : Window
{
    /// <summary>The chosen campus name, exactly as picked or typed.</summary>
    public string CampusName { get; private set; } = "";

    public MoveSiteDialog(Site site, IReadOnlyList<Campus> campusesInCluster, string? currentCampusName)
    {
        InitializeComponent();
        TxtHeading.Text = $"Move site {site.Name}";
        Title           = TxtHeading.Text;
        CboCampus.ItemsSource = campusesInCluster;
        CboCampus.SelectedItem = campusesInCluster.FirstOrDefault(c => c.Name == currentCampusName);
        TxtPmNote.Text = site.PmOverride
            ? "This site has its own PM, so it keeps that PM after the move."
            : "The site will take the new campus's PM (if it has one).";
        Loaded += (_, _) => CboCampus.Focus();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var name = (CboCampus.SelectedItem as Campus)?.Name ?? CboCampus.Text;
        if (string.IsNullOrWhiteSpace(name))
        {
            TxtError.Visibility = Visibility.Visible;
            return;
        }
        CampusName = name.Trim();
        DialogResult = true;
    }
}
