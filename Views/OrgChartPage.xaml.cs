using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class OrgChartPage : UserControl
{
    private OrgChartViewModel? Vm => DataContext as OrgChartViewModel;

    public OrgChartPage() => InitializeComponent();

    private void PmCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && sender is FrameworkElement { Tag: int pmId }) vm.OpenPm?.Invoke(pmId);
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
