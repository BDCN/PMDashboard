using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

/// <summary>
/// Configuration → Source Data → "Edit Column Mapping…": which spreadsheet column fills each
/// Order field, per source type (Services/OrderColumnMapping). A row's text is one of
///   "(not imported)"  → skip
///   "Column I"        → fixed column letter
///   anything else     → a row-1 heading (matched case-insensitively)
/// </summary>
public partial class OrderColumnMappingDialog : Window
{
    public const string NotImported = "(not imported)";
    private const string ColumnPrefix = "Column ";

    private string _type;
    private Dictionary<int, string>? _sample;       // column → heading from the loaded sample
    private string? _sampleName;
    private readonly ObservableCollection<string> _choices = new();
    private readonly ObservableCollection<MappingRow> _rows = new();
    private bool _dirty, _loading;

    public OrderColumnMappingDialog(string sourceType)
    {
        InitializeComponent();
        _type = OrderColumnMapping.SourceTypes.Contains(sourceType) ? sourceType : "VADATA";
        foreach (var t in OrderColumnMapping.SourceTypes)
            CmbSource.Items.Add(new ComboBoxItem { Content = OrderColumnMapping.SourceLabel(t), Tag = t });
        RowsList.ItemsSource = _rows;

        _loading = true;
        CmbSource.SelectedIndex = Array.IndexOf(OrderColumnMapping.SourceTypes, _type);
        _loading = false;
        LoadRows(OrderColumnMapping.Load(_type));
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    private void LoadRows(Dictionary<string, ColumnSource> map)
    {
        RebuildChoices();
        _rows.Clear();
        foreach (var f in OrderColumnMapping.Fields)
        {
            var row = new MappingRow(f, _choices, StatusFor) { SourceText = ToText(map[f.Key]) };
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MappingRow.SourceText)) _dirty = true; };
            _rows.Add(row);
        }
        foreach (var r in _rows) r.RefreshStatus();
        _dirty = false;
        TxtError.Text = "";
    }

    private void RebuildChoices()
    {
        _choices.Clear();
        _choices.Add(NotImported);
        int last = 42;
        if (_sample != null)
        {
            foreach (var (_, h) in _sample.OrderBy(kv => kv.Key))
                if (h.Length > 0 && !_choices.Contains(h, StringComparer.OrdinalIgnoreCase)) _choices.Add(h);
            last = Math.Max(_sample.Keys.DefaultIfEmpty(0).Max(), 1);
        }
        for (int c = 1; c <= last; c++) _choices.Add(ColumnPrefix + OrderColumnMapping.ColumnLetter(c));
    }

    private static string ToText(ColumnSource s) =>
        !string.IsNullOrWhiteSpace(s.Header) ? s.Header!
        : !string.IsNullOrWhiteSpace(s.Column) ? ColumnPrefix + s.Column!.Trim().ToUpperInvariant()
        : NotImported;

    /// <summary>Null = valid; otherwise why the text can't be used.</summary>
    private static ColumnSource? FromText(string? text, out string? error)
    {
        error = null;
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Equals(NotImported, StringComparison.OrdinalIgnoreCase)) return ColumnSource.Skip();
        if (t.StartsWith(ColumnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var letter = t[ColumnPrefix.Length..].Trim().ToUpperInvariant();
            if (OrderColumnMapping.IsColumnLetter(letter)) return ColumnSource.FromColumn(letter);
            error = $"\"{t}\" isn't a valid column letter.";
            return null;
        }
        return ColumnSource.FromHeader(t);
    }

    /// <summary>"In sample file" text for a row — blank until a sample is loaded.</summary>
    private (string Text, Brush Brush) StatusFor(string? text)
    {
        var grey = Brushes.SlateGray;
        var src  = FromText(text, out var err);
        if (src == null) return ("✗ " + err, Brushes.Firebrick);
        if (src.IsSkipped) return ("", grey);
        if (_sample == null) return ("", grey);

        if (src.Column is { } letter)
        {
            var col = OrderColumnMapping.ColumnIndex(letter);
            return _sample.TryGetValue(col, out var h)
                ? ($"✓ column {letter}" + (h.Length > 0 ? $" — \"{h}\"" : " (no heading)"), Brushes.SeaGreen)
                : ($"✗ column {letter} is beyond the sample's last column", Brushes.Firebrick);
        }
        var key = OrderColumnMapping.NormalizeHeading(src.Header);
        var hit = _sample.Where(kv => OrderColumnMapping.NormalizeHeading(kv.Value) == key)
                         .Select(kv => (int?)kv.Key).FirstOrDefault();
        return hit is { } c
            ? ($"✓ found in column {OrderColumnMapping.ColumnLetter(c)}", Brushes.SeaGreen)
            : ("✗ heading not in the sample file", Brushes.Firebrick);
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private void CmbSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbSource.SelectedItem is not ComboBoxItem { Tag: string type } || type == _type) return;
        if (_dirty && MessageBox.Show(this, "Discard the unsaved changes to the " +
                $"{OrderColumnMapping.SourceLabel(_type)} mapping?", "Unsaved Changes",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            _loading = true;
            CmbSource.SelectedIndex = Array.IndexOf(OrderColumnMapping.SourceTypes, _type);
            _loading = false;
            return;
        }
        _type = type;
        LoadRows(OrderColumnMapping.Load(_type));
    }

    private void BtnLoadSample_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Pick a sample export to read its column headings",
            Filter = "Excel Files|*.xlsx;*.xlsm|All Files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _sample     = ExcelImportService.ReadHeadings(dlg.FileName);
            _sampleName = Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read that file:\n{ex.Message}", "Load Sample",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Keep what's typed, just offer the sample's headings and re-check every row.
        var texts = _rows.Select(r => r.SourceText).ToList();
        RebuildChoices();
        for (int i = 0; i < _rows.Count; i++) { _rows[i].SourceText = texts[i]; _rows[i].RefreshStatus(); }

        var missing = _rows.Count(r => r.Status.StartsWith("✗"));
        TxtSample.Text = $"Sample: {_sampleName} — {_sample.Count} columns. " +
                         (missing == 0 ? "Every mapped column was found." : $"{missing} mapped column(s) not found (✗).");

        if (MessageBox.Show(this,
                "Match fields to this file's headings automatically where the names are the same " +
                "(e.g. a \"Job Name\" heading → Job Name)?\n\nFields whose heading isn't in the file are left as they are.",
                "Match Headings", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            AutoMatch();
    }

    /// <summary>Switch each field to a heading of the sample whose name matches the field's label
    /// (or the default parser's comment names, e.g. "Job" for Job Name in Open Lines).</summary>
    private void AutoMatch()
    {
        if (_sample == null) return;
        var byHeading = _sample.Where(kv => kv.Value.Length > 0)
            .GroupBy(kv => OrderColumnMapping.NormalizeHeading(kv.Value))
            .ToDictionary(g => g.Key, g => g.First().Value);
        int matched = 0;
        foreach (var row in _rows)
        {
            foreach (var candidate in Synonyms(row.Field))
                if (byHeading.TryGetValue(OrderColumnMapping.NormalizeHeading(candidate), out var heading))
                {
                    if (!string.Equals(row.SourceText, heading, StringComparison.OrdinalIgnoreCase))
                    {
                        row.SourceText = heading;
                        matched++;
                    }
                    break;
                }
            row.RefreshStatus();
        }
        var missing = _rows.Count(r => r.Status.StartsWith("✗"));
        TxtSample.Text = $"Sample: {_sampleName} — {_sample.Count} columns. {matched} field(s) matched by heading. " +
                         (missing == 0 ? "Every mapped column was found." : $"{missing} mapped column(s) not found (✗) — fix or set to {NotImported}.");
    }

    private static IEnumerable<string> Synonyms(OrderImportField f)
    {
        var label = f.Label.Split("  (")[0];
        yield return label;
        yield return f.Key;
        var extra = f.Key switch
        {
            "JobName"       => new[] { "Job", "Job Name", "JobName" },
            "CustomerOrder" => new[] { "CO", "Customer Order", "Cust Order", "Order" },
            "OrdLine"       => new[] { "Line", "Order Line", "Ord Line", "CO Line" },
            "CustomerPO"    => new[] { "Customer Po", "Cust PO", "PO" },
            "CustPOLine"    => new[] { "Customer Po Line", "Cust PO Line", "PO Line" },
            "FaceSite"      => new[] { "Site", "Face Site" },
            "AssignWhse"    => new[] { "Assign Warehouse", "Assign Whse", "Warehouse" },
            "Slsman"        => new[] { "Slsman", "Salesperson", "Sales Person" },
            "ItemDescription" => new[] { "Description", "Item Description" },
            "QtyOrd"        => new[] { "Qty Ordered", "Qty Ord", "Quantity Ordered" },
            "QtyShip"       => new[] { "Qty Shipped", "Qty Ship" },
            "Price"         => new[] { "Line Price", "Price" },
            "ExtPrice"      => new[] { "Line Ext", "Ext Price", "Extended Price" },
            "TotalCOPrice"  => new[] { "Price 2", "Total CO Price" },
            "SalesTax1"     => new[] { "Sales Tax T", "Sales Tax" },
            "SalesTax2"     => new[] { "Sales Tax T2" },
            "CustNum"       => new[] { "Cust Num", "Customer Number", "Customer #" },
            "CustomerName"  => new[] { "Name", "Customer Name" },
            "ProjectManagerDataName" => new[] { "Project Manager", "PM", "PM Data Name" },
            "OrderStatus"   => new[] { "Status", "Order Status" },
            _               => Array.Empty<string>(),
        };
        foreach (var s in extra) yield return s;
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Reset the {OrderColumnMapping.SourceLabel(_type)} mapping to the built-in " +
                "column positions? (Takes effect when you click Save Mapping.)", "Reset to Defaults",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        LoadRows(OrderColumnMapping.Defaults(_type));
        _dirty = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var map = new Dictionary<string, ColumnSource>();
        foreach (var row in _rows)
        {
            var src = FromText(row.SourceText, out var err);
            if (src == null) { TxtError.Text = $"{row.Field.Label}: {err}"; return; }
            if (row.Required && src.IsSkipped)
            {
                TxtError.Text = $"{row.Field.Label.Split("  (")[0]} is required — it can't be \"{NotImported}\".";
                return;
            }
            map[row.Field.Key] = src;
        }

        var dupes = map.Where(kv => !kv.Value.IsSkipped)
            .GroupBy(kv => kv.Value.ToString(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).ToList();
        if (dupes.Count > 0 && MessageBox.Show(this,
                "Some columns fill more than one field:\n\n• " +
                string.Join("\n• ", dupes.Select(g => $"{g.Key} → " +
                    string.Join(", ", g.Select(kv => OrderColumnMapping.Field(kv.Key).Label.Split("  (")[0])))) +
                "\n\nSave anyway?", "Same Column Twice", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        OrderColumnMapping.Save(_type, map);
        AuditService.Log("Update", "OrderColumnMapping", details:
            $"{OrderColumnMapping.SourceLabel(_type)}: Job Name ← {map["JobName"]}, Customer Order ← {map["CustomerOrder"]}, " +
            $"Order Line ← {map["OrdLine"]}" + (_sampleName != null ? $" (sample: {_sampleName})" : ""));
        DialogResult = true;
    }

    // ── Row ───────────────────────────────────────────────────────────────────

    public partial class MappingRow : ObservableObject
    {
        private readonly Func<string?, (string, Brush)> _statusOf;

        public MappingRow(OrderImportField field, ObservableCollection<string> choices, Func<string?, (string, Brush)> status)
        {
            Field   = field;
            Choices = choices;
            _statusOf = status;
        }

        public OrderImportField Field { get; }
        public ObservableCollection<string> Choices { get; }
        public bool   Required     => Field.Required;
        public string DisplayLabel => Field.Required ? Field.Label + "  *" : Field.Label;

        [ObservableProperty] private string? _sourceText;
        [ObservableProperty] private string  _status = "";
        [ObservableProperty] private Brush   _statusBrush = Brushes.SlateGray;

        partial void OnSourceTextChanged(string? value) => RefreshStatus();

        public void RefreshStatus() => (Status, StatusBrush) = _statusOf(SourceText);
    }
}
