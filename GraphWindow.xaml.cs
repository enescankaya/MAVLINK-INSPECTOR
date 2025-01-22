using LiveCharts;
using LiveCharts.Wpf;
using System.Windows;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Threading;
using System.ComponentModel;
using System.Collections.Concurrent; // ConcurrentDictionary için ekleyin
using System.Windows.Interop; // HwndSource için
using System.Threading.Channels; // Channel için
using System.Windows.Input; // MouseWheelEventArgs için
using Microsoft.Win32; // SaveFileDialog için   
using System.IO; // File işlemleri için
using System.Windows.Media.Imaging; // RenderTargetBitmap için
using MavlinkInspector.Services;

namespace MavlinkInspector
{
    // DictionaryExtensions'ı sınıf dışına taşıyın ve static yapın
    public static class DictionaryExtensions
    {
        public static TValue GetOrAddValue<TKey, TValue>(
            this ConcurrentDictionary<TKey, TValue> dictionary,
            TKey key,
            TValue value)
        {
            return dictionary.GetOrAdd(key, _ => value);
        }
    }

    public class LegendItem
    {
        public string Title { get; set; } = "";
        public Brush Color { get; set; } = Brushes.White;
        public double Value { get; set; }
        public StatisticsInfo Statistics { get; set; } = new StatisticsInfo();
    }

    public partial class GraphWindow : Window, IDisposable
    {
        // Constants
        private const int MIN_UPDATE_INTERVAL = 16;
        private const int DEFAULT_UPDATE_INTERVAL = 50;
        private const int MAX_BATCH_SIZE = 20;
        private const int RENDER_TIMEOUT_MS = 100;
        private const int RESIZE_DELAY = 100;
        // Thread-safe collections
        private readonly ConcurrentDictionary<string, FieldStatistics> _fieldStats = new();
        private static readonly HashSet<GraphWindow> _activeWindows = new();
        private static readonly SemaphoreSlim _renderLock = new(1, 1);
        private static readonly object _windowLock = new();

        // Resource management
        private readonly CancellationTokenSource _cts = new();
        private bool _isDisposed;
        private BatchProcessor<(string key, double value)> _batchProcessor;

        // Performance tracking
        private int _lastUpdateTick;
        private bool _isResizing;
        private bool _isDragging;

        private readonly SeriesCollection _seriesCollection;
        private readonly Dictionary<string, ChartValues<double>> _valuesByField;
        private readonly Dictionary<string, DateTime> _lastUpdateTime;
        private DispatcherTimer _updateTimer;
        private readonly PacketInspector<MAVLink.MAVLinkMessage> _inspector;
        private readonly List<(byte sysid, byte compid, uint msgid, string field)> _trackedFields;
        private readonly ObservableCollection<LegendItem> _legendItems;
        private readonly Dictionary<string, double> _previousValues = new();
        private double _filterRatio = 0.7; // 70% önceki değer, 30% yeni değer

        private class FieldStatistics
        {
            private double _min = double.MaxValue;
            private double _max = double.MinValue;
            private double _sum = 0;
            private int _count = 0;

            public double Min { get => _min; set => _min = value; }
            public double Max { get => _max; set => _max = value; }
            public double Sum { get => _sum; set => _sum = value; }
            public int Count { get => _count; set => _count = value; }
            public double Mean => Count > 0 ? Sum / Count : 0;
        }

        private readonly Color[] _graphColors = new[]
        {
            Color.FromRgb(255, 99, 132),   // Kırmızı
            Color.FromRgb(54, 162, 235),   // Mavi
            Color.FromRgb(255, 206, 86),   // Sarı
            Color.FromRgb(75, 192, 192),   // Turkuaz
            Color.FromRgb(153, 102, 255),  // Mor
            Color.FromRgb(255, 159, 64),   // Turuncu
            Color.FromRgb(76, 230, 135),   // Yeşil
            Color.FromRgb(250, 120, 200)   // Pembe
        };

        private readonly DispatcherTimer _resizeTimer;

        private double _originalMinX;
        private double _originalMaxX;
        private double _originalMinY;
        private double _originalMaxY;

        // Yeni özellik ekle
        private bool _isPaused;

        // Add this static property at class level
        public static Func<double, string> ValueFormatter { get; } = (value) => value.ToString("F9");

        // Yeni field ekle
        private readonly HashSet<string> _focusedSeries = new();

        public GraphWindow(PacketInspector<MAVLink.MAVLinkMessage> inspector, IEnumerable<(byte sysid, byte compid, uint msgid, string field)> fields)
        {
            InitializeComponent();
            Chart.PreviewMouseWheel += (s, e) =>
            {
                // Zoom değiştiğinde AutoScale'i kapat
                AutoScaleCheckbox.IsChecked = false;
            };
            // Window yapılandırması
            Owner = Application.Current.MainWindow; // MainWindow'u parent olarak ayarla

            // GPU optimizations
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            RenderOptions.SetCachingHint(this, CachingHint.Cache);

            // Initialize batch processor
            _batchProcessor = new BatchProcessor<(string key, double value)>(
                ProcessBatch,
                MAX_BATCH_SIZE,
                TimeSpan.FromMilliseconds(DEFAULT_UPDATE_INTERVAL),
                _cts.Token);

            // Window management
            lock (_windowLock)
            {
                _activeWindows.Add(this);
            }

            // CompositionTarget.Rendering event'ine direkt subscribe ol
            CompositionTarget.Rendering += CompositionTarget_Rendering;

            _inspector = inspector;
            _seriesCollection = new SeriesCollection();
            _valuesByField = new Dictionary<string, ChartValues<double>>();
            _lastUpdateTime = new Dictionary<string, DateTime>();
            _trackedFields = new List<(byte sysid, byte compid, uint msgid, string field)>(fields);
            _legendItems = new ObservableCollection<LegendItem>();

            Chart.Series = _seriesCollection;

            // Add this after Chart.Series = _seriesCollection;
            Chart.DataContext = this; // Enable binding to ValueFormatter

            // LegendItemsControl referansını kaldır ve doğrudan DataContext'i ayarla
            DataContext = _legendItems;

            InitializeSeries();
            InitializeEventHandlers();
            StartTimer();
            InitializeFilterControls(); // Bu satırı ekleyin

            // Resize olaylarını yönetmek için timer
            _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RESIZE_DELAY) };
            _resizeTimer.Tick += (s, e) =>
            {
                _resizeTimer.Stop();
                _isResizing = false;
                EnableChartUpdates(true);
                InvalidateVisual();
            };

            // Resize event handlers
            this.SizeChanged += Window_SizeChanged;

            // Pencere taşıma olaylarını ekle
            this.SourceInitialized += Window_SourceInitialized;
            this.Closed += Window_Closed;

            // Timer optimizasyonları
            _updateTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(DEFAULT_UPDATE_INTERVAL)
            };

            Dispatcher.Thread.Priority = ThreadPriority.Highest;
            Chart.DisableAnimations = true;
            Chart.UseLayoutRounding = true;

            CompositionTarget.Rendering += async (s, e) =>
            {
                if (_isDisposed) return;
                if (await _renderLock.WaitAsync(RENDER_TIMEOUT_MS))
                {
                    try
                    {
                        if (!_isResizing && !_isDragging)
                        {
                            Chart.UpdateLayout();
                        }
                    }
                    finally
                    {
                        _renderLock.Release();
                    }
                }
            };

            // ComboBox'ların varsayılan değerlerini ayarla
            UpdateRateCombo.SelectedIndex = UpdateRateCombo.Items
                .Cast<ComboBoxItem>()
                .ToList()
                .FindIndex(item => item.Content.ToString() == "50");

            // Graf dondurma bilgisini göster
            PauseInfoText.Text = "Wheel Click: Pause/Resume Graph";
            PauseInfoText.Opacity = 0.6;

            SaveOriginalAxisLimits();

            // Mouse wheel event handler'ı Preview event'ine bağla
            //Chart.PreviewMouseWheel += Chart_PreviewMouseWheel;
            Chart.MouseDown += Chart_MouseDown;

            // DataGrid selection changed handler'ı ekle
            StatisticsGrid.SelectionChanged += StatisticsGrid_SelectionChanged;
        }

        private void SaveOriginalAxisLimits()
        {
            _originalMinX = Chart.AxisX[0].MinValue;
            _originalMaxX = Chart.AxisX[0].MaxValue;
            _originalMinY = Chart.AxisY[0].MinValue;
            _originalMaxY = Chart.AxisY[0].MaxValue;
        }
        private void Chart_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Sağ tık menüsünün açılmasına izin ver
            e.Handled = false;
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            ResetChartView();
        }

        private void ResetChartView()
        {
            Chart.AxisX[0].MinValue = _originalMinX;
            Chart.AxisX[0].MaxValue = _originalMaxX;
            Chart.AxisY[0].MinValue = _originalMinY;
            Chart.AxisY[0].MaxValue = _originalMaxY;
        }

        private void SaveAsPng_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PNG Image|*.png",
                FileName = $"chart_{DateTime.Now:yyyyMMdd_HHmmss}.png"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    // Tüm grafik penceresini kapsayacak şekilde boyutları al
                    var parentCanvas = GetChartCanvas();
                    if (parentCanvas == null) return;

                    // Chart'ın tam boyutlarını al
                    var chartWidth = Chart.ActualWidth;
                    var chartHeight = Chart.ActualHeight;

                    // Grafik için yeni bir RenderTargetBitmap oluştur
                    var renderBitmap = new RenderTargetBitmap(
                        (int)chartWidth,
                        (int)chartHeight,
                        96, 96, // DPI değerleri
                        PixelFormats.Pbgra32);

                    // Geçici olarak grafik görünümünü düzenle
                    var transform = Chart.LayoutTransform;
                    Chart.LayoutTransform = null;

                    // Grafik boyutlarını kaydet
                    var size = new Size(chartWidth, chartHeight);
                    Chart.Measure(size);
                    Chart.Arrange(new Rect(size));

                    // Grafiği render et
                    renderBitmap.Render(Chart);

                    // Grafik görünümünü eski haline getir
                    Chart.LayoutTransform = transform;

                    // PNG encoder oluştur ve kaydet
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

                    using (var stream = new FileStream(dialog.FileName, FileMode.Create))
                    {
                        encoder.Save(stream);
                    }

                    MessageBoxService.ShowInfo("Chart saved successfully!", this, "Success");
                }
                catch (Exception ex)
                {

                    MessageBoxService.ShowError($"Error saving chart:", this, "Error");

                }
            }
        }

        private void SaveAsCsv_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV File|*.csv",
                FileName = $"chart_data_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                using var writer = new StreamWriter(dialog.FileName);

                // Başlık satırı
                var headers = Chart.Series.Select(s => s.Title).ToList();
                writer.WriteLine(string.Join(",", headers));

                // Veri satırları
                var maxPoints = Chart.Series.Max(s => s.Values.Count);
                for (int i = 0; i < maxPoints; i++)
                {
                    var values = Chart.Series.Select(s => i < s.Values.Count ? s.Values[i].ToString() : "").ToList();
                    writer.WriteLine(string.Join(",", values));
                }

                MessageBoxService.ShowSuccess("Data exported successfully!", this, "Success");



            }
        }

        private void ToggleGridLines_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuItem)sender;
            var showGrid = menuItem.IsChecked;

            foreach (var axis in Chart.AxisX.Concat(Chart.AxisY))
            {
                axis.Separator.StrokeThickness = showGrid ? 1 : 0;
            }
        }

        private void TogglePointMarkers_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuItem)sender;
            foreach (var series in Chart.Series)
            {
                if (series is LineSeries lineSeries)
                {
                    lineSeries.PointGeometry = menuItem.IsChecked ? DefaultGeometries.Circle : null;
                }
            }
        }

        private void ToggleSmoothLines_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuItem)sender;
            foreach (var series in Chart.Series)
            {
                if (series is LineSeries lineSeries)
                {
                    lineSeries.LineSmoothness = menuItem.IsChecked ? 1 : 0;
                }
            }
        }

        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null)
            {
                source.AddHook(WndProc);
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            try
            {
                _isDisposed = true;
                _cts.Cancel();
                lock (_windowLock)
                {
                    _activeWindows.Remove(this);
                }

                _updateTimer?.Stop();
                _resizeTimer?.Stop();

                CompositionTarget.Rendering -= CompositionTarget_Rendering;

                CleanupResources();
            }
            catch { }
        }

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (_isDisposed) return;

            var currentTick = Environment.TickCount;
            if (currentTick - _lastUpdateTick < MIN_UPDATE_INTERVAL) return;

            _lastUpdateTick = currentTick;

            if (!_isResizing && !_isDragging)
            {
                Chart.UpdateLayout();
            }
        }

        private void InitializeSeries()
        {
            // Önce mevcut koleksiyonları temizle
            _seriesCollection.Clear();
            _valuesByField.Clear();
            _lastUpdateTime.Clear();
            _fieldStats.Clear();
            _legendItems.Clear();

            // Tekrar eden alanları önlemek için bir HashSet kullan
            var uniqueFields = new HashSet<(byte sysid, byte compid, uint msgid, string field)>();
            foreach (var field in _trackedFields)
            {
                // Eğer bu alan zaten eklenmiş ise atla
                if (!uniqueFields.Add(field)) continue;

                var key = GetFieldKey(field);
                var colorIndex = _seriesCollection.Count % _graphColors.Length;
                var color = _graphColors[colorIndex];
                var brush = new SolidColorBrush(color);

                var values = new ChartValues<double>();
                _valuesByField[key] = values;
                _lastUpdateTime[key] = DateTime.MinValue;
                _fieldStats[key] = new FieldStatistics();

                var title = $"{field.field} (Sys:{field.sysid} Comp:{field.compid} Msg:{field.msgid})";

                _seriesCollection.Add(new LineSeries
                {
                    Title = title,
                    Values = values,
                    PointGeometry = null,
                    Stroke = brush,
                    Fill = Brushes.Transparent,
                    LineSmoothness = 0
                });

                _legendItems.Add(new LegendItem
                {
                    Title = title,
                    Color = brush,
                    Value = 0,
                    Statistics = new StatisticsInfo()
                });
            }

            // _trackedFields listesini de güncelle
            _trackedFields.Clear();
            _trackedFields.AddRange(uniqueFields);
        }

        private void InitializeEventHandlers()
        {
            SampleCountCombo.SelectionChanged += (s, e) => UpdateSampleCount();
            UpdateRateCombo.SelectionChanged += (s, e) => UpdateTimerInterval();
            AutoScaleCheckbox.Checked += (s, e) => EnableAutoScale();
            AutoScaleCheckbox.Unchecked += (s, e) => DisableAutoScale();
        }


        private void StartTimer()
        {
            _updateTimer = new DispatcherTimer();
            UpdateTimerInterval();
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();
        }

        private async void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (_isResizing || _isDragging || _isDisposed || _isPaused) return;

            try
            {
                var maxSamples = int.Parse(((ComboBoxItem)SampleCountCombo.SelectedItem).Content.ToString()!);
                var currentBatch = new List<(string key, double value)>();

                for (int i = 0; i < _trackedFields.Count; i++)
                {
                    var field = _trackedFields[i];
                    var key = GetFieldKey(field);

                    if (_inspector.TryGetLatestMessage(field.sysid, field.compid, field.msgid, out var message))
                    {
                        var now = DateTime.Now;
                        var updateInterval = int.Parse(((ComboBoxItem)UpdateRateCombo.SelectedItem).Content.ToString()!);

                        // Update rate kontrolü
                        if ((now - _lastUpdateTime[key]).TotalMilliseconds < updateInterval)
                            continue;

                        _lastUpdateTime[key] = now;
                        var value = GetFieldValue(message.data, field.field);

                        if (value.HasValue)
                        {
                            // Batch işleme için değeri ekle
                            currentBatch.Add((key, value.Value));

                            // Doğrudan güncelleme
                            var values = _valuesByField[key];
                            var filteredValue = ApplyFilter(key, value.Value);
                            values.Add(filteredValue);

                            while (values.Count > maxSamples)
                                values.RemoveAt(0);

                            // Legend ve istatistikleri güncelle
                            _legendItems[i].Value = filteredValue;
                            UpdateStatistics(key, filteredValue);

                            var legendItem = _legendItems[i];
                            var stats = _fieldStats[key];
                            if (legendItem != null)
                            {
                                legendItem.Statistics.Min = stats.Min;
                                legendItem.Statistics.Max = stats.Max;
                                legendItem.Statistics.Mean = stats.Mean;
                            }
                        }
                    }
                }

                if (currentBatch.Any())
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        Chart.UpdateLayout();
                        StatisticsGrid.Items.Refresh();
                    }, DispatcherPriority.Send);
                }

                // Seçili serilerin scale'ini güncelle
                if (_focusedSeries.Count > 0)
                {
                    var selectedSeries = _seriesCollection
                        .Cast<LineSeries>()
                        .Where(s => _focusedSeries.Contains(s.Title));

                    var values = selectedSeries.SelectMany(s => s.Values.Cast<double>());
                    var minY = values.Min();
                    var maxY = values.Max();
                    var padding = (maxY - minY) * 0.1;

                    Chart.AxisY[0].MinValue = minY - padding;
                    Chart.AxisY[0].MaxValue = maxY + padding;
                }

                UpdateStatus();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        private void UpdateTimerInterval()
        {
            if (UpdateRateCombo.SelectedItem is ComboBoxItem item)
            {
                var interval = int.Parse(item.Content.ToString()!);
                _updateTimer.Interval = TimeSpan.FromMilliseconds(interval);
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Önce seçimleri temizle
                StatisticsGrid.SelectedItems.Clear();
                _focusedSeries.Clear();

                // Grafik serilerini normal haline getir
                foreach (var series in _seriesCollection.Cast<LineSeries>())
                {
                    series.StrokeThickness = 2;
                    series.Opacity = 1;
                }

                // Verileri temizle
                foreach (var values in _valuesByField.Values)
                {
                    values?.Clear();
                }

                // İstatistikleri sıfırla
                foreach (var stats in _fieldStats.Values)
                {
                    if (stats != null)
                    {
                        stats.Min = 0;
                        stats.Max = 0;
                        stats.Sum = 0;
                        stats.Count = 0;
                    }
                }

                // Legend itemları güncelle
                foreach (var item in _legendItems)
                {
                    if (item != null)
                    {
                        item.Value = 0;
                        item.Statistics.Min = 0;
                        item.Statistics.Max = 0;
                        item.Statistics.Mean = 0;
                    }
                }

                // Önceki değerleri temizle
                _previousValues.Clear();

                // Eksenleri varsayılan değerlere ayarla
                Chart.AxisY[0].MinValue = 0;
                Chart.AxisY[0].MaxValue = 100;
                Chart.AxisX[0].MinValue = 0;
                Chart.AxisX[0].MaxValue = 100;

                // UI güncelle
                Chart.UpdateLayout();
                StatisticsGrid.Items.Refresh();
                UpdateStatus();
                ResetView_Click(null, null);
            }
            catch (Exception ex)
            {
                MessageBoxService.ShowError($"Error clearing data: {ex.Message}", this);
            }
        }

        private void UpdateSampleCount()
        {
            if (SampleCountCombo.SelectedItem is ComboBoxItem item)
            {
                var maxSamples = int.Parse(item.Content.ToString()!);
                foreach (var values in _valuesByField.Values)
                {
                    while (values.Count > maxSamples)
                        values.RemoveAt(0);
                }
            }
        }

        private void EnableAutoScale()
        {
            Chart.AxisY[0].MinValue = double.NaN;
            Chart.AxisY[0].MaxValue = double.NaN;
        }

        private void DisableAutoScale()
        {
            var allValues = _valuesByField.Values.SelectMany(v => v).ToList();
            if (allValues.Any())
            {
                Chart.AxisY[0].MinValue = allValues.Min();
                Chart.AxisY[0].MaxValue = allValues.Max();
            }
        }

        private void UpdateStatus()
        {
            var totalPoints = _valuesByField.Values.Sum(v => v.Count);
            StatusText.Text = $"Tracking {_trackedFields.Count} fields, {totalPoints} total points";
        }

        private double ApplyFilter(string key, double newValue)
        {
            if (_filterRatio <= 0) return newValue;  // Ham sinyal
            if (_filterRatio >= 1) return _previousValues.GetValueOrDefault(key, newValue);  // Maksimum smoothing

            if (!_previousValues.TryGetValue(key, out double previousValue))
            {
                _previousValues[key] = newValue;
                return newValue;
            }

            var filteredValue = (_filterRatio * previousValue) + ((1 - _filterRatio) * newValue);
            _previousValues[key] = filteredValue;
            return filteredValue;
        }

        private void UpdateStatistics(string key, double value)
        {
            var stats = _fieldStats.GetOrAddValue(key, new FieldStatistics());

            if (stats.Count == 0)
            {
                stats.Min = value;
                stats.Max = value;
                stats.Sum = value;
            }
            else
            {
                stats.Min = Math.Min(stats.Min, value);
                stats.Max = Math.Max(stats.Max, value);
                stats.Sum += value;
            }
            stats.Count++;
        }

        private void InitializeFilterControls()
        {
            FilterSlider.Value = _filterRatio * 100;
            UpdateFilterText(_filterRatio);

            FilterSlider.ValueChanged += (s, e) =>
            {
                _filterRatio = FilterSlider.Value / 100.0;
                UpdateFilterText(_filterRatio);
                _previousValues.Clear();
            };

            // Timer interval için özel event handler
            UpdateRateCombo.SelectionChanged += (s, e) =>
            {
                if (UpdateRateCombo.SelectedItem is ComboBoxItem item &&
                    int.TryParse(item.Content.ToString(), out int interval))
                {
                    _updateTimer.Interval = TimeSpan.FromMilliseconds(interval);
                    _batchProcessor = new BatchProcessor<(string key, double value)>(
                        ProcessBatch,
                        MAX_BATCH_SIZE,
                        TimeSpan.FromMilliseconds(interval),
                        _cts.Token);
                }
            };
        }

        private void UpdateFilterText(double ratio)
        {
            if (ratio <= 0)
                FilterValueText.Text = "Filter: Raw signal (no smoothing)";
            else if (ratio >= 1)
                FilterValueText.Text = "Filter: Maximum smoothing";
            else
                FilterValueText.Text = $"Filter: {ratio * 100:F0}% previous, {(1 - ratio) * 100:F0}% new";
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_isResizing)
            {
                _isResizing = true;
                EnableChartUpdates(false);
                _resizeTimer.Stop();
            }
            _resizeTimer.Start();
        }

        private void EnableChartUpdates(bool enable)
        {
            if (_isDisposed) return;

            if (enable && !_isResizing && !_isDragging)
            {
                Chart.DisableAnimations = true;
                _updateTimer.Start();
                Chart.UpdateLayout();
            }
            else
            {
                _updateTimer.Stop();
                Chart.DisableAnimations = true;
            }

            // Diğer pencerelerin güncellemelerini yönet
            lock (_windowLock)
            {
                foreach (var window in _activeWindows)
                {
                    if (window != this && !window._isDisposed)
                    {
                        window._updateTimer.Interval = TimeSpan.FromMilliseconds(
                            _activeWindows.Count * MIN_UPDATE_INTERVAL);
                    }
                }
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            if (_isDisposed) return;

            base.OnRenderSizeChanged(sizeInfo);

            if (!_isResizing)
            {
                _isResizing = true;
                EnableChartUpdates(false);
                _resizeTimer.Stop();
            }
            _resizeTimer.Start();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_isDisposed) return IntPtr.Zero;

            switch (msg)
            {
                case 0x0231: // WM_ENTERSIZEMOVE
                    _isDragging = true;
                    EnableChartUpdates(false);
                    break;

                case 0x0232: // WM_EXITSIZEMOVE
                    _isDragging = false;
                    EnableChartUpdates(true);
                    InvalidateVisual();
                    break;
            }
            return IntPtr.Zero;
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _cts.Cancel();
            lock (_windowLock)
            {
                _activeWindows.Remove(this);
            }
            CleanupResources();
        }

        private void CleanupResources()
        {
            try
            {
                _batchProcessor?.Dispose();
                _cts?.Dispose();
            }
            catch { }
        }

        private async Task ProcessBatch(IReadOnlyList<(string key, double value)> batch)
        {
            if (_isDisposed) return;

            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var (key, value) in batch)
                    {
                        ProcessValue(key, value);
                    }
                    if (!_isResizing && !_isDragging)
                    {
                        Chart.UpdateLayout();
                    }
                }, DispatcherPriority.Send);
            }
            catch (Exception ex)
            {
            }
        }

        private string GetFieldKey((byte sysid, byte compid, uint msgid, string field) field)
        {
            return $"{field.sysid}_{field.compid}_{field.msgid}_{field.field}";
        }

        private double? GetFieldValue(object data, string fieldName)
        {
            try
            {
                var type = data.GetType();

                // Önce field olarak dene
                var field = type.GetField(fieldName);
                if (field != null)
                {
                    var value = field.GetValue(data);
                    if (value != null) return Convert.ToDouble(value);
                }

                // Field bulunamadıysa property olarak dene 
                var property = type.GetProperty(fieldName);
                if (property != null && property.CanRead)
                {
                    try
                    {
                        var value = property.GetValue(data);
                        if (value != null) return Convert.ToDouble(value);
                    }
                    catch
                    {
                        // Property okuma hatası
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private void ProcessValue(string key, double value)
        {
            try
            {
                // Değeri 9 decimal'e yuvarla
                value = Math.Round(value, 9);

                if (_valuesByField.TryGetValue(key, out var values))
                {
                    var maxSamples = GetMaxSamples();

                    // Değer filtreleme ve ekleme
                    var filteredValue = ApplyFilter(key, value);
                    filteredValue = Math.Round(filteredValue, 9);

                    // Mevcut veri sayısını kontrol et
                    if (values.Count >= maxSamples)
                    {
                        values.RemoveAt(0);
                    }

                    values.Add(filteredValue);

                    // İstatistikleri güncelle
                    UpdateStatistics(key, filteredValue);
                    UpdateLegendItem(key, filteredValue);
                }
            }
            catch (Exception ex)
            {
                // Hata durumunda sessizce devam et
            }
        }

        private int GetMaxSamples()
        {
            if (SampleCountCombo.SelectedItem is ComboBoxItem item)
            {
                if (int.TryParse(item.Content.ToString(), out int maxSamples))
                {
                    return maxSamples;
                }
            }
            return 100; // Default value
        }

        private void UpdateLegendItem(string key, double value)
        {
            var index = _trackedFields.FindIndex(f => GetFieldKey(f) == key);
            if (index >= 0 && index < _legendItems.Count)
            {
                var legendItem = _legendItems[index];
                legendItem.Value = Math.Round(value, 9);

                if (_fieldStats.TryGetValue(key, out var stats))
                {
                    legendItem.Statistics.Min = Math.Round(stats.Min, 9);
                    legendItem.Statistics.Max = Math.Round(stats.Max, 9);
                    legendItem.Statistics.Mean = Math.Round(stats.Mean, 9);
                }
            }
        }

        // IDisposable implementation
        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            _cts.Cancel();

            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _batchProcessor?.Dispose();
            _cts?.Dispose();
            _renderLock?.Dispose();

            lock (_windowLock)
            {
                _activeWindows.Remove(this);
            }

            // Clear collections
            _seriesCollection?.Clear();
            _valuesByField?.Clear();
            _fieldStats?.Clear();
            _legendItems?.Clear();
        }

        private Canvas GetChartCanvas()
        {
            return Chart.Parent as Canvas;
        }

        // Yeni event handler ekle
        private void Chart_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                _isPaused = !_isPaused;
                PauseInfoText.Text = _isPaused ? "Graph Paused - Click Middle Mouse Button to Resume" : "Click Middle Mouse Button to Pause";
                PauseInfoText.Opacity = _isPaused ? 1.0 : 0.6;
                e.Handled = true;
            }
        }

        private void StatisticsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                _focusedSeries.Clear();

                if (StatisticsGrid.SelectedItems.Count > 0)
                {
                    var selectedSeries = new List<LineSeries>();

                    foreach (LegendItem item in StatisticsGrid.SelectedItems)
                    {
                        var series = _seriesCollection.FirstOrDefault(s => s.Title == item.Title) as LineSeries;
                        if (series != null)
                        {
                            selectedSeries.Add(series);
                            _focusedSeries.Add(item.Title);
                            series.StrokeThickness = 3;
                            series.Opacity = 1;
                        }
                    }

                    // Seçili olmayan serileri sönükleştir
                    foreach (var series in _seriesCollection.Cast<LineSeries>())
                    {
                        if (!_focusedSeries.Contains(series.Title))
                        {
                            series.StrokeThickness = 1;
                            series.Opacity = 0.3;
                        }
                    }

                    if (selectedSeries.Any())
                    {
                        try
                        {
                            var values = selectedSeries.SelectMany(s => s.Values.Cast<double>());
                            if (values.Any())
                            {
                                var minY = values.Min();
                                var maxY = values.Max();
                                var padding = (maxY - minY) * 0.1;

                                Chart.AxisY[0].MinValue = minY - padding;
                                Chart.AxisY[0].MaxValue = maxY + padding;
                            }
                        }
                        catch (Exception ex)
                        {
                            // Değer hesaplama hatası durumunda varsayılan eksenleri kullan
                            Chart.AxisY[0].MinValue = double.NaN;
                            Chart.AxisY[0].MaxValue = double.NaN;
                        }

                        AutoScaleCheckbox.IsChecked = false;
                    }
                }
                else
                {
                    // Tüm serileri normal haline getir
                    foreach (var series in _seriesCollection.Cast<LineSeries>())
                    {
                        series.StrokeThickness = 2;
                        series.Opacity = 1;
                    }

                    ResetAxisScaling();
                }
            }
            catch (Exception ex)
            {
                // Hata durumunda varsayılan ayarlara dön
                ResetAxisScaling();
            }
        }

        private void ResetAxisScaling()
        {
            if (AutoScaleCheckbox.IsChecked == true)
            {
                Chart.AxisY[0].MinValue = double.NaN;
                Chart.AxisY[0].MaxValue = double.NaN;
            }
            else
            {
                try
                {
                    var allSeries = _seriesCollection.Cast<LineSeries>();
                    var values = allSeries.SelectMany(s => s.Values.Cast<double>());
                    if (values.Any())
                    {
                        var minY = values.Min();
                        var maxY = values.Max();
                        var padding = (maxY - minY) * 0.1;

                        Chart.AxisY[0].MinValue = minY - padding;
                        Chart.AxisY[0].MaxValue = maxY + padding;
                    }
                }
                catch
                {
                    // Hata durumunda otomatik ölçeklendirmeye geç
                    Chart.AxisY[0].MinValue = double.NaN;
                    Chart.AxisY[0].MaxValue = double.NaN;
                }
            }
        }
    }

    // Batch işleme için yardımcı sınıf
    public class BatchProcessor<T> : IDisposable
    {
        private readonly Channel<T> _channel;
        private readonly Task _processTask;
        private readonly CancellationToken _cancellationToken;
        private bool _isDisposed;

        public BatchProcessor(
            Func<IReadOnlyList<T>, Task> processAction,
            int batchSize,
            TimeSpan maxDelay,
            CancellationToken cancellationToken)
        {
            _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = true // Performans için eklendi
            });
            _cancellationToken = cancellationToken;

            _processTask = Task.Run(async () =>
            {
                var batch = new List<T>(batchSize);
                var timer = new PeriodicTimer(maxDelay);

                try
                {
                    while (!_cancellationToken.IsCancellationRequested)
                    {
                        var hasItems = false;

                        while (batch.Count < batchSize)
                        {
                            if (!await _channel.Reader.WaitToReadAsync(_cancellationToken))
                                break;

                            while (batch.Count < batchSize && _channel.Reader.TryRead(out var item))
                            {
                                batch.Add(item);
                                hasItems = true;
                            }

                            if (hasItems && !await timer.WaitForNextTickAsync(_cancellationToken))
                                break;
                        }

                        if (batch.Count > 0)
                        {
                            await processAction(batch);
                            batch.Clear();
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        public ValueTask AddAsync(T item) => _channel.Writer.WriteAsync(item, _cancellationToken);

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _channel.Writer.Complete();
            _processTask.Wait(1000);
        }
    }

    public class StatisticsInfo : INotifyPropertyChanged
    {
        private double _min = 0;  // Changed from double.MaxValue
        private double _max = 0;  // Changed from double.MinValue
        private double _mean = 0;
        private double _value;

        public double Min
        {
            get => _min;
            set
            {
                _min = value;
                OnPropertyChanged(nameof(Min));
            }
        }

        public double Max
        {
            get => _max;
            set
            {
                _max = value;
                OnPropertyChanged(nameof(Max));
            }
        }

        public double Mean
        {
            get => _mean;
            set
            {
                _mean = value;
                OnPropertyChanged(nameof(Mean));
            }
        }

        public double Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    OnPropertyChanged(nameof(Value));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
