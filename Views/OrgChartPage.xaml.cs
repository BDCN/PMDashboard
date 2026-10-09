using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PMDashboard.Dialogs;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class OrgChartPage : UserControl
{
    private OrgChartViewModel? Vm => DataContext as OrgChartViewModel;

    public OrgChartPage()
    {
        InitializeComponent();
        // Leaving the page with unsaved edits: ask rather than silently drop them.
        Unloaded += (_, _) =>
        {
            if (Vm is not { IsEditMode: true } vm || !vm.HasChanges) { if (Vm is { IsEditMode: true } v) v.CancelEdit(); return; }
            var save = MessageBox.Show("Save your Org Chart changes?\n\n" + vm.ChangeSummary,
                "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (save) vm.SaveEdits(); else vm.CancelEdit();
        };
    }

    private void PmCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (_dragged) { _dragged = false; return; }
        if (Vm is { IsEditMode: false } vm && sender is FrameworkElement { Tag: OrgChartPm pm }) vm.OpenPm?.Invoke(pm.PmId);
    }

    // ── Edit mode ─────────────────────────────────────────────────────────────

    private void BtnBeginEdit_Click(object sender, RoutedEventArgs e) => Vm?.BeginEdit();

    private void BtnCancelEdit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.HasChanges && MessageBox.Show("Discard your Org Chart changes?", "Cancel Editing",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        vm.CancelEdit();
    }

    private void BtnSaveEdit_Click(object sender, RoutedEventArgs e) => Vm?.SaveEdits();

    private void BtnMakeDefault_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { Tag: OrgChartPm { SubRegionId: { } sub } pm }) vm.MakeDefault(pm.PmId, sub);
    }

    private void BtnRemoveSub_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { Tag: OrgChartPm { SubRegionId: { } sub } pm }) vm.RemoveFromSubRegion(pm.PmId, sub);
    }

    // ── Drag and drop: PM cards → sub-region columns; PA chips → PM cards ──

    private Point _dragStart;
    private object? _dragItem;     // OrgChartPm or OrgChartPa under the mouse at press time
    private bool _dragged;         // suppresses the click that ends a drag

    private void PmCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = false;
        if (Vm is not { IsEditMode: true }) { _dragItem = null; return; }
        _dragStart = e.GetPosition(this);
        // A PA chip inside the card wins over the card itself.
        _dragItem = FindTag<OrgChartPa>(e.OriginalSource as DependencyObject) as object
                    ?? (sender as FrameworkElement)?.Tag as OrgChartPm;
    }

    private void PmCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed || Vm is not { IsEditMode: true }) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        _dragged = true;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(item.GetType(), item), DragDropEffects.Move | DragDropEffects.Copy);
    }

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (sender is Border b && e.Data.GetData(typeof(OrgChartPm)) is OrgChartPm)
        {
            e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
            b.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE7, 0xFF));
        }
        e.Handled = true;
    }

    private void Column_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.Background = Brushes.Transparent;
    }

    private void Column_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.Background = Brushes.Transparent;
        if (Vm is not { } vm || sender is not FrameworkElement { Tag: OrgChartColumn col }) return;
        if (e.Data.GetData(typeof(OrgChartPm)) is not OrgChartPm pm) return;
        bool add = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        vm.MovePm(pm.PmId, pm.SubRegionId, col.IsNoTeam ? null : col.SubRegionId, add && !col.IsNoTeam);
        e.Handled = true;
    }

    private void PmCard_DragOver(object sender, DragEventArgs e)
    {
        // Only PAs drop onto a PM card; a dragged PM falls through to the column.
        if (e.Data.GetData(typeof(OrgChartPa)) is not OrgChartPa) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void PmCard_Drop(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm || e.Data.GetData(typeof(OrgChartPa)) is not OrgChartPa pa) return;
        if (sender is FrameworkElement { Tag: OrgChartPm pm }) vm.MovePa(pa.Id, pm.PmId);
        e.Handled = true;
    }

    // ── TBD placeholders ──

    private bool TbdReady(OrgChartViewModel vm)
    {
        if (vm.TbdBlockedReason is not { Length: > 0 } why) return true;
        MessageBox.Show(why, "TBD", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void BtnAddTbdPm_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { Tag: OrgChartColumn { SubRegionId: { } sub } } && TbdReady(vm)) vm.AddTbdPm(sub);
    }

    private void BtnAddTbdPa_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { Tag: OrgChartPm pm } && TbdReady(vm)) vm.AddTbdPa(pm.PmId);
    }

    private void BtnFillPm_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not FrameworkElement { Tag: OrgChartPm pm } || !TbdReady(vm)) return;
        var dlg = new AddEditPmDialog(null, new PmMaintenanceViewModel().GetAllPms()) { Owner = Window.GetWindow(this), Title = $"Fill {pm.Name}" };
        if (dlg.ShowDialog() == true && dlg.Result is { } hired) vm.FillPm(pm.PmId, hired);
    }

    private void BtnFillPa_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not FrameworkElement { Tag: OrgChartPa pa } || !TbdReady(vm)) return;
        var teams = new TeamsViewModel();
        var dlg = new TeamMemberDialog(Data.Models.TeamRoles.Pa, null, TeamsViewModel.DefaultRegions, teams.AllSubRegions(), teams.ActivePms())
            { Owner = Window.GetWindow(this), Title = "Fill TBD PA" };
        if (dlg.ShowDialog() == true && dlg.Result is { } hired) vm.FillPa(pa.Id, hired);
    }

    private void BtnRemoveTbd_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var (pmId, paId) = sender is FrameworkElement { Tag: OrgChartPm pm } ? ((int?)pm.PmId, (int?)null)
                         : sender is FrameworkElement { Tag: OrgChartPa pa } ? (null, pa.Id) : (null, null);
        if (vm.RemoveTbd(pmId, paId) is { Length: > 0 } why)
            MessageBox.Show(why, "Remove TBD", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Whole-chart drop handling: auto-scroll + "nearest column" fallback ──

    // Fires (tunnelling) for every drag over the chart, even over a column — scroll when near an edge.
    private void ChartScroll_PreviewDragOver(object sender, DragEventArgs e)
    {
        const double edge = 48, step = 24;
        var p = e.GetPosition(ChartScroll);
        if (p.X < edge) ChartScroll.ScrollToHorizontalOffset(ChartScroll.HorizontalOffset - step);
        else if (p.X > ChartScroll.ViewportWidth - edge) ChartScroll.ScrollToHorizontalOffset(ChartScroll.HorizontalOffset + step);
        if (p.Y < edge) ChartScroll.ScrollToVerticalOffset(ChartScroll.VerticalOffset - step);
        else if (p.Y > ChartScroll.ViewportHeight - edge) ChartScroll.ScrollToVerticalOffset(ChartScroll.VerticalOffset + step);
    }

    // Reached only when no column handled it (pointer over a heading, Director card, gap…).
    private Border? _fallbackHighlight;
    private void ChartScroll_DragOver(object sender, DragEventArgs e)
    {
        var col = e.Data.GetData(typeof(OrgChartPm)) is OrgChartPm ? ColumnNear(e.GetPosition(ChartRoot)) : null;
        SetFallbackHighlight(col);
        e.Effects = col == null ? DragDropEffects.None
                  : (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private void ChartScroll_DragLeave(object sender, DragEventArgs e) => SetFallbackHighlight(null);

    private void ChartScroll_Drop(object sender, DragEventArgs e)
    {
        SetFallbackHighlight(null);
        if (Vm is not { } vm || e.Data.GetData(typeof(OrgChartPm)) is not OrgChartPm pm) return;
        if (ColumnNear(e.GetPosition(ChartRoot)) is not { Tag: OrgChartColumn col }) return;
        bool add = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        vm.MovePm(pm.PmId, pm.SubRegionId, col.IsNoTeam ? null : col.SubRegionId, add && !col.IsNoTeam);
        e.Handled = true;
    }

    private void SetFallbackHighlight(Border? col)
    {
        if (_fallbackHighlight == col) return;
        if (_fallbackHighlight != null) _fallbackHighlight.Background = Brushes.Transparent;
        _fallbackHighlight = col;
        if (col != null) col.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE7, 0xFF));
    }

    /// <summary>The column whose horizontal span contains the point (within its region's height,
    /// plus some slack above for the region heading / Director card).</summary>
    private Border? ColumnNear(Point p)
    {
        Border? best = null;
        double bestDy = double.MaxValue;
        foreach (var b in Descendants<Border>(ChartRoot).Where(b => b.Tag is OrgChartColumn))
        {
            var r = b.TransformToAncestor(ChartRoot).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
            if (p.X < r.Left || p.X > r.Right) continue;
            double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
            if (dy < bestDy && dy <= 160) { best = b; bestDy = dy; }
        }
        return best;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    // ── "Move to…" — the same moves without dragging ──

    private void BtnMoveTo_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Button { Tag: OrgChartPm pm } btn) return;
        var menu = new ContextMenu { PlacementTarget = btn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var move = new MenuItem { Header = "Move to (makes it their default)" };
        var also = new MenuItem { Header = "Also cover (add)" };
        foreach (var (id, name, region) in vm.SubRegionChoices())
        {
            var label = $"{region} › {name}";
            var m = new MenuItem { Header = label, IsEnabled = id != pm.SubRegionId };
            m.Click += (_, _) => vm.MovePm(pm.PmId, pm.SubRegionId, id, add: false);
            move.Items.Add(m);
            var a = new MenuItem { Header = label };
            a.Click += (_, _) => vm.MovePm(pm.PmId, pm.SubRegionId, id, add: true);
            also.Items.Add(a);
        }
        var none = new MenuItem { Header = "No team (take off every team)" };
        none.Click += (_, _) => vm.MovePm(pm.PmId, pm.SubRegionId, null, add: false);
        menu.Items.Add(move);
        menu.Items.Add(also);
        menu.Items.Add(new Separator());
        menu.Items.Add(none);
        menu.IsOpen = true;
    }

    private static T? FindTag<T>(DependencyObject? d) where T : class
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: T t }) return t;
        return null;
    }

    private void BtnEditTeams_Click(object sender, RoutedEventArgs e) => Vm?.OpenTeams?.Invoke();

    // Ctrl + wheel zooms; plain wheel scrolls as usual.
    private void ChartScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is not { } vm || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        vm.Zoom = Math.Clamp(Math.Round(vm.Zoom + (e.Delta > 0 ? 0.1 : -0.1), 2), 0.3, 1.5);
        e.Handled = true;
    }

    private void BtnFit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        // Unscaled width = current width ÷ current zoom.
        var natural = ChartRoot.ActualWidth;
        if (natural <= 0) return;
        var available = ChartScroll.ViewportWidth - 8;
        vm.Zoom = Math.Clamp(Math.Round(available / natural, 2), 0.3, 1.5);
    }

    /// <summary>Saves the whole chart (not just the visible part) as a PNG, at full size regardless of zoom.</summary>
    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Org Chart", FileName = $"Org_Chart_{DateTime.Now:yyyy-MM-dd}", DefaultExt = ".png",
            Filter = "PNG image|*.png",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ChartRoot.UpdateLayout();
            var w = ChartRoot.ActualWidth;   // layout size before the zoom transform
            var h = ChartRoot.ActualHeight;
            if (w <= 0 || h <= 0) return;
            // 1.5× for sharper text, capped so a very large chart stays a sane file size.
            var scale = Math.Min(1.5, 9000 / Math.Max(w, h));
            // The brush draws ChartRoot as it appears on screen — i.e. already scaled by the zoom
            // (its LayoutTransform) — so undo the zoom here to export at full size.
            var zoom = Vm?.Zoom is > 0 and var z ? z : 1.0;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale / zoom, scale / zoom));
                var area = new Rect(0, 0, w * zoom, h * zoom);
                dc.DrawRectangle(Brushes.White, null, area);
                dc.DrawRectangle(new VisualBrush(ChartRoot) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                                 null, area);
            }
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(dlg.FileName);
            enc.Save(fs);
            MessageBox.Show($"Org chart saved to:\n{dlg.FileName}", "Exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not export the chart:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
