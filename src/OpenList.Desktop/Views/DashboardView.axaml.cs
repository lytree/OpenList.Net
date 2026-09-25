using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using OpenList.Desktop.ViewModels;
using AvPoint = Avalonia.Point;

namespace OpenList.Desktop.Views;

/// <summary>
/// 主页视图。心跳图在上游是 SVG（20px 网格 + 折线 + 采样点圆），这里用 Canvas 等效绘制；
/// 进程监控（CPU / 内存曲线）同样以 Canvas 绘制。
/// </summary>
public partial class DashboardView : UserControl
{
    private const double GridStep = 20;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookVm();
        // 主题切换（浅色 ↔ 深色）时用新主题配色重绘图表
        ActualThemeVariantChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(RedrawAll);
    }

    /// <summary>从当前主题变体解析主题相关画刷（图表配色随主题切换）。</summary>
    private SolidColorBrush ThemeBrush(string key, string fallback)
    {
        if (Application.Current?.TryGetResource(key, ActualThemeVariant, out var value) is true &&
            value is SolidColorBrush brush)
            return brush;
        return new SolidColorBrush(Color.Parse(fallback));
    }

    private DashboardViewModel? Vm => DataContext as DashboardViewModel;

    private void HookVm()
    {
        if (Vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.Monitor.PropertyChanged += OnMonitorPropertyChanged;
        }
        RedrawAll();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.HeartbeatPoints))
            Avalonia.Threading.Dispatcher.UIThread.Post(RedrawHeartbeat);
    }

    private void OnMonitorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CoreMonitorViewModel.CpuPoints) or nameof(CoreMonitorViewModel.MemPoints))
            Avalonia.Threading.Dispatcher.UIThread.Post(RedrawMonitor);
    }

    private void OnChartSizeChanged(object? sender, SizeChangedEventArgs e) => RedrawAll();

    private void RedrawAll()
    {
        RedrawHeartbeat();
        RedrawMonitor();
    }

    private void RedrawHeartbeat()
    {
        var canvas = this.FindControl<Canvas>("ChartCanvas");
        var points = Vm?.HeartbeatPoints;
        if (canvas is null) return;
        canvas.Children.Clear();

        var w = canvas.Bounds.Width;
        var h = canvas.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        DrawGrid(canvas, w, h);

        if (points is null || points.Count == 0) return;

        var maxMs = Math.Max(50, points.Max(p => p.Ms) * 1.2);
        var stepX = points.Count > 1 ? (w - 10) / (points.Count - 1) : 0;

        // 折线
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            AvPoint Map(HeartbeatPoint p) => new(5 + p.Index * stepX, h - 8 - p.Ms / maxMs * (h - 16));
            ctx.BeginFigure(Map(points[0]), isFilled: false);
            for (var i = 1; i < points.Count; i++)
                ctx.LineTo(Map(points[i]));
            ctx.EndFigure(false);
        }
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Stroke = ThemeBrush("Brush.Accent", "#007AFF"),
            StrokeThickness = 2,
            StrokeJoin = PenLineJoin.Round,
        });

        // 采样点
        foreach (var p in points)
        {
            var cx = 5 + p.Index * stepX;
            var cy = h - 8 - p.Ms / maxMs * (h - 16);
            var dot = new Ellipse
            {
                Width = p.Healthy ? 5 : 6.5,
                Height = p.Healthy ? 5 : 6.5,
                Fill = p.Healthy
                    ? ThemeBrush("Brush.Accent", "#007AFF")
                    : ThemeBrush("Brush.Danger", "#EF4444"),
            };
            Canvas.SetLeft(dot, cx - 2.5);
            Canvas.SetTop(dot, cy - 2.5);
            ToolTip.SetTip(dot, $"{p.Time} · {p.Ms} ms · {(p.Healthy ? "健康" : "不健康")}");
            canvas.Children.Add(dot);
        }
    }

    private void RedrawMonitor()
    {
        var vm = Vm?.Monitor;
        if (vm is null) return;
        DrawMetricSeries(this.FindControl<Canvas>("CpuCanvas"), vm.CpuPoints,
            ThemeBrush("Brush.Accent", "#007AFF"), min => Math.Max(10, min));
        DrawMetricSeries(this.FindControl<Canvas>("MemCanvas"), vm.MemPoints,
            ThemeBrush("Brush.Indigo", "#5856D6"), min => Math.Max(1, min));
    }

    /// <summary>绘制一条监控曲线（20px 网格 + 折线 + 末点标记，带 ToolTip）。</summary>
    private void DrawMetricSeries(Canvas? canvas, IReadOnlyList<MetricPoint> points,
        SolidColorBrush lineBrush, Func<double, double> ensureMin)
    {
        if (canvas is null) return;
        canvas.Children.Clear();

        var w = canvas.Bounds.Width;
        var h = canvas.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        DrawGrid(canvas, w, h);
        if (points.Count == 0) return;

        var max = ensureMin(points.Max(p => p.Value) * 1.2);
        var stepX = points.Count > 1 ? (w - 10) / (points.Count - 1) : 0;
        AvPoint Map(MetricPoint p) => new(5 + p.Index * stepX, h - 8 - p.Value / max * (h - 16));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Map(points[0]), isFilled: false);
            for (var i = 1; i < points.Count; i++)
                ctx.LineTo(Map(points[i]));
            ctx.EndFigure(false);
        }
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Stroke = lineBrush,
            StrokeThickness = 2,
            StrokeJoin = PenLineJoin.Round,
        });

        // 末点标记
        var last = points[^1];
        var dot = new Ellipse { Width = 6, Height = 6, Fill = lineBrush };
        var pos = Map(last);
        Canvas.SetLeft(dot, pos.X - 3);
        Canvas.SetTop(dot, pos.Y - 3);
        ToolTip.SetTip(dot, $"{last.Time} · {last.Value:F1}");
        canvas.Children.Add(dot);
    }

    /// <summary>20px 网格（上游 &lt;pattern id="grid"&gt;）。</summary>
    private void DrawGrid(Canvas canvas, double w, double h)
    {
        var gridBrush = ThemeBrush("Brush.ChartGrid", "#1A007AFF");
        for (var x = GridStep; x < w; x += GridStep)
            canvas.Children.Add(new Line { StartPoint = new(x, 0), EndPoint = new(x, h), Stroke = gridBrush, StrokeThickness = 0.6 });
        for (var y = GridStep; y < h; y += GridStep)
            canvas.Children.Add(new Line { StartPoint = new(0, y), EndPoint = new(w, y), Stroke = gridBrush, StrokeThickness = 0.6 });
    }
}
