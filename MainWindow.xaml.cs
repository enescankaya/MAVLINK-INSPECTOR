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
using System.Windows.Input;

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
    private int UPDATE_INTERVAL_MS = 200;

    // Sınıf seviyesinde yeni değişken ekle
    private object? _selectedFieldItem = null;

    // Sınıf seviyesinde yeni değişkenler ekle
    private string _treeViewSearchText = "";
    private string _fieldsSearchText = "";
    private DateTime _lastTreeViewKeyPress = DateTime.MinValue;
    private DateTime _lastFieldsKeyPress = DateTime.MinValue;
    private const int SEARCH_TIMEOUT_MS = 300; // 1 saniye

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
        InitializeUpdateIntervalComboBox();
        // Timer'ları konfigüre et
        ConfigureUpdateTimers();
        InitializeSearchFeatures();
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

        ShowGCSTraffic.Checked += (s, e) =>
        {
            _connectionManager.OnMessageSent += HandleMessage;
            Dispatcher.BeginInvoke(RefreshTreeView);
        };

        ShowGCSTraffic.Unchecked += (s, e) =>
        {
            _connectionManager.OnMessageSent -= HandleMessage;
            RemoveGCSTrafficFromTreeView();
            ResetButton_Click(s, e);
        };

        // Başlangıçta GCS trafiğini dinlemeye başla
        _connectionManager.OnMessageReceived += HandleMessage;

        treeView1.SelectedItemChanged += TreeView_SelectedItemChanged;
    }

    // Yeni metodlar ekle
    private void RemoveGCSTrafficFromTreeView()
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                treeView1.Items.Clear(); // Tüm mesajları temizle
                RefreshTreeView(); // Mesajları yeniden yükle
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Remove GCS Traffic Error: {ex.Message}");
        }
    }

    private void RefreshTreeView()
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                var messages = _mavInspector.GetPacketMessages().ToList();
                foreach (var message in messages)
                {
                    var rate = _mavInspector.GetMessageRate(message.sysid, message.compid, message.msgid);
                    UpdateTreeViewForMessage(message, rate);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Refresh TreeView Error: {ex.Message}");
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
            await DisconnectAsync();
        }
    }

    private void HandleMessage(MAVLink.MAVLinkMessage message)
    {
        // GCS trafiği kontrolü
        if (message.sysid == 255 && !ShowGCSTraffic.IsChecked.GetValueOrDefault())
            return;

        // Mesajı ekle ama UI güncellemesini timer'a bırak
        _mavInspector.Add(message.sysid, message.compid, message.msgid, message, message.Length);
        Interlocked.Increment(ref _totalMessages);
        Interlocked.Increment(ref _messagesSinceLastUpdate);
    }

    private void ProcessMessage(MAVLink.MAVLinkMessage message)
    {
        try
        {
            // Tüm mesajları işle, hiç kontrol yapma
            _mavInspector.Add(message.sysid, message.compid, message.msgid, message, message.Length);

            Interlocked.Increment(ref _totalMessages);
            Interlocked.Increment(ref _messagesSinceLastUpdate);

            lock (_messageQueue)
            {
                _messageQueue.Enqueue(message);
            }

            if (IsSelectedMessage(message))
            {
                Dispatcher.InvokeAsync(() => UpdateMessageDetails(message));
            }
        }
        catch
        {
            // Hata durumunda sessizce devam et
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
            // Benzersiz anahtar oluştur
            var messageKey = (uint)((message.sysid << 16) | (message.compid << 8) | message.msgid);
            var bps = _mavInspector.GetBps(message.sysid, message.compid, message.msgid);
            var header = FormatMessageHeader(message, rate, bps);

            var vehicleNode = GetOrCreateVehicleNode(message.sysid);
            var componentNode = GetOrCreateComponentNode(vehicleNode, message);
            var msgNode = componentNode.FindOrCreateChild(header, messageKey, message);

            // Text ve renk güncellemesi
            if (msgNode.Header is StackPanel sp && sp.Children.Count > 1 &&
                sp.Children[1] is TextBlock tb)
            {
                tb.Text = header;

                // Yeni rengi hesapla
                var color = GenerateMessageColor(rate);
                tb.Foreground = new SolidColorBrush(color);
            }

            // DataContext'i güncelle
            msgNode.DataContext = message;

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

    private void UpdateNodeColor(TreeViewItem node, byte sysid, byte compid, uint msgid, double rate)
    {
        // Mesaj için benzersiz bir anahtar oluştur
        var messageKey = (uint)((sysid << 16) | (compid << 8) | msgid);

        // Rengi yeniden hesapla veya cache'den al
        if (!_messageColors.TryGetValue(messageKey, out var color))
        {
            color = GenerateMessageColor(rate);
            _messageColors[messageKey] = color;
        }

        // Node'un rengini güncelle
        if (node.Header is StackPanel sp && sp.Children.Count > 1 &&
            sp.Children[1] is TextBlock tb)
        {
            tb.Foreground = new SolidColorBrush(color);
        }
    }

    private Color GenerateMessageColor(double rate)
    {
        try
        {
            // Hz değerlerine göre doğrudan RGB karşılıkları
            if (rate < 0.1) // Çok düşük Hz - Kırmızı
            {
                return Color.FromRgb(255, 0, 0); // Saf kırmızı
            }
            else if (rate < 1.0) // 0.1 - 1 Hz arası - Kırmızıdan sarıya geçiş
            {
                return Color.FromRgb(255, 128, 0); // Turuncu (kırmızıdan sarıya geçiş)
            }
            else if (rate < 10.0) // 1 - 10 Hz arası - Sarıya geçiş
            {
                return Color.FromRgb(255, 255, 0); // Saf sarı
            }
            else if (rate < 32.0) // 10 - 32 Hz arası - Yeşile geçiş
            {
                return Color.FromRgb(128, 255, 0); // Yeşilimsi sarı
            }
            else if (rate < 48.0) // 32 - 48 Hz arası - Açık yeşile geçiş
            {
                return Color.FromRgb(0, 255, 0); // Saf yeşil
            }
            else if (rate < 64.0) // 48 - 64 Hz arası - Açık maviye geçiş
            {
                return Color.FromRgb(0, 255, 255); // Açık mavi (camgöbeği)
            }
            else if (rate < 100.0) // 64 - 100 Hz arası - Maviye geçiş
            {
                return Color.FromRgb(0, 128, 255); // Parlak mavi
            }
            else // 100+ Hz - Açık mavi ile mor arasında
            {
                return Color.FromRgb(220, 220, 220); //  tonları
            }
        }
        catch
        {
            return Color.FromRgb(255, 255, 255); // Hata durumunda parlak beyaz döndür
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
        try
        {
            if (message == null)
            {
                // Clear all fields when no message is selected
                Dispatcher.Invoke(() =>
                {
                    // Clear existing fields
                    msgTypeText.Text = string.Empty;
                    sysidText.Text = string.Empty;
                    compidText.Text = string.Empty;
                    msgidText.Text = string.Empty;
                    lengthText.Text = string.Empty;

                    // Clear new fields
                    msgTypeNameText.Text = string.Empty;
                    crc16Text.Text = string.Empty;
                    seqText.Text = string.Empty;
                    headerText.Text = string.Empty;
                    isMavlink2Text.Text = string.Empty;

                    fieldsListView.Items.Clear();
                });
                _selectedFieldItem = null;
                return;
            }

            _currentlyDisplayedMessage = message;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Update existing header information
                    msgTypeText.Text = message.GetType().Name;
                    sysidText.Text = message.sysid.ToString();
                    compidText.Text = message.compid.ToString();
                    msgidText.Text = message.msgid.ToString();
                    lengthText.Text = message.Length.ToString();

                    // Update new header information
                    msgTypeNameText.Text = message.msgtypename;
                    crc16Text.Text = message.crc16.ToString("X4"); // Hexadecimal format
                    seqText.Text = message.seq.ToString();
                    headerText.Text = message.header.ToString();
                    if (message.ismavlink2)
                    {
                        isMavlink2Text.Text = "Mavlink V2";
                    }
                    else
                    {
                        isMavlink2Text.Text = "Mavlink V1";
                    }

                    // Clear existing fields
                    var currentSelection = _selectedFieldItem;
                    fieldsListView.Items.Clear();

                    var fields = message.data.GetType().GetFields();
                    // Add each payload field to the ListView
                    foreach (var field in fields)
                    {
                        var value = field.GetValue(message.data);
                        var typeName = field.FieldType.ToString();

                        var item = new
                        {
                            Field = field.Name,
                            Value = value,
                            Type = typeName
                        };

                        fieldsListView.Items.Add(item);

                        // Eğer bu önceden seçili olan item ise, tekrar seç
                        if (currentSelection != null &&
                            item.Field == (currentSelection.GetType().GetProperty("Field")?.GetValue(currentSelection) as string))
                        {
                            fieldsListView.SelectedItem = item;
                            _selectedFieldItem = item;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Show error in the fields list
                    fieldsListView.Items.Clear();
                    fieldsListView.Items.Add(new
                    {
                        Name = "Error",
                        Value = ex.Message,
                        Type = "Exception"
                    });
                }
            });
        }
        catch (Exception)
        {
            // Log or handle the outer exception if needed
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
                    UpdateMessageDetails(message);
                }
            }
        }
        catch (Exception ex)
        {
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

                    // Renk cache'ini temizle
                    _messageColors.Clear();
                    treeView1.Items.Clear();
                    // Clear all message details fields
                    msgTypeText.Text = string.Empty;
                    sysidText.Text = string.Empty;
                    compidText.Text = string.Empty;
                    msgidText.Text = string.Empty;
                    lengthText.Text = string.Empty;
                    msgTypeNameText.Text = string.Empty;
                    crc16Text.Text = string.Empty;
                    seqText.Text = string.Empty;
                    headerText.Text = string.Empty;
                    isMavlink2Text.Text = string.Empty;
                    fieldsListView.Items.Clear(); statusMessagesText.Text = "Messages: 0";
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
                }

            }
        }
        catch (Exception ex)
        {
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
        // TreeView update timer
        _treeViewUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
        _treeViewUpdateTimer.Tick += async (s, e) =>
        {
            var now = DateTime.Now;
            if ((now - _lastTreeViewUpdate).TotalMilliseconds >= UPDATE_INTERVAL_MS)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateTreeView();
                }, DispatcherPriority.Background);
                _lastTreeViewUpdate = now;
            }
        };

        // Details update timer
        _detailsUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
        _detailsUpdateTimer.Tick += (s, e) =>
        {
            var now = DateTime.Now;
            if ((now - _lastDetailsUpdate).TotalMilliseconds >= UPDATE_INTERVAL_MS)
            {
                UpdateMessageDetailsIfNeeded();
                _lastDetailsUpdate = now;
            }
        };

        // Timer'ları başlat
        _treeViewUpdateTimer.Start();
        _detailsUpdateTimer.Start();
    }

    private void UpdateTreeView()
    {
        try
        {
            lock (_treeUpdateLock)
            {
                var messages = _mavInspector.GetPacketMessages()
                    .Where(m => ShowGCSTraffic.IsChecked.GetValueOrDefault() || m.sysid != 255)
                    .ToList();

                foreach (var message in messages)
                {
                    var rate = _mavInspector.GetMessageRate(message.sysid, message.compid, message.msgid);
                    UpdateTreeViewForMessage(message, rate);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateTreeView error: {ex.Message}");
        }
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
    private void InitializeUpdateIntervalComboBox()
    {
        var intervals = new[]
        {
            new { Display = "100", Value = 100 },
            new { Display = "200", Value = 200 },
            new { Display = "500", Value = 500 },
            new { Display = "1000", Value = 1000 }
        };

        UpdateIntervalComboBox.ItemsSource = intervals;
        UpdateIntervalComboBox.SelectedIndex = 1; // 200ms varsayılan
    }
    private void UpdateIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UpdateIntervalComboBox.SelectedValue is int newInterval)
        {
            // Timer'ları durdur
            _treeViewUpdateTimer.Stop();
            _detailsUpdateTimer.Stop();

            // Yeni interval değerini ayarla
            UPDATE_INTERVAL_MS = newInterval;

            // Timer'ları yeni interval ile yapılandır
            _treeViewUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
            _detailsUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);

            // Zaman damgalarını sıfırla
            _lastTreeViewUpdate = DateTime.Now;
            _lastDetailsUpdate = DateTime.Now;

            // Timer'ları yeniden başlat
            _treeViewUpdateTimer.Start();
            _detailsUpdateTimer.Start();
        }
    }

    // ListView'in SelectionChanged event handler'ını ekle
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        fieldsListView.SelectionChanged += FieldsListView_SelectionChanged;
    }

    private void FieldsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (fieldsListView.SelectedItem != null)
        {
            _selectedFieldItem = fieldsListView.SelectedItem;
        }
    }

    private void InitializeSearchFeatures()
    {
        // TreeView için klavye olayını ekle
        treeView1.KeyDown += TreeView_KeyDown;
        // ListView için klavye olayını ekle
        fieldsListView.KeyDown += FieldsListView_KeyDown;
    }

    private void TreeView_KeyDown(object sender, KeyEventArgs e)
    {
        if (!char.IsLetterOrDigit((char)KeyInterop.VirtualKeyFromKey(e.Key)))
            return;

        var now = DateTime.Now;
        if ((now - _lastTreeViewKeyPress).TotalMilliseconds > SEARCH_TIMEOUT_MS)
        {
            _treeViewSearchText = "";
        }

        _treeViewSearchText += e.Key.ToString().ToLower();
        _lastTreeViewKeyPress = now;

        SearchInTreeView(_treeViewSearchText);
        e.Handled = true;
    }

    private void SearchInTreeView(string searchText)
    {
        if (string.IsNullOrEmpty(searchText))
            return;

        var allItems = GetAllTreeViewItems(treeView1);
        var matchingItem = allItems.FirstOrDefault(item =>
        {
            if (item.Header is StackPanel sp && sp.Children.Count > 1 &&
                sp.Children[1] is TextBlock tb)
            {
                return tb.Text.ToLower().StartsWith(searchText);
            }
            return false;
        });

        if (matchingItem != null)
        {
            matchingItem.IsSelected = true;
            matchingItem.BringIntoView();
        }
    }

    private IEnumerable<TreeViewItem> GetAllTreeViewItems(ItemsControl parent)
    {
        for (int i = 0; i < parent.Items.Count; i++)
        {
            var item = parent.Items[i] as TreeViewItem;
            if (item == null) continue;

            yield return item;

            foreach (var child in GetAllTreeViewItems(item))
            {
                yield return child;
            }
        }
    }

    private void FieldsListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (!char.IsLetterOrDigit((char)KeyInterop.VirtualKeyFromKey(e.Key)))
            return;

        var now = DateTime.Now;
        if ((now - _lastFieldsKeyPress).TotalMilliseconds > SEARCH_TIMEOUT_MS)
        {
            _fieldsSearchText = "";
        }

        _fieldsSearchText += e.Key.ToString().ToLower();
        _lastFieldsKeyPress = now;

        SearchInFieldsList(_fieldsSearchText);
        e.Handled = true;
    }

    private void SearchInFieldsList(string searchText)
    {
        if (string.IsNullOrEmpty(searchText))
            return;

        var items = fieldsListView.Items.Cast<dynamic>();
        var matchingItem = items.FirstOrDefault(item =>
            item.Field.ToString().ToLower().StartsWith(searchText));

        if (matchingItem != null)
        {
            fieldsListView.SelectedItem = matchingItem;
            fieldsListView.ScrollIntoView(matchingItem);
        }
    }
} // MainWindow class ends here