using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PMDashboard.Data.Models;
using PMDashboard.Dialogs;
using PMDashboard.Services;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class CustomDataPage : UserControl
{
    private static readonly CustomCellValueConverter CellConverter = new();

    // Editable (user-added) columns are marked with a coloured header and a border down both
    // sides of the column. A cell background does not work here: the grid already alternates
    // row colours, so a tint reads as ordinary banding rather than "you can type here".
    private static readonly Brush EditableAccentBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly Brush EditableOnAccent    = Brushes.White;

    private static readonly Brush FilterActiveBrush   = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush FilterInactiveBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    /// <summary>Header glyphs by column key, so an applied filter can be reflected without rebuilding columns.</summary>
    private readonly Dictionary<string, TextBlock> _filterGlyphs = new();

    /// <summary>Display names by column key, for the popup title.</summary>
    private readonly Dictionary<string, string> _filterTitles = new();

    /// <summary>Keys of user-added columns, so their glyphs stay legible on the accent header.</summary>
    private readonly HashSet<string> _editableKeys = new();

    /// <summary>Column key for each built grid column, so a drag can be written back as an order.</summary>
    private readonly Dictionary<DataGridColumn, string> _columnKeys = new();

    /// <summary>Suppresses order-saving while columns are being built.</summary>
    private bool _rebuilding;

    /// <summary>Values shown in the open popup, and the column they belong to.</summary>
    private readonly ObservableCollection<FilterValueItem> _filterItems = new();
    private string? _activeFilterKey;

    private CustomDataViewModel? Vm => DataContext as CustomDataViewModel;

    public CustomDataPage()
    {
        InitializeComponent();
        Loaded += CustomDataPage_Loaded;
        DataContextChanged += CustomDataPage_DataContextChanged;
    }

    private void CustomDataPage_Loaded(object sender, RoutedEventArgs e) => RebuildColumns();

    // Rebuild grid columns whenever the VM's static field list or dynamic column list changes —
    // covers mode switches, filter reruns, and anything done through ManageColumnsDialog (same
    // VM instance), without every mutation site needing to remember to call the rebuild itself.
    private void CustomDataPage_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CustomDataViewModel oldVm) oldVm.PropertyChanged -= Vm_PropertyChanged;
        if (e.NewValue is CustomDataViewModel newVm) newVm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CustomDataViewModel.StaticFields) or nameof(CustomDataViewModel.DynamicColumns))
            RebuildColumns();
    }

    private void RebuildColumns()
    {
        if (Vm is not { } vm) return;

        _rebuilding = true;
        try { RebuildColumnsCore(vm); }
        finally { _rebuilding = false; }
    }

    private void RebuildColumnsCore(CustomDataViewModel vm)
    {
        DataGridCustom.Columns.Clear();
        _filterGlyphs.Clear();
        _filterTitles.Clear();
        _editableKeys.Clear();
        _columnKeys.Clear();
        FilterPopup.IsOpen = false;

        // Built in default order first (statics in catalog order, then dynamics by SortOrder),
        // then re-sequenced below by whatever order the user last saved.
        var built = new List<(string Key, DataGridColumn Column)>();
        void Add(string key, DataGridColumn column)
        {
            _columnKeys[column] = key;
            built.Add((key, column));
        }

        // Static columns — read-only, reflect real Order/ROMP data already in the database.
        foreach (var field in vm.StaticFields)
        {
            var binding = new Binding($"StaticValues[{field.Key}]") { Mode = BindingMode.OneWay };
            if (field.FieldType == ReportFieldType.Date)
                binding.StringFormat = "yyyy-MM-dd";
            Add(field.Key, new DataGridTextColumn
            {
                Header       = BuildFilterHeader(field.Key, field.DisplayName),
                Binding      = binding,
                IsReadOnly   = true,
                // Sorting stays off: these cells bind through a dictionary indexer, which is
                // not a path ListCollectionView can build a SortDescription from.
                CanUserSort  = false,
            });
        }

        // Dynamic columns — editable, user-defined.
        foreach (var col in vm.DynamicColumns)
        {
            switch (col.ColumnType)
            {
                case "Bool":
                    Add(col.Key, new DataGridCheckBoxColumn
                    {
                        Header      = BuildFilterHeader(col.Key, col.Name),
                        CanUserSort = false,
                        Binding = new Binding($"DynamicValues[{col.Key}]")
                        {
                            Mode = BindingMode.TwoWay,
                            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
                            Converter = CellConverter,
                            ConverterParameter = col.ColumnType,
                        },
                    });
                    break;

                case "Date":
                    Add(col.Key, BuildDateColumn(col));
                    break;

                default: // Text, Number
                    Add(col.Key, new DataGridTextColumn
                    {
                        Header      = BuildFilterHeader(col.Key, col.Name),
                        CanUserSort = false,
                        Binding = new Binding($"DynamicValues[{col.Key}]")
                        {
                            Mode = BindingMode.TwoWay,
                            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                            Converter = CellConverter,
                            ConverterParameter = col.ColumnType,
                        },
                    });
                    break;
            }
        }

        // Tint the editable half and give it a History link.
        foreach (var (key, column) in built)
            if (vm.DynamicColumns.Any(c => c.Key == key))
            {
                _editableKeys.Add(key);
                ApplyEditableStyling(column);
                UpdateFilterGlyph(key);   // re-colour for the accent header
            }

        Add(HistoryColumnKey, BuildHistoryColumn());

        // Re-sequence by the saved order. Keys absent from it (a newly added column, or a
        // first run before anything was dragged) keep their default relative position at the
        // end, because OrderBy is stable.
        var saved = vm.GetSavedColumnOrder();
        var rank  = new Dictionary<string, int>();
        for (int i = 0; i < saved.Count; i++) rank.TryAdd(saved[i], i);

        foreach (var (_, column) in built.OrderBy(b => rank.TryGetValue(b.Key, out var r) ? r : int.MaxValue))
            DataGridCustom.Columns.Add(column);
    }

    /// <summary>Persists the new left-to-right order after the user drags a column header.</summary>
    private void DataGridCustom_ColumnReordered(object sender, DataGridColumnEventArgs e)
    {
        if (_rebuilding || Vm is not { } vm) return;

        var keys = DataGridCustom.Columns
            .OrderBy(c => c.DisplayIndex)
            .Select(c => _columnKeys.TryGetValue(c, out var k) ? k : null)
            .OfType<string>()
            .ToList();

        if (keys.Count > 0) vm.SaveColumnOrder(keys);
    }

    private DataGridTemplateColumn BuildDateColumn(CustomColumn col)
    {
        string bindingPath = $"DynamicValues[{col.Key}]";

        var cellTemplate = new DataTemplate();
        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new Binding(bindingPath)
        {
            Converter = CellConverter, ConverterParameter = "Date", StringFormat = "{0:yyyy-MM-dd}",
        });
        textFactory.SetValue(TextBlock.MarginProperty, new Thickness(12, 0, 0, 0));
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        cellTemplate.VisualTree = textFactory;

        var editTemplate = new DataTemplate();
        var dateFactory = new FrameworkElementFactory(typeof(DatePicker));
        dateFactory.SetBinding(DatePicker.SelectedDateProperty, new Binding(bindingPath)
        {
            Mode = BindingMode.TwoWay, Converter = CellConverter, ConverterParameter = "Date",
        });
        dateFactory.SetValue(DatePicker.BorderThicknessProperty, new Thickness(0));
        editTemplate.VisualTree = dateFactory;

        return new DataGridTemplateColumn
        {
            Header              = BuildFilterHeader(col.Key, col.Name),
            CanUserSort         = false,
            CellTemplate        = cellTemplate,
            CellEditingTemplate = editTemplate,
        };
    }

    // ── Column header filters ────────────────────────────────────────────────

    /// <summary>
    /// Builds a header carrying a filter glyph on its right-hand edge.
    ///
    /// Only the glyph is a Button. Making the whole header a button swallows the mouse-down
    /// that DataGridColumnHeader needs to start its drag-to-reorder gesture, which silently
    /// breaks column reordering — so the label stays plain, non-interactive content that the
    /// DataGrid can drag as usual. The glyph doubles as the "this column is filtered" indicator.
    /// </summary>
    private FrameworkElement BuildFilterHeader(string key, string displayName)
    {
        _filterTitles[key] = displayName;

        var label = new TextBlock
        {
            Text              = displayName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };

        var glyph = new TextBlock
        {
            Text       = "▾",
            FontSize   = 10,
            Foreground = FilterInactiveBrush,
        };
        _filterGlyphs[key] = glyph;

        var glyphButton = new Button
        {
            Content           = glyph,
            Tag               = key,
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            Padding           = new Thickness(5, 0, 5, 0),
            Margin            = new Thickness(4, 0, 0, 0),
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = $"Filter by {displayName}",
        };
        glyphButton.Click += FilterHeader_Click;

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(label);
        content.Children.Add(glyphButton);

        UpdateFilterGlyph(key);
        return content;
    }

    private const string HistoryColumnKey = "__history";

    /// <summary>
    /// Marks a user-added column: accent-coloured header, plus a border down the left and
    /// right edge of every cell so the column reads as one bracketed band.
    ///
    /// Cell backgrounds are deliberately left alone - the grid alternates row colours, so a
    /// tint is indistinguishable from normal banding.
    /// </summary>
    private void ApplyEditableStyling(DataGridColumn column)
    {
        // Uses the dedicated XAML style rather than adding setters to DataGridCellStyle: that
        // style's ControlTemplate binds only Background and Padding, so border setters on it
        // are silently ignored - which is exactly why the first attempt drew nothing.
        column.CellStyle = TryFindResource("DataGridEditableCellStyle") as Style;

        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader),
                                    TryFindResource("DataGridColumnHeaderStyle") as Style);
        void Set(DependencyProperty p, object v) =>
            headerStyle.Setters.Add(new Setter(p, v));
        Set(System.Windows.Controls.Primitives.DataGridColumnHeader.BackgroundProperty,  EditableAccentBrush);
        Set(System.Windows.Controls.Primitives.DataGridColumnHeader.ForegroundProperty,  EditableOnAccent);
        Set(System.Windows.Controls.Primitives.DataGridColumnHeader.FontWeightProperty,  FontWeights.SemiBold);
        Set(System.Windows.Controls.Primitives.DataGridColumnHeader.BorderBrushProperty, EditableAccentBrush);
        Set(System.Windows.Controls.Primitives.DataGridColumnHeader.BorderThicknessProperty, new Thickness(2, 2, 2, 0));
        column.HeaderStyle = headerStyle;
    }

    /// <summary>
    /// Trailing link to everything recorded against this row. Not filterable and not part of
    /// the saved column order, since it is navigation rather than data.
    /// </summary>
    private DataGridTemplateColumn BuildHistoryColumn()
    {
        var template = new DataTemplate();
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(ContentControl.ContentProperty, "History");
        button.SetValue(Button.CursorProperty, Cursors.Hand);
        button.SetValue(Button.BackgroundProperty, Brushes.Transparent);
        button.SetValue(Button.BorderThicknessProperty, new Thickness(0));
        button.SetValue(Button.ForegroundProperty, FilterActiveBrush);
        button.SetValue(Button.FontSizeProperty, 12.0);
        button.SetValue(Button.PaddingProperty, new Thickness(8, 0, 8, 0));
        button.SetValue(Button.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        button.AddHandler(Button.ClickEvent, new RoutedEventHandler(HistoryLink_Click));
        template.VisualTree = button;

        return new DataGridTemplateColumn
        {
            Header       = "History",
            Width        = 90,
            CanUserSort  = false,
            IsReadOnly   = true,
            CellTemplate = template,
        };
    }

    private void HistoryLink_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Button { DataContext: CustomDataRow row }) return;

        var entityType = vm.Mode == ReportMode.Orders ? "Order" : "Romp";
        var label = row.StaticValues.GetValueOrDefault("JobName")?.ToString()
                 ?? row.StaticValues.GetValueOrDefault("RompName")?.ToString()
                 ?? $"{entityType} #{row.EntityId}";

        new RowHistoryDialog(entityType, row.EntityId, label) { Owner = Window.GetWindow(this) }
            .ShowDialog();
    }

    private void UpdateFilterGlyph(string key)
    {
        if (Vm is not { } vm) return;
        if (!_filterGlyphs.TryGetValue(key, out var glyph)) return;

        var active = vm.IsColumnFiltered(key);
        glyph.Text       = active ? "▼" : "▾";
        glyph.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;

        // On an accent header the normal slate/blue glyphs disappear, so use white there.
        glyph.Foreground = _editableKeys.Contains(key)
            ? EditableOnAccent
            : active ? FilterActiveBrush : FilterInactiveBrush;
    }

    private void RefreshAllFilterGlyphs()
    {
        foreach (var key in _filterGlyphs.Keys.ToList()) UpdateFilterGlyph(key);
    }

    private void FilterHeader_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Button btn || btn.Tag is not string key) return;

        _activeFilterKey = key;

        _filterItems.Clear();
        foreach (var item in vm.GetFilterValues(key)) _filterItems.Add(item);

        LstFilterValues.ItemsSource = _filterItems;
        TxtFilterColumn.Text        = _filterTitles.TryGetValue(key, out var title) ? title : "Filter";
        TxtFilterSearch.Text        = "";
        // The view is shared across openings, so a search left over from the previously
        // opened column would otherwise still be hiding values here.
        CollectionViewSource.GetDefaultView(_filterItems).Filter = null;

        var truncated = vm.FilterValuesTruncated(key);
        TxtFilterTruncated.Text = truncated
            ? $"Showing the first {CustomDataViewModel.MaxFilterValues} values — narrow the date range for the rest."
            : "";
        TxtFilterTruncated.Visibility = truncated ? Visibility.Visible : Visibility.Collapsed;

        FilterPopup.PlacementTarget = btn;
        FilterPopup.IsOpen          = true;
    }

    private void TxtFilterSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LstFilterValues.ItemsSource == null) return;
        var text = TxtFilterSearch.Text;
        var view = CollectionViewSource.GetDefaultView(LstFilterValues.ItemsSource);
        view.Filter = string.IsNullOrWhiteSpace(text)
            ? null
            : o => o is FilterValueItem i &&
                   i.Display.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    // Select all / none act on what the search box currently shows, so they stay predictable
    // when the list is narrowed.
    private void BtnFilterSelectAll_Click(object sender, RoutedEventArgs e)  => SetVisibleChecked(true);
    private void BtnFilterSelectNone_Click(object sender, RoutedEventArgs e) => SetVisibleChecked(false);

    private void SetVisibleChecked(bool value)
    {
        if (LstFilterValues.ItemsSource == null) return;
        foreach (var obj in CollectionViewSource.GetDefaultView(LstFilterValues.ItemsSource))
            if (obj is FilterValueItem item) item.IsChecked = value;
    }

    private void BtnFilterApply_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _activeFilterKey is not { } key) return;

        vm.SetColumnFilter(key, _filterItems.Where(i => i.IsChecked).Select(i => i.Display));
        UpdateFilterGlyph(key);
        FilterPopup.IsOpen = false;
    }

    private void BtnFilterRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || _activeFilterKey is not { } key) return;

        vm.ClearColumnFilter(key);
        UpdateFilterGlyph(key);
        FilterPopup.IsOpen = false;
    }

    private void BtnClearColumnFilters_Click(object sender, RoutedEventArgs e)
    {
        Vm?.ClearAllFilters();
        RefreshAllFilterGlyphs();
    }

    // ── Row lifecycle — only dynamic columns are ever actually editable ────────

    private void DataGridCustom_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not CustomDataRow row) return;
        vm.SaveRow(row);
    }

    // ── Mode + filter ────────────────────────────────────────────────────────

    private void RbModeOrders_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Mode = ReportMode.Orders;
    }

    private void RbModeSitesRomps_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.Mode = ReportMode.SitesRomps;
    }

    private void BtnApplyFilter_Click(object sender, RoutedEventArgs e) => Vm?.ApplyFilter();

    // ── Paging ───────────────────────────────────────────────────────────────

    private void BtnFirstPage_Click(object sender, RoutedEventArgs e) => Vm?.FirstPage();
    private void BtnPrevPage_Click(object sender, RoutedEventArgs e)  => Vm?.PrevPage();
    private void BtnNextPage_Click(object sender, RoutedEventArgs e)  => Vm?.NextPage();
    private void BtnLastPage_Click(object sender, RoutedEventArgs e)  => Vm?.LastPage();

    // ── Setup screen ─────────────────────────────────────────────────────────

    private void BtnManageColumns_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new ManageColumnsDialog(vm) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }
}
