using LiveCharts;
using LiveCharts.Wpf;
using System.Windows;
using System.Collections.Generic;
using System.Windows.Media;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MavlinkInspector
{
    public class LegendItem
    {
        public string Title { get; set; } = "";
        public Brush Color { get; set; } = Brushes.White;
        public double Value { get; set; }
    }

    public partial class GraphWindow : Window
    {
        private readonly SeriesCollection _seriesCollection;
        private readonly Dictionary<string, ChartValues<double>> _valuesByField;
        private readonly Dictionary<string, DateTime> _lastUpdateTime;
        private DispatcherTimer _updateTimer;
        private readonly PacketInspector<MAVLink.MAVLinkMessage> _inspector;
        private readonly List<(byte sysid, byte compid, uint msgid, string field)> _trackedFields;
        private readonly ObservableCollection<LegendItem> _legendItems;

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

        public GraphWindow(PacketInspector<MAVLink.MAVLinkMessage> inspector, IEnumerable<(byte sysid, byte compid, uint msgid, string field)> fields)
        {
            InitializeComponent();

            _inspector = inspector;
            _seriesCollection = new SeriesCollection();
            _valuesByField = new Dictionary<string, ChartValues<double>>();
            _lastUpdateTime = new Dictionary<string, DateTime>();
            _trackedFields = new List<(byte sysid, byte compid, uint msgid, string field)>(fields);
            _legendItems = new ObservableCollection<LegendItem>();

            Chart.Series = _seriesCollection;
            LegendItemsControl.ItemsSource = _legendItems;

            InitializeSeries();
            InitializeEventHandlers();
            StartTimer();
        }

        private void InitializeSeries()
        {
            for (int i = 0; i < _trackedFields.Count; i++)
            {
                var field = _trackedFields[i];
                var color = _graphColors[i % _graphColors.Length];
                var brush = new SolidColorBrush(color);
                var key = GetFieldKey(field);

                var values = new ChartValues<double>();
                _valuesByField[key] = values;
                _lastUpdateTime[key] = DateTime.MinValue;

                var title = $"{field.field} (Sys:{field.sysid} Comp:{field.compid} Msg:{field.msgid})";

                _seriesCollection.Add(new LineSeries
                {
                    Title = title,
                    Values = values,
                    PointGeometry = null,
                    Stroke = brush,
                    Fill = new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)),
                    LineSmoothness = 0
                });

                _legendItems.Add(new LegendItem
                {
                    Title = title,
                    Color = brush,
                    Value = 0
                });
            }
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

        private void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                var maxSamples = int.Parse(((ComboBoxItem)SampleCountCombo.SelectedItem).Content.ToString()!);

                for (int i = 0; i < _trackedFields.Count; i++)
                {
                    var field = _trackedFields[i];
                    var key = GetFieldKey(field);

                    if (_inspector.TryGetLatestMessage(field.sysid, field.compid, field.msgid, out var message))
                    {
                        var now = DateTime.Now;
                        var updateInterval = int.Parse(((ComboBoxItem)UpdateRateCombo.SelectedItem).Content.ToString()!);

                        if ((now - _lastUpdateTime[key]).TotalMilliseconds < updateInterval)
                            continue;

                        _lastUpdateTime[key] = now;
                        var value = GetFieldValue(message.data, field.field);

                        if (value.HasValue)
                        {
                            var values = _valuesByField[key];
                            values.Add(value.Value);

                            while (values.Count > maxSamples)
                                values.RemoveAt(0);

                            // Legend değerini güncelle
                            _legendItems[i].Value = value.Value;
                        }
                    }
                }

                UpdateStatus();
            }
            catch
            {
                // Handle exceptions
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
                var field = data.GetType().GetField(fieldName);
                if (field == null) return null;

                var value = field.GetValue(data);
                if (value == null) return null;

                return Convert.ToDouble(value);
            }
            catch
            {
                return null;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _updateTimer.Stop();
            base.OnClosed(e);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var values in _valuesByField.Values)
            {
                values.Clear();
            }
            UpdateStatus();
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

        private void UpdateTimerInterval()
        {
            if (UpdateRateCombo.SelectedItem is ComboBoxItem item)
            {
                var interval = int.Parse(item.Content.ToString()!);
                _updateTimer.Interval = TimeSpan.FromMilliseconds(interval);
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
    }
}
