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
    private ConcurrentDictionary<uint, MessageUpdateInfo> _messageUpdateInfo = new();
    private readonly object _treeUpdateLock = new();
    private MAVLink.MAVLinkMessage? _currentlyDisplayedMessage;
    private const int MESSAGE_BUFFER_SIZE = 1024 * 16;
    private const int UI_UPDATE_THRESHOLD = 100; // ms
    private const int MAX_TREE_ITEMS = 1800;
    private DateTime _lastUIUpdate = DateTime.MinValue;
    private bool _isDisposed;

    // Eklenen yeni sabitler
    private const int UI_BATCH_SIZE = 10;
    private const int UI_UPDATE_DELAY_MS = 50;
    private readonly Queue<MAVLink.MAVLinkMessage> _messageQueue = new();

    // Yeni timer değişkenleri ekle
    private readonly DispatcherTimer _treeViewUpdateTimer = new();
    private readonly DispatcherTimer _detailsUpdateTimer = new();
    private DateTime _lastTreeViewUpdate = DateTime.MinValue;
    private DateTime _lastDetailsUpdate = DateTime.MinValue;
    private const int UPDATE_INTERVAL_MS = 200;

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

        // Timer'ları konfigüre et
        ConfigureUpdateTimers();
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

        // GCS traffic checkbox event handler
        ShowGCSTraffic.Checked += (s, e) =>
        {
            _connectionManager.OnMessageReceived += HandleMessage;
            _connectionManager.OnMessageSent += HandleMessage;
            RefreshTreeView(); // TreeView'i yenile
        };

        ShowGCSTraffic.Unchecked += (s, e) =>
        {
            _connectionManager.OnMessageReceived -= HandleMessage;
            _connectionManager.OnMessageSent -= HandleMessage;
            RemoveGCSTrafficFromTreeView(); // GCS trafiğini kaldır
            ResetButton_Click(s,e);
        };

        treeView1.SelectedItemChanged += TreeView_SelectedItemChanged;
    }

    // Yeni metodlar ekle
    private void RemoveGCSTrafficFromTreeView()
    {
        try
        {
            var itemsToRemove = new List<TreeViewItem>();

            // Tüm Vehicle node'larını kontrol et
            foreach (TreeViewItem vehicleNode in treeView1.Items)
            {
                if (vehicleNode.Tag is byte sysid && sysid == 255)
                {
                    itemsToRemove.Add(vehicleNode);
                    continue;
                }

                // Component node'larını kontrol et
                var componentItemsToRemove = new List<TreeViewItem>();
                foreach (TreeViewItem componentNode in vehicleNode.Items)
                {
                    if (componentNode.Tag is int tag && (tag >> 8) == 255)
                    {
                        componentItemsToRemove.Add(componentNode);
                    }
                }

                // Component node'larını kaldır
                foreach (var componentNode in componentItemsToRemove)
                {
                    componentNode.Items.Remove(componentNode);
                }
            }

            // Vehicle node'larını kaldır
            foreach (var item in itemsToRemove)
            {
                treeView1.Items.Remove(item);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error removing GCS traffic: {ex.Message}");
        }
    }

    private void RefreshTreeView()
    {
        try
        {
            // Tüm mesajları yeniden yükle
            var messages = _mavInspector.GetPacketMessages().ToList();
            foreach (var message in messages)
            {
                if (ShowGCSTraffic.IsChecked == true || message.sysid != 255)
                {
                    var rate = _mavInspector.GetMessageRate(message.sysid, message.compid, message.msgid);
                    UpdateTreeViewForMessage(message, rate);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error refreshing TreeView: {ex.Message}");
        }
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
            statusConnectionText.Text = "Connected";

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
        statusConnectionText.Text = "Disconnected";
    }

    private async Task ProcessIncomingDataAsync()
    {
        var mavlinkParse = new MAVLink.MavlinkParse();
        var messageQueue = new ConcurrentQueue<MAVLink.MAVLinkMessage>();
        var buffer = new List<byte>();

        try
        {
            // Debug için log ekle
            Debug.WriteLine("Starting message processing...");

            await foreach (var data in _connectionManager.DataChannel.ReadAllAsync())
            {
                buffer.AddRange(data);

                while (buffer.Count >= 8) // Minimum MAVLink message size
                {
                    try
                    {
                        using var ms = new MemoryStream(buffer.ToArray());
                        var packet = mavlinkParse.ReadPacket(ms);

                        if (packet == null)
                        {
                            buffer.RemoveAt(0);
                            continue;
                        }

                        Debug.WriteLine($"Received message: SysID={packet.sysid}, CompID={packet.compid}, MsgID={packet.msgid}");

                        // İşleme için UI thread'e gönder
                        await Dispatcher.InvokeAsync(() =>
                        {
                            ProcessMessage(packet);
                        });

                        // Başarıyla okunan veriyi buffer'dan kaldır
                        var bytesRead = (int)ms.Position;
                        buffer.RemoveRange(0, bytesRead);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error parsing packet: {ex.Message}");
                        buffer.RemoveAt(0);
                    }
                }

                // Buffer temizleme
                if (buffer.Count > MESSAGE_BUFFER_SIZE)
                {
                    buffer.RemoveRange(0, buffer.Count - MESSAGE_BUFFER_SIZE);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProcessIncomingDataAsync error: {ex.Message}");
            await DisconnectAsync();
        }
    }

    private void HandleMessage(MAVLink.MAVLinkMessage message)
    {
        if (ShowGCSTraffic.IsChecked.GetValueOrDefault() || message.sysid != 255)
        {
            ProcessMessage(message);
        }
    }

    private void ProcessMessage(MAVLink.MAVLinkMessage message)
    {
        try
        {
            // GCS trafiği kontrolü
            if (message.sysid == 255 && !ShowGCSTraffic.IsChecked.GetValueOrDefault())
                return;

            _mavInspector.Add(message.sysid, message.compid, message.msgid, message, message.Length);

            Interlocked.Increment(ref _totalMessages);
            Interlocked.Increment(ref _messagesSinceLastUpdate);

            // Message queue'ya ekle
            lock (_messageQueue)
            {
                _messageQueue.Enqueue(message);
            }

            // Eğer bu mesaj şu an seçili olan mesajsa, detayları güncelle
            if (IsSelectedMessage(message))
            {
                Dispatcher.InvokeAsync(() => UpdateMessageDetails(message));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProcessMessage error: {ex.Message}");
        }
    }

    private async Task UpdateUIFromQueueAsync(ConcurrentQueue<MAVLink.MAVLinkMessage> messageQueue)
    {
        while (!_isDisposed)
        {
            if (messageQueue.TryDequeue(out var message))
            {
                await ProcessMessageAsync(message);
            }
            else
            {
                await Task.Delay(1); // CPU kullanımını azalt
            }
        }
    }

    private async Task ProcessMessageAsync(MAVLink.MAVLinkMessage message)
    {
        if (!ShowGCSTraffic.IsChecked.GetValueOrDefault() && message.sysid == 255)
            return;

        _mavInspector.Add(message.sysid, message.compid, message.msgid, message, message.Length);

        Interlocked.Increment(ref _totalMessages);
        Interlocked.Increment(ref _messagesSinceLastUpdate);

        var now = DateTime.UtcNow;
        if ((now - _lastUIUpdate).TotalMilliseconds >= UI_UPDATE_THRESHOLD)
        {
            _lastUIUpdate = now;
            await UpdateUIAsync(message);
        }
    }

    private async Task UpdateUIAsync(MAVLink.MAVLinkMessage message)
    {
        var rate = _mavInspector.GetMessageRate(message.sysid, message.compid, message.msgid);
        await Dispatcher.InvokeAsync(() =>
        {
            UpdateTreeViewForMessage(message, rate);
            CleanupOldMessages();
        }, DispatcherPriority.Background);
    }

    private void CleanupOldMessages()
    {
        // TreeView'daki eski mesajları temizle
        foreach (TreeViewItem vehicleNode in treeView1.Items)
        {
            foreach (TreeViewItem componentNode in vehicleNode.Items)
            {
                var messages = componentNode.Items.Cast<TreeViewItem>().ToList();
                if (messages.Count > MAX_TREE_ITEMS)
                {
                    var toRemove = messages.Count - MAX_TREE_ITEMS;
                    for (int i = 0; i < toRemove; i++)
                    {
                        componentNode.Items.RemoveAt(0);
                    }
                }
            }
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
        try
        {
            var messageKey = (uint)((message.sysid << 16) | (message.compid << 8) | message.msgid);
            var bps = _mavInspector.GetBps(message.sysid, message.compid, message.msgid);

            // Debug için mesaj bilgilerini yazdır
            Debug.WriteLine($"Updating TreeView - Msg: {message.msgtypename}, Rate: {rate:F1} Hz, BPS: {bps:F0}");

            var header = FormatMessageHeader(message, rate, bps);
            var vehicleNode = GetOrCreateVehicleNode(message.sysid);
            var componentNode = GetOrCreateComponentNode(vehicleNode, message);
            var msgNode = componentNode.FindOrCreateChild(header, message.msgid, message);  // message'ı DataContext olarak geçir

            // Header'ı güncelle ama DataContext'i koru
            if (msgNode.Header is StackPanel sp && sp.Children.Count > 1 &&
                sp.Children[1] is TextBlock tb)
            {
                tb.Text = header;
            }

            msgNode.DataContext = message;  // DataContext'i güncelle

            // Node'u renklendir
            UpdateNodeColor(msgNode, message.msgid);

            // Seçili mesajı güncelle
            if (IsSelectedMessage(message))
            {
                UpdateMessageDetails(message);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateTreeViewForMessage error: {ex.Message}");
        }
    }

    private string FormatMessageHeader(MAVLink.MAVLinkMessage message, double rate, double bps)
    {
        if (bps >= 1000000)
            return $"{message.msgtypename} ({rate:F1} Hz, {bps / 1000000:F1} Mbps)";
        if (bps >= 1000)
            return $"{message.msgtypename} ({rate:F1} Hz, {bps / 1000:F1} kbps)";
        return $"{message.msgtypename} ({rate:F1} Hz, {bps:F0} bps)";
    }

    private void UpdateNodeColor(TreeViewItem node, uint msgId)
    {
        if (!_messageColors.TryGetValue(msgId, out var color))
        {
            color = GenerateMessageColor(msgId);
            _messageColors[msgId] = color;
        }

        if (node.Header is StackPanel sp && sp.Children.Count > 1 &&
            sp.Children[1] is TextBlock tb)
        {
            tb.Foreground = new SolidColorBrush(color);
        }
    }

    private Color GenerateMessageColor(uint msgId)
    {
        // Altın oran ile renk dağılımı (0-1 arası)
        var hue = (msgId * 0.618034f) % 1.0f;

        // Siyah üstünde açık ve rahatça görülebilir renkler için ayarlamalar
        const float saturation = 0.3f;  // Daha düşük doygunluk
        const float brightness = 0.99f; // Daha yüksek parlaklık

        // Renk çarkını 6 parçaya bölerek açık renkler üret
        int segment = Convert.ToInt32(Math.Floor(hue * 6));
        float f = hue * 6 - segment;

        // RGB bileşenlerini hesapla
        float p = brightness * (1 - saturation);
        float q = brightness * (1 - f * saturation);
        float t = brightness * (1 - (1 - f) * saturation);

        // RGB değerlerini byte'a dönüştür
        byte vb = (byte)(brightness * 255);
        byte pb = (byte)(p * 255);
        byte tb = (byte)(t * 255);
        byte qb = (byte)(q * 255);

        // Renk çarkının hangi segmentinde olduğumuza göre renk döndür
        return segment switch
        {
            0 => Color.FromRgb(vb, tb, pb),  // Kırmızı -> Sarı
            1 => Color.FromRgb(qb, vb, pb),  // Sarı -> Yeşil
            2 => Color.FromRgb(pb, vb, tb),  // Yeşil -> Cyan
            3 => Color.FromRgb(pb, qb, vb),  // Cyan -> Mavi
            4 => Color.FromRgb(tb, pb, vb),  // Mavi -> Magenta
            _ => Color.FromRgb(vb, pb, qb)   // Magenta -> Kırmızı
        };
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
        try
        {
            if (message == null)
            {
                detailsTextBox.Clear();
                return;
            }

            Debug.WriteLine($"Updating details for message: {message.msgtypename}");
            _currentlyDisplayedMessage = message;

            // Extensions.UpdateMessageDetails metodunu çağır
            detailsTextBox.Dispatcher.Invoke(() =>
            {
                try
                {
                    detailsTextBox.UpdateMessageDetails(message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error updating message details: {ex.Message}");
                    detailsTextBox.Text = $"Error displaying message details: {ex.Message}";
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in UpdateMessageDetails: {ex.Message}");
        }
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        try
        {
            if (e.NewValue is TreeViewItem item)
            {
                // TreeViewItem'ın DataContext'ini kontrol et (mesaj burada saklanıyor)
                if (item.DataContext is MAVLink.MAVLinkMessage message)
                {
                    Debug.WriteLine($"Selected message: {message.msgtypename}");
                    UpdateMessageDetails(message);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in TreeView selection: {ex.Message}");
        }
    }

    private void UpdateStatistics()
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastRateUpdate).TotalSeconds;
        if (elapsed >= 1)
        {
            var rate = _messagesSinceLastUpdate / elapsed;
            statusMessagesText.Text = $"Messages: {_totalMessages}";
            statusRateText.Text = $"Rate: {rate:F1} msg/s";
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
                    statusMessagesText.Text = "Messages: 0";
                    statusRateText.Text = "Rate: 0 msg/s";
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
                        statusConnectionText.Text = "Connected - Receiving messages...";
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
        _treeViewUpdateTimer.Stop();
        _detailsUpdateTimer.Stop();
        _isDisposed = true;
        base.OnClosing(e);
    }

    private bool IsSelectedMessage(MAVLink.MAVLinkMessage message)
    {
        if (treeView1.SelectedItem is not TreeViewItem selectedItem)
            return false;

        if (selectedItem.Tag is not MAVLink.MAVLinkMessage selectedMsg)
            return false;

        return selectedMsg.msgid == message.msgid &&
               selectedMsg.sysid == message.sysid &&
               selectedMsg.compid == message.compid;
    }

    private void ConfigureUpdateTimers()
    {
        // TreeView güncellemesi için timer
        _treeViewUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
        _treeViewUpdateTimer.Tick += (s, e) => UpdateTreeView();
        _treeViewUpdateTimer.Start();

        // Message details güncellemesi için timer
        _detailsUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
        _detailsUpdateTimer.Tick += (s, e) => UpdateMessageDetailsIfNeeded();
        _detailsUpdateTimer.Start();
    }

    private void UpdateTreeView()
    {
        if ((DateTime.Now - _lastTreeViewUpdate).TotalMilliseconds < UPDATE_INTERVAL_MS)
            return;

        lock (_treeUpdateLock)
        {
            // Tüm mesajları tek seferde güncelle
            var messages = _mavInspector.GetPacketMessages().ToList();
            foreach (var message in messages)
            {
                var rate = _mavInspector.GetMessageRate(message.sysid, message.compid, message.msgid);
                UpdateTreeViewForMessage(message, rate);
            }
        }

        _lastTreeViewUpdate = DateTime.Now;
    }

    private void UpdateMessageDetailsIfNeeded()
    {
        if (_currentlyDisplayedMessage == null ||
            (DateTime.Now - _lastDetailsUpdate).TotalMilliseconds < UPDATE_INTERVAL_MS)
            return;

        if (_mavInspector.TryGetLatestMessage(
            _currentlyDisplayedMessage.sysid,
            _currentlyDisplayedMessage.compid,
            _currentlyDisplayedMessage.msgid,
            out var latestMessage))
        {
            UpdateMessageDetails(latestMessage);
        }

        _lastDetailsUpdate = DateTime.Now;
    }
} // MainWindow class ends here