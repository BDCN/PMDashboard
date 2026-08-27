using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PMDashboard.ViewModels;

namespace PMDashboard.Views;

public partial class WorkloadPage : UserControl
{
    // Width of the PM-name column — must match the DataGrid row template
    private const double NameColumnWidth = 150;

    private WorkloadViewModel? Vm => DataContext as WorkloadViewModel;

    public WorkloadPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is WorkloadViewModel vm)
            RebuildBackground(vm);
    }

    // ── Auto-scale: fires whenever the chart panel gets a new size ──
    private void ChartGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var barWidth = e.NewSize.Width - NameColumnWidth;
        if (barWidth < 50) return;          // too small to do anything useful

        vm.ApplyScale(barWidth);            // recomputes bar widths + tick positions
        RebuildBackground(vm);              // redraws grid lines at new positions
    }

    // ── Row click → PM Detail ──

    private void RowGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is PmWorkloadRow row)
        {
            var mainWin = Window.GetWindow(this);
            if (mainWin?.DataContext is MainViewModel mainVm)
                mainVm.NavigateToPmDetail(row.PmId);
        }
    }

    // ── Background grid lines ──

    private void RebuildBackground(WorkloadViewModel vm)
    {
        BackgroundCanvas.Children.Clear();

        // Minor lines (every 10%) — clearly visible light gray
        var minorBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
        // Major lines (every 50%) — slightly darker and thicker
        var majorBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

        foreach (var tick in vm.Ticks)
        {
            var line = new Rectangle
            {
                Width            = tick.IsMajor ? 1.5 : 1,
                Height           = 10_000,
                Fill             = tick.IsMajor ? majorBrush : minorBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(line, tick.X);
            BackgroundCanvas.Children.Add(line);
        }

        // 100% capacity reference line — visible soft red
        var refLine = new Rectangle
        {
            Width            = 2,
            Height           = 10_000,
            Fill             = new SolidColorBrush(Color.FromArgb(0xC0, 0xF8, 0x71, 0x71)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(refLine, vm.HundredPctX);
        BackgroundCanvas.Children.Add(refLine);
    }

}
