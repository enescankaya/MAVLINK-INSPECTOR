using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Collections.Concurrent;
using System.Windows.Threading;
using System.Collections.Generic;

namespace MavlinkInspector.Controls;

public class SelectedFieldInfo
{
    public string TabId { get; set; }
    public string DisplayText { get; set; }
    public string Field { get; set; }
    public byte SysId { get; set; }
    public byte CompId { get; set; }
    public uint MsgId { get; set; }
}

public partial class MessageDetailsControl : UserControl
{
    private readonly PacketInspector<MAVLink.MAVLinkMessage> _mavInspector;
    private MAVLink.MAVLinkMessage? _currentMessage;
    private readonly DispatcherTimer _updateTimer;
    private readonly ConcurrentDictionary<(byte sysid, byte compid, uint msgid, string field), bool> _selectedFields = new();
    private int _updateInterval;
    private string _tabId;
    public event EventHandler<bool> SelectionChanged;
    public event Action<SelectedFieldInfo, bool> FieldSelectionChanged;

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
        lock (_selectedFields)
        {
            return _selectedFields.Keys.ToList();
        }
    }

    public bool HasSelectedFields()
    {
        return fieldsListView.SelectedItems.Count > 0;
    }

    public void ClearSelectedFields()
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                // Önce tüm seçimleri Dictionary'den kaldır
                foreach (var key in _selectedFields.Keys.ToList())
                {
                    if (_selectedFields.TryRemove(key, out _))
                    {
                        FieldSelectionChanged?.Invoke(new SelectedFieldInfo
                        {
                            TabId = _tabId,
                            Field = key.field,
                            SysId = key.sysid,
                            CompId = key.compid,
                            MsgId = key.msgid
                        }, false);
                    }
                }

                // Sonra ListView'daki seçimleri temizle
                fieldsListView.SelectedItems.Clear();
                SelectionChanged?.Invoke(this, false);
            });
        }
        catch (Exception)
        {
            // Hata durumunda sessizce devam et
        }
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
    }

    public void SetMessage(MAVLink.MAVLinkMessage message)
    {
        _currentMessage = message;
        UpdateMessageDetails(message);
    }

    public void Initialize(string tabId)
    {
        _tabId = tabId;
        fieldsListView.SelectionChanged += FieldsListView_SelectionChanged;
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentMessage == null) return;

        if (_mavInspector.TryGetLatestMessage(
            _currentMessage.sysid,
            _currentMessage.compid,
            _currentMessage.msgid,
            out var latestMessage))
        {
            UpdateMessageDetails(latestMessage);
        }
    }

    private void UpdateMessageDetails(MAVLink.MAVLinkMessage message)
    {
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

        // Update fields
        var fields = message.data.GetType().GetFields();
        foreach (var field in fields)
        {
            var value = field.GetValue(message.data);
            var typeName = field.FieldType.ToString().Replace("System.", "");

            var item = new
            {
                Field = field.Name,
                Value = value?.ToString() ?? "null",
                Type = typeName
            };

            fieldsListView.Items.Add(item);

            // Restore selection if previously selected
            if (selectedFields.Contains(field.Name))
            {
                fieldsListView.SelectedItems.Add(item);
            }
        }
    }
    public void RemoveSelectedField(byte sysid, byte compid, uint msgid, string field)
    {
        try
        {
            var key = (sysid, compid, msgid, field);

            Dispatcher.Invoke(() =>
            {
                if (_selectedFields.TryRemove(key, out _))
                {
                    // ListView'dan kaldır
                    var itemToRemove = fieldsListView.Items.Cast<dynamic>()
                        .FirstOrDefault(item => item.Field.ToString() == field);

                    if (itemToRemove != null)
                    {
                        fieldsListView.SelectedItems.Remove(itemToRemove);
                    }

                    // Event'leri tetikle
                    SelectionChanged?.Invoke(this, HasSelectedFields());
                    FieldSelectionChanged?.Invoke(new SelectedFieldInfo
                    {
                        TabId = _tabId,
                        Field = field,
                        SysId = sysid,
                        CompId = compid,
                        MsgId = msgid
                    }, false);
                }
            }, DispatcherPriority.Normal);
        }
        catch (Exception)
        {
            // Hata durumunda sessizce devam et
        }
    }
    private void FieldsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentMessage == null) return;

        try
        {
            // Eşzamanlı işlem için lock kullan
            lock (_selectedFields)
            {
                // Yeni seçilenleri ekle
                foreach (dynamic item in e.AddedItems)
                {
                    if (IsNumericType(item.Type.ToString()))
                    {
                        var field = new SelectedFieldInfo
                        {
                            TabId = _tabId,
                            DisplayText = $"{_currentMessage.msgtypename}.{item.Field}",
                            Field = item.Field.ToString(),
                            SysId = _currentMessage.sysid,
                            CompId = _currentMessage.compid,
                            MsgId = _currentMessage.msgid
                        };

                        var key = (_currentMessage.sysid, _currentMessage.compid, _currentMessage.msgid, item.Field.ToString());
                        if (_selectedFields.TryAdd(((byte sysid, byte compid, uint msgid, string field))key, true))
                        {
                            FieldSelectionChanged?.Invoke(field, true);
                        }
                    }
                }

                // Seçimi kaldırılanları çıkar
                foreach (dynamic item in e.RemovedItems)
                {
                    var key = (_currentMessage.sysid, _currentMessage.compid, _currentMessage.msgid, item.Field.ToString());
                    if (_selectedFields.TryRemove(((byte sysid, byte compid, uint msgid, string field))key, out _))
                    {
                        var field = new SelectedFieldInfo
                        {
                            TabId = _tabId,
                            Field = item.Field.ToString(),
                            SysId = _currentMessage.sysid,
                            CompId = _currentMessage.compid,
                            MsgId = _currentMessage.msgid
                        };
                        FieldSelectionChanged?.Invoke(field, false);
                    }
                }
            }

            // Seçim durumunu bildir
            SelectionChanged?.Invoke(this, HasSelectedFields());
        }
        catch (Exception)
        {
            // Hata durumunda seçimleri temizle
            ClearSelectedFields();
        }
    }

    public void RemoveFieldFromSelection(string fieldName)
    {
        var itemToRemove = fieldsListView.Items.Cast<dynamic>()
            .FirstOrDefault(item => item.Field.ToString() == fieldName);

        if (itemToRemove != null)
        {
            fieldsListView.SelectedItems.Remove(itemToRemove);
        }
    }

    private void FieldsListView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        TriggerGraphing();
    }

    private void GraphMenuItem_Click(object sender, RoutedEventArgs e)
    {
        TriggerGraphing();
    }

    private void TriggerGraphing()
    {
        if (_selectedFields.Count > 0)
        {
            FieldsSelectedForGraph?.Invoke(this, _selectedFields.Keys);
            _selectedFields.Clear();
            fieldsListView.SelectedItems.Clear();
        }
    }

    private bool IsNumericType(string typeName)
    {
        return typeName.Contains("Int") ||
               typeName.Contains("Float") ||
               typeName.Contains("Double") ||
               typeName.Contains("Decimal") ||
               typeName.Contains("Single") ||
               typeName.Contains("Byte");
    }

    public void StopUpdates()
    {
        _updateTimer.Stop();
    }
}
