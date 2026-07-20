using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RouterMonitor.Wpf.Controls;

/// <summary>
/// Minimal dependency-free line chart: two auto-scaled polylines (download/upload) redrawn on
/// data or size change. Deliberately hand-rolled instead of pulling in a charting library —
/// the requirement is a simple transfer-over-time sparkline, not an interactive chart.
/// </summary>
public partial class TransferChart : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty DownstreamProperty = DependencyProperty.Register(
        nameof(Downstream), typeof(IEnumerable), typeof(TransferChart),
        new PropertyMetadata(null, OnSeriesChanged));

    public static readonly DependencyProperty UpstreamProperty = DependencyProperty.Register(
        nameof(Upstream), typeof(IEnumerable), typeof(TransferChart),
        new PropertyMetadata(null, OnSeriesChanged));

    public IEnumerable? Downstream
    {
        get => (IEnumerable?)GetValue(DownstreamProperty);
        set => SetValue(DownstreamProperty, value);
    }

    public IEnumerable? Upstream
    {
        get => (IEnumerable?)GetValue(UpstreamProperty);
        set => SetValue(UpstreamProperty, value);
    }

    public TransferChart()
    {
        InitializeComponent();
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TransferChart)d;

        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= chart.OnDataCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += chart.OnDataCollectionChanged;

        chart.Redraw();
    }

    private void OnDataCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        DrawSurface.Children.Clear();

        var width = DrawSurface.ActualWidth;
        var height = DrawSurface.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var down = ToList(Downstream);
        var up = ToList(Upstream);
        if (down.Count == 0 && up.Count == 0)
            return;

        var max = Math.Max(down.Count > 0 ? down.Max() : 0, up.Count > 0 ? up.Max() : 0);
        if (max <= 0)
            max = 1;

        AddSeries(down, max, width, height, System.Windows.Media.Brushes.DodgerBlue);
        AddSeries(up, max, width, height, System.Windows.Media.Brushes.MediumVioletRed);
    }

    private void AddSeries(List<double> values, double max, double width, double height, System.Windows.Media.Brush brush)
    {
        if (values.Count < 2)
            return;

        var points = new PointCollection(values.Count);
        var stepX = width / (values.Count - 1);

        for (var i = 0; i < values.Count; i++)
        {
            var x = i * stepX;
            var y = height - values[i] / max * height;
            points.Add(new System.Windows.Point(x, y));
        }

        DrawSurface.Children.Add(new Polyline
        {
            Points = points,
            Stroke = brush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });
    }

    private static List<double> ToList(IEnumerable? source)
    {
        var result = new List<double>();
        if (source is null)
            return result;

        foreach (var item in source)
        {
            if (item is double d)
                result.Add(d);
        }

        return result;
    }
}
