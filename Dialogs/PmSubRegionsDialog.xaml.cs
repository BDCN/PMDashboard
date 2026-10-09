using System.ComponentModel;
using System.Windows;
using PMDashboard.Data.Models;

namespace PMDashboard.Dialogs;

/// <summary>Pick a PM's default sub-region and any further sub-regions they cover. The caller
/// saves via PmTeamService.SetSubRegions.</summary>
public partial class PmSubRegionsDialog : Window
{
    public sealed record Choice(int? Id, string Label);

    public sealed class OtherItem : INotifyPropertyChanged
    {
        public int    Id    { get; init; }
        public string Label { get; init; } = "";
        private bool _checked, _enabled = true;
        public bool IsChecked { get => _checked; set { _checked = value; Raise(nameof(IsChecked)); } }
        public bool IsEnabled { get => _enabled; set { _enabled = value; Raise(nameof(IsEnabled)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly List<OtherItem> _others;

    public int?      DefaultId { get; private set; }
    public List<int> OtherIds  { get; private set; } = new();

    public PmSubRegionsDialog(string pmName, IReadOnlyList<SubRegion> subRegions, int? currentDefault, IEnumerable<int> currentOthers)
    {
        InitializeComponent();
        TxtTitle.Text = $"Sub-regions for {pmName}";
        Title = TxtTitle.Text;

        var choices = new[] { new Choice(null, "— no team —") }
            .Concat(subRegions.Select(s => new Choice(s.Id, $"{s.Name}  ({s.Region})"))).ToList();
        CboDefault.ItemsSource = choices;

        var others = currentOthers.ToHashSet();
        _others = subRegions.Select(s => new OtherItem { Id = s.Id, Label = $"{s.Name}  ({s.Region})", IsChecked = others.Contains(s.Id) }).ToList();
        ListOthers.ItemsSource = _others;

        CboDefault.SelectedItem = choices.FirstOrDefault(c => c.Id == currentDefault) ?? choices[0];
        SyncOthers();
    }

    // The default can't also be an "other"; with no default there's no team, so no others either.
    private void SyncOthers()
    {
        var def = (CboDefault.SelectedItem as Choice)?.Id;
        foreach (var o in _others)
        {
            o.IsEnabled = def != null && o.Id != def;
            if (!o.IsEnabled) o.IsChecked = false;
        }
    }

    private void CboDefault_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SyncOthers();

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        DefaultId = (CboDefault.SelectedItem as Choice)?.Id;
        OtherIds  = _others.Where(o => o.IsChecked && o.Id != DefaultId).Select(o => o.Id).ToList();
        DialogResult = true;
    }
}
