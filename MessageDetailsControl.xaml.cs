using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Collections.Concurrent;
using System.Windows.Threading;

namespace MavlinkInspector.Controls;

public partial class MessageDetailsControl : UserControl
{
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
        fieldsListView.SelectionChanged += FieldsListView_SelectionChanged;
        fieldsListView.MouseDoubleClick += FieldsListView_MouseDoubleClick;

        // Add context menu
        var contextMenu = new ContextMenu();
        var graphMenuItem = new MenuItem { Header = "Graph Selected Fields" };
        graphMenuItem.Click += GraphMenuItem_Click;
        contextMenu.Items.Add(graphMenuItem);
        fieldsListView.ContextMenu = contextMenu;
    }

    public void SetMessage(MAVLink.MAVLinkMessage message)
    {
        _currentMessage = message;
        UpdateMessageDetails(message);
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

    private void FieldsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectionChanged?.Invoke(this, HasSelectedFields());

        if (_currentMessage == null) return;

        // Handle newly selected items
        foreach (dynamic item in e.AddedItems)
        {
            if (IsNumericType(item.Type.ToString()))
            {
                _selectedFields.TryAdd(
                    (_currentMessage.sysid, _currentMessage.compid, _currentMessage.msgid, item.Field.ToString()),
                    true);
            }
        }

        // Handle deselected items
        foreach (dynamic item in e.RemovedItems)
        {
            _selectedFields.TryRemove(
                (_currentMessage.sysid, _currentMessage.compid, _currentMessage.msgid, item.Field.ToString()),
                out _);
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
