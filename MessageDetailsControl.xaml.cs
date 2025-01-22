using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Collections.Concurrent;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Reflection;

namespace MavlinkInspector.Controls;

public partial class MessageDetailsControl : UserControl
{
    private const int UI_UPDATE_INTERVAL = 500;  // UI update interval'i artır
    private const int BATCH_SIZE = 10;  // Batch size'ı düşür
    private const int UPDATE_BATCH_SIZE = 20;
    private const int MIN_UPDATE_INTERVAL = 100;

    private readonly ConcurrentQueue<Action> _updateQueue = new();
    private readonly DispatcherTimer _batchTimer;

    private readonly PacketInspector<MAVLink.MAVLinkMessage> _mavInspector;
    private MAVLink.MAVLinkMessage? _currentMessage;
    private readonly DispatcherTimer _updateTimer;
    private readonly ConcurrentDictionary<(byte sysid, byte compid, uint msgid, string field), bool> _selectedFields = new();
    private int _updateInterval;
    public event EventHandler<bool> SelectionChanged;

    public event EventHandler<IEnumerable<(byte sysid, byte compid, uint msgid, string field)>>? FieldsSelectedForGraph;
    public int UpdateInterval
    {
        get => _updateInterval;
        set
        {
            _updateInterval = value;
            UpdateTimerInterval(value);
        }
    }

    private void UpdateTimerInterval(int interval)
    {
        if (_updateTimer != null)
        {
            _updateTimer.Interval = TimeSpan.FromMilliseconds(interval);
        }
    }

    public List<(byte sysid, byte compid, uint msgid, string field)> GetSelectedFieldsForGraph()
    {
        var fields = new List<(byte sysid, byte compid, uint msgid, string field)>();
        if (_currentMessage != null && fieldsListView.SelectedItems.Count > 0)
        {
            foreach (dynamic item in fieldsListView.SelectedItems)
            {
                if (IsNumericType(item.Type.ToString()))
                {
                    fields.Add((_currentMessage.sysid,
                              _currentMessage.compid,
                              _currentMessage.msgid,
                              item.Field.ToString()));
                }
            }
        }
        return fields;
    }

    public bool HasSelectedFields()
    {
        return fieldsListView.SelectedItems.Count > 0;
    }

    public void ClearSelectedFields()
    {
        fieldsListView.SelectedItems.Clear();
        _selectedFields.Clear();
        SelectionChanged?.Invoke(this, false);
        SelectionChangedWithFields?.Invoke(this, new List<(byte, byte, uint, string)>());
    }

    public MessageDetailsControl(PacketInspector<MAVLink.MAVLinkMessage> mavInspector)
    {
        InitializeComponent();
        _mavInspector = mavInspector;

        // Setup update timer
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();

        // Setup fields ListView events
        fieldsListView.SelectionMode = SelectionMode.Extended;
        fieldsListView.SelectionChanged += FieldsListView_SelectionChanged;
        fieldsListView.MouseDoubleClick += FieldsListView_MouseDoubleClick;

        // Modern context menu oluştur
        var contextMenu = new ContextMenu { Style = Application.Current.FindResource("ModernContextMenu") as Style };

        // Graph menu item
        var graphMenuItem = new MenuItem
        {
            Header = "Graph Selected Fields",
            Style = Application.Current.FindResource("ModernMenuItem") as Style,
            Icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,8 L4,4 L8,8 M4,4 L4,12"),
                Stroke = (Brush)Application.Current.FindResource("TextColor"),
                StrokeThickness = 1.5,
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform
            }
        };
        graphMenuItem.Click += GraphMenuItem_Click;
        contextMenu.Items.Add(graphMenuItem);

        // Separator ekle
        contextMenu.Items.Add(new Separator { Style = Application.Current.FindResource("ModernSeparator") as Style });

        // Copy Value menu item
        var copyValueMenuItem = new MenuItem
        {
            Header = "Copy Value",
            Style = Application.Current.FindResource("ModernMenuItem") as Style,
            Icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z"),
                Fill = (Brush)Application.Current.FindResource("TextColor"),
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform
            }
        };
        copyValueMenuItem.Click += (s, e) =>
        {
            if (fieldsListView.SelectedItem is object item)
            {
                try
                {
                    Clipboard.SetText(item?.ToString() ?? "");
                }
                catch { }
            }
        };
        contextMenu.Items.Add(copyValueMenuItem);

        fieldsListView.ContextMenu = contextMenu;

        _batchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UI_UPDATE_INTERVAL)
        };
        _batchTimer.Tick += ProcessUpdateQueue;
        _batchTimer.Start();
    }

    private void ProcessUpdateQueue(object sender, EventArgs e)
    {
        int processed = 0;
        while (_updateQueue.Count > 0 && processed < BATCH_SIZE)
        {
            if (_updateQueue.TryDequeue(out var action))
            {
                action();
                processed++;
            }
        }
    }

    public void SetMessage(MAVLink.MAVLinkMessage message)
    {
        _currentMessage = message;
        UpdateMessageDetails(message);
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentMessage == null || fieldsListView.IsMouseOver) return;

        // Throttle UI updates
        if ((DateTime.Now - _lastUpdate).TotalMilliseconds < UI_UPDATE_INTERVAL)
            return;

        if (_mavInspector.TryGetLatestMessage(
            _currentMessage.sysid,
            _currentMessage.compid,
            _currentMessage.msgid,
            out var latestMessage))
        {
            _lastUpdate = DateTime.Now;
            UpdateMessageDetails(latestMessage);
        }
    }

    private void UpdateMessageDetails(MAVLink.MAVLinkMessage message)
    {
        // Mevcut thread UI thread ise direkt güncelle, değilse kuyruğa al
        if (Dispatcher.CheckAccess())
        {
            UpdateUIDirectly(message);
        }
        else
        {
            _updateQueue.Enqueue(() => UpdateUIDirectly(message));
        }
    }

    private void UpdateUIDirectly(MAVLink.MAVLinkMessage message)
    {
        if (!fieldsListView.IsMouseOver) // Sadece mouse üzerinde değilse güncelle
        {
            using (Dispatcher.DisableProcessing())
            {
                // Store current message
                _currentMessage = message;

                // Update header info
                headerText.Text = message.header.ToString();
                lengthText.Text = message.Length.ToString();
                seqText.Text = message.seq.ToString();
                sysidText.Text = message.sysid.ToString();
                compidText.Text = message.compid.ToString();
                msgidText.Text = message.msgid.ToString();
                msgTypeText.Text = message.GetType().Name;
                msgTypeNameText.Text = message.msgtypename;
                crc16Text.Text = message.crc16.ToString("X4");
                isMavlink2Text.Text = message.ismavlink2 ? "Mavlink V2" : "Mavlink V1";

                // Store current selections
                var selectedFields = fieldsListView.SelectedItems.Cast<dynamic>()
                    .Select(item => item.Field.ToString())
                    .ToList();

                fieldsListView.Items.Clear();

                // Fields ve Properties'i birlikte al
                var members = message.data.GetType()
                    .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property);

                foreach (var member in members)
                {
                    object? value = null;
                    Type? memberType = null;

                    if (member is FieldInfo field)
                    {
                        value = field.GetValue(message.data);
                        memberType = field.FieldType;
                    }
                    else if (member is PropertyInfo property && property.CanRead)
                    {
                        try
                        {
                            value = property.GetValue(message.data);
                            memberType = property.PropertyType;
                        }
                        catch { continue; } // Skip properties that throw exceptions
                    }

                    if (memberType != null)
                    {
                        var typeName = memberType.ToString().Replace("System.", "");
                        var item = new
                        {
                            Field = member.Name,
                            Value = value?.ToString() ?? "null",
                            Type = typeName,
                            IsProperty = member is PropertyInfo
                        };

                        fieldsListView.Items.Add(item);

                        // Restore selection if previously selected
                        if (selectedFields.Contains(member.Name))
                        {
                            fieldsListView.SelectedItems.Add(item);
                        }
                    }
                }
            }
        }
    }

    public event EventHandler<IEnumerable<(byte sysid, byte compid, uint msgid, string field)>> SelectionChangedWithFields;

    public void UpdateSelections()
    {
        var selectedFields = GetSelectedFieldsForGraph();
        SelectionChanged?.Invoke(this, selectedFields.Any());
        SelectionChangedWithFields?.Invoke(this, selectedFields);
    }

    private void FieldsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentMessage == null) return;

        try
        {
            // Seçimi kaldırılanları işle
            foreach (dynamic item in e.RemovedItems)
            {
                var field = item.Field.ToString();
                var key = (_currentMessage.sysid, _currentMessage.compid, _currentMessage.msgid, field);
                _selectedFields.TryRemove(((byte sysid, byte compid, uint msgid, string field))key, out _);
            }

            // Yeni seçilenleri işle
            foreach (dynamic item in e.AddedItems)
            {
                if (IsNumericType(item.Type.ToString()))
                {
                    var field = item.Field.ToString();
                    var key = (_currentMessage.sysid, _currentMessage.compid, _currentMessage.msgid, field);
                    _selectedFields.TryAdd(((byte sysid, byte compid, uint msgid, string field))key, true);
                }
            }

            // Selection event'lerini tetikle
            UpdateSelections();
        }
        catch (Exception)
        {
            // Hata durumunda sessizce devam et
        }
    }

    private void FieldsListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        TriggerGraphing();
    }

    private void GraphMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selectedFields = GetSelectedFieldsForGraph();
        if (selectedFields.Any())
        {
            FieldsSelectedForGraph?.Invoke(this, selectedFields);
        }
    }

    private void TriggerGraphing()
    {
        var selectedFields = GetSelectedFieldsForGraph();
        if (selectedFields.Any())
        {
            FieldsSelectedForGraph?.Invoke(this, selectedFields);
        }
    }

    private bool IsNumericType(string typeName)
    {
        return typeName.Contains("Int") ||
               typeName.Contains("Float") ||
               typeName.Contains("Double") ||
               typeName.Contains("Decimal") ||
               typeName.Contains("Single") ||
               typeName.Contains("Byte") ||
               typeName.Contains("sbyte") ||
               typeName.Contains("long") ||
               typeName.Contains("short");
    }

    public void StopUpdates()
    {
        _updateTimer.Stop();
    }

    public void RemoveSelectedField(byte sysid, byte compid, uint msgid, string field)
    {
        try
        {
            var key = (sysid, compid, msgid, field);
            _selectedFields.TryRemove(key, out _);

            Dispatcher.Invoke(() =>
            {
                var itemToRemove = fieldsListView.Items.Cast<dynamic>()
                    .FirstOrDefault(item =>
                        _currentMessage != null &&
                        _currentMessage.sysid == sysid &&
                        _currentMessage.compid == compid &&
                        _currentMessage.msgid == msgid &&
                        item.Field.ToString() == field);

                if (itemToRemove != null)
                {
                    fieldsListView.SelectedItems.Remove(itemToRemove);
                }

                // Selection event'lerini tetikle
                UpdateSelections();
            });
        }
        catch (Exception)
        {
            // Hata durumunda sessizce devam et
        }
    }

    public MAVLink.MAVLinkMessage? GetCurrentMessage()
    {
        return _currentMessage;
    }

    private double? GetFieldValue(object data, string memberName)
    {
        try
        {
            var type = data.GetType();

            // Önce field olarak dene
            var field = type.GetField(memberName);
            if (field != null)
            {
                var value = field.GetValue(data);
                return value != null ? Convert.ToDouble(value) : null;
            }

            // Field bulunamadıysa property olarak dene
            var property = type.GetProperty(memberName);
            if (property != null && property.CanRead)
            {
                try
                {
                    var value = property.GetValue(data);
                    return value != null ? Convert.ToDouble(value) : null;
                }
                catch
                {
                    return null; // Property okuma hatası durumunda null dön
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private DateTime _lastUpdate = DateTime.MinValue;

    public enum FieldType
    {
        Numeric,
        Text,
        Enum,
        Array,
        Custom
    }

    public enum UpdateStrategy
    {
        OnChange,
        Periodic,
        Manual
    }

    private readonly record struct FieldMetadata
    {
        public FieldType Type { get; init; }
        public bool IsReadOnly { get; init; }
        public string DisplayName { get; init; }
        public string Description { get; init; }
    }

    // Performans için yeni yapılar
    private readonly ConcurrentDictionary<string, FieldMetadata> _fieldMetadata = new();
    private readonly ConcurrentDictionary<string, Func<object, string>> _valueFormatters = new();
}
