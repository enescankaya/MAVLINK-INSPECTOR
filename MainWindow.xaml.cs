using MavlinkInspector.Connections;
using System.IO.Ports;
using System.IO;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;

namespace MavlinkInspector;

public partial class MainWindow : Window
{
    private readonly PacketInspector<MAVLink.MAVLinkMessage> _mavInspector = new();
    private readonly DispatcherTimer _timer = new();
    private Dictionary<uint, Color> _messageColors = new();
    private readonly Random _random = new();
    private ConnectionManager _connectionManager = new();
    private int _totalMessages;
    private DateTime _lastRateUpdate = DateTime.Now;
    private int _messagesSinceLastUpdate;
    private ConcurrentDictionary<uint, DateTime> _lastUpdateTime = new();
    private readonly object _updateLock = new();
    private const int UPDATE_INTERVAL_MS = 100;
    private ConcurrentDictionary<uint, MessageUpdateInfo> _messageUpdateInfo = new();
    private const int UI_UPDATE_INTERVAL_MS = 100;
    private readonly object _treeUpdateLock = new();
    private MAVLink.MAVLinkMessage? _currentlyDisplayedMessage;
    private readonly DispatcherTimer _detailsUpdateTimer = new();

    private class MessageUpdateInfo
    {
        public DateTime LastUpdate { get; set; }
        public double LastRate { get; set; }
        public string LastHeader { get; set; } = string.Empty;
    }

    public MainWindow()
    {
        InitializeComponent();
        InitializeUI();
        SetupTimer();
        SetupMessageHandling();
        SetupDetailsTimer();
    }

    private void InitializeUI()
    {
        ConnectionTypeComboBox.ItemsSource = new[] { "Serial", "TCP", "UDP" };
        ConnectionTypeComboBox.SelectedIndex = 0;

        PortComboBox.ItemsSource = SerialPort.GetPortNames();
        BaudRateComboBox.ItemsSource = new[] { 9600, 19200, 38400, 57600, 115200 };
        BaudRateComboBox.SelectedValue = 57600;

        if (PortComboBox.Items.Count > 0)
            PortComboBox.SelectedIndex = 0;
    }

    private void SetupTimer()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void SetupMessageHandling()
    {
        _mavInspector.NewSysidCompid += (s, e) => Dispatcher.BeginInvoke(UpdateSystemList);

        treeView1.SelectedItemChanged += (s, e) =>
        {
            if (e.NewValue is TreeViewItem item && item.Tag is MAVLink.MAVLinkMessage msg)
            {
                UpdateMessageDetails(msg);
            }
        };
    }

    private void SetupDetailsTimer()
    {
        _detailsUpdateTimer.Interval = TimeSpan.FromMilliseconds(100); // 10 Hz update rate
        _detailsUpdateTimer.Tick += DetailsTimer_Tick;
        _detailsUpdateTimer.Start();
    }

    private void DetailsTimer_Tick(object sender, EventArgs e)
    {
        if (_currentlyDisplayedMessage != null)
        {
            UpdateMessageDetailsIfNeeded(_currentlyDisplayedMessage);
        }
    }

    private void UpdateMessageDetailsIfNeeded(MAVLink.MAVLinkMessage message)
    {
        if (_mavInspector.TryGetLatestMessage(message.sysid, message.compid, message.msgid, out var latestMessage))
        {
            UpdateMessageDetails(latestMessage);
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionManager.IsConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            var parameters = new ConnectionParameters
            {
                ConnectionType = ConnectionTypeComboBox.SelectedItem.ToString(),
                Port = PortComboBox.SelectedItem?.ToString(),
                BaudRate = (int)BaudRateComboBox.SelectedValue,
                IpAddress = IpAddressTextBox.Text,
                NetworkPort = int.Parse(PortTextBox.Text)
            };

            await _connectionManager.ConnectAsync(parameters);
            ConnectButton.Content = "Disconnect";
            UpdateUIState(true);
            statusConnection.Content = "Connected";

            _ = ProcessIncomingDataAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connection failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await DisconnectAsync();
        }
    }

    private async Task DisconnectAsync()
    {
        await _connectionManager.DisconnectAsync();
        ConnectButton.Content = "Connect";
        UpdateUIState(false);
        statusConnection.Content = "Disconnected";
    }

    private async Task ProcessIncomingDataAsync()
    {
        var mavlinkParse = new MAVLink.MavlinkParse();
        var buffer = new byte[4096]; // Fixed size buffer
        var bufferList = new List<byte>();

        try
        {
            await foreach (var data in _connectionManager.DataChannel.ReadAllAsync())
            {
                bufferList.AddRange(data);

                while (bufferList.Count >= 8) // Minimum MAVLink message size
                {
                    using var ms = new MemoryStream(bufferList.ToArray());
                    try
                    {
                        var packet = mavlinkParse.ReadPacket(ms);
                        if (packet == null) break;

                        var processedBytes = (int)ms.Position;
                        bufferList.RemoveRange(0, processedBytes);

                        await ProcessMessageAsync(packet);
                    }
                    catch
                    {
                        bufferList.RemoveAt(0);
                    }
                }

                if (bufferList.Count > 1024 * 16)
                {
                    bufferList.Clear();
                }//--
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProcessIncomingDataAsync error: {ex.Message}");
        }
    }

    private async Task ProcessMessageAsync(MAVLink.MAVLinkMessage message)
    {
        // Skip GCS messages if ShowGCSTraffic is not checked
        if (!ShowGCSTraffic.IsChecked.GetValueOrDefault() && message.sysid == 255)
            return;

        _mavInspector.Add(message.sysid, message.compid, message.msgid, message, message.Length);

        Interlocked.Increment(ref _totalMessages);
        Interlocked.Increment(ref _messagesSinceLastUpdate);

        var messageKey = (uint)((message.sysid << 16) | (message.compid << 8) | message.msgid);
        var now = DateTime.UtcNow;

        var updateInfo = _messageUpdateInfo.GetOrAdd(messageKey, _ => new MessageUpdateInfo());

        if ((now - updateInfo.LastUpdate).TotalMilliseconds >= UI_UPDATE_INTERVAL_MS)
        {
            var rate = _mavInspector.GetMessageRate(message.sysid, message.compid, message.msgid);
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateTreeViewForMessage(message, rate);
                updateInfo.LastRate = rate;
                updateInfo.LastUpdate = now;
            }, DispatcherPriority.Background);
        }
    }

    private TreeViewItem GetOrCreateVehicleNode(byte sysid)
    {
        var header = $"Vehicle {sysid}";
        return treeView1.FindOrCreateChild(header, sysid);
    }

    private TreeViewItem GetOrCreateComponentNode(TreeViewItem vehicleNode, MAVLink.MAVLinkMessage message)
    {
        var header = $"Component {message.compid}";
        return vehicleNode.FindOrCreateChild(header, (message.sysid << 8) | message.compid);
    }
    private void UpdateTreeViewForMessage(MAVLink.MAVLinkMessage message, double rate)
    {
        lock (_treeUpdateLock)
        {
            try
            {
                // Skip GCS messages if ShowGCSTraffic is not checked
                if (!ShowGCSTraffic.IsChecked.GetValueOrDefault() && message.sysid == 255)
                    return;

                var vehicleNode = GetOrCreateVehicleNode(message.sysid);
                var componentNode = GetOrCreateComponentNode(vehicleNode, message);

                var bps = _mavInspector.GetBps(message.sysid, message.compid, message.msgid);
                string header;

                if (bps >= 1000)
                    header = $"{message.msgtypename} ({rate:F1} Hz, {bps / 1000:F1} kbps)";
                else
                    header = $"{message.msgtypename} ({rate:F1} Hz, {bps:F0} bps)";

                var messageKey = (uint)((message.sysid << 16) | (message.compid << 8) | message.msgid);
                var updateInfo = _messageUpdateInfo.GetOrAdd(messageKey, _ => new MessageUpdateInfo());

                if (header != updateInfo.LastHeader)
                {
                    var msgNode = componentNode.FindOrCreateChild(header, message.msgid, message);
                    updateInfo.LastHeader = header;

                    if (!_messageColors.TryGetValue(message.msgid, out var color))
                    {
                        color = _random.GetRandomColor();
                        _messageColors[message.msgid] = color;
                    }

                    // Apply color to the text part only
                    if (msgNode.Header is StackPanel stackPanel &&
                        stackPanel.Children.Count > 1 &&
                        stackPanel.Children[1] is TextBlock textBlock)
                    {
                        textBlock.Foreground = new SolidColorBrush(color);
                    }

                    // Sort only the message nodes, not the vehicle or component nodes
                    SortTreeViewItems(componentNode);

                    if (treeView1.SelectedItem is TreeViewItem selectedItem &&
                        selectedItem.Tag is MAVLink.MAVLinkMessage selectedMsg &&
                        selectedMsg.msgid == message.msgid &&
                        selectedMsg.sysid == message.sysid &&
                        selectedMsg.compid == message.compid)
                    {
                        UpdateMessageDetails(message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating TreeView: {ex.Message}");
            }
        }
    }

    private void SortTreeViewItems(ItemsControl parent)
    {
        // Get only message nodes (skip Vehicle and Component nodes)
        var items = parent.Items.Cast<TreeViewItem>()
            .OrderBy(item =>
            {
                // Get the text part without Hz and bps info
                if (item.Header is StackPanel sp &&
                    sp.Children.Count > 1 &&
                    sp.Children[1] is TextBlock tb)
                {
                    string text = tb.Text;
                    int bracketIndex = text.IndexOf('(');
                    if (bracketIndex > 0)
                    {
                        return text.Substring(0, bracketIndex).Trim();
                    }
                    return text;
                }
                return item.Header.ToString();
            })
            .ToList();

        parent.Items.Clear();
        foreach (var item in items)
        {
            parent.Items.Add(item);
        }
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
        UpdateStatistics();
    }

    private void UpdateUIState(bool isConnected)
    {
        ConnectionTypeComboBox.IsEnabled = !isConnected;
        SerialPanel.IsEnabled = !isConnected;
        NetworkPanel.IsEnabled = !isConnected;
    }

    private void UpdateSystemList()
    {
        var sysids = _mavInspector.SeenSysid();
        // Update UI with system IDs if needed
    }

    private void UpdateMessageDetails(MAVLink.MAVLinkMessage message)
    {
        _currentlyDisplayedMessage = message;
        detailsTextBox.UpdateMessageDetails(message);
    }

    private void UpdateStatistics()
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastRateUpdate).TotalSeconds;
        if (elapsed >= 1)
        {
            var rate = _messagesSinceLastUpdate / elapsed;
            statusMessages.Content = $"Messages: {_totalMessages}";
            statusRate.Content = $"Rate: {rate:F1} msg/s";
            _messagesSinceLastUpdate = 0;
            _lastRateUpdate = now;
        }
    }

    private void ConnectionTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionTypeComboBox.SelectedItem is string selectedType)
        {
            SerialPanel.Visibility = selectedType == "Serial" ? Visibility.Visible : Visibility.Collapsed;
            NetworkPanel.Visibility = selectedType != "Serial" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            lock (_treeUpdateLock)
            {
                // Geçici olarak yeni mesaj işlemeyi durdur
                var isConnected = _connectionManager.IsConnected;

                // UI elementlerini temizle
                Dispatcher.Invoke(() =>
                {
                    treeView1.Items.Clear();
                    detailsTextBox.Clear();
                    statusMessages.Content = "Messages: 0";
                    statusRate.Content = "Rate: 0 msg/s";
                });

                // Sayaçları sıfırla
                _totalMessages = 0;
                _messagesSinceLastUpdate = 0;
                _currentlyDisplayedMessage = null;

                // Collection'ları temizle
                _messageUpdateInfo.Clear();
                _messageColors.Clear();
                _lastUpdateTime.Clear();

                // PacketInspector'ı sıfırla ama yapısını koru
                _mavInspector.Clear();

                // Eğer bağlantı aktifse, hemen yeni mesajları almaya başla
                if (isConnected)
                {
                    Dispatcher.Invoke(() =>
                    {
                        statusConnection.Content = "Connected - Receiving messages...";
                    });

                    // Mevcut bağlantıyı koru, mesaj almaya devam et
                    Debug.WriteLine("Connection active, continuing to receive messages...");
                }

                Debug.WriteLine("Reset completed - System ready for new messages");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during reset: {ex.Message}");
            MessageBox.Show("Reset operation encountered an error but connection is maintained.",
                           "Reset Warning",
                           MessageBoxButton.OK,
                           MessageBoxImage.Information);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _ = _connectionManager.DisposeAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _detailsUpdateTimer.Stop();
        base.OnClosing(e);
    }
}
