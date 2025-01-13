using MavlinkInspector.Connections;
using System.IO.Ports;
using System.IO;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Controls.Primitives;

namespace MavlinkInspector;

// Enum tanımlamaları ekle
public enum ConnectionType
{
    Serial,
    TCP,
    UDP
}

public enum UpdateInterval
{
    Fast = 100,
    Normal = 200,
    Slow = 500,
    VerySlow = 1000
}

public partial class MainWindow : Window
{
    // Sabit değerler için const tanımlamaları
    private const int MAX_CACHED_COLORS = 1000;
    private const int MAX_MESSAGE_QUEUE_SIZE = 5000;
    private const int CLEANUP_INTERVAL_MS = 30000; // 30 saniye

    // Mevcut field tanımlamaları yerine daha optimize versiyonları
    private readonly ConcurrentDictionary<uint, Color> _messageColors = new(Environment.ProcessorCount, MAX_CACHED_COLORS);
    private readonly ConcurrentQueue<MAVLink.MAVLinkMessage> _messageQueue = new();
    private readonly DispatcherTimer _cleanupTimer = new();

    private readonly PacketInspector<MAVLink.MAVLinkMessage> _mavInspector = new();
    private readonly DispatcherTimer _timer = new();
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

    // Sınıf seviyesinde yeni bir değişken ekleyin
    private List<MAVLink.MAVLinkMessage> _selectedMessages = new();

    private HashSet<(byte sysid, byte compid, uint msgid, string field)> _selectedFieldsForGraph = new();

    private class MessageUpdateInfo
    {
        public DateTime LastUpdate { get; set; }
        public double LastRate { get; set; }
        public string LastHeader { get; set; } = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the MainWindow class.
    /// </summary>
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
        InitializeGraphingFeatures();
        InitializeGraphButton();

        // Cleanup timer'ı başlat
        _cleanupTimer.Interval = TimeSpan.FromMilliseconds(CLEANUP_INTERVAL_MS);
        _cleanupTimer.Tick += CleanupTimer_Tick;
        _cleanupTimer.Start();
    }

    /// <summary>
    /// Initializes the UI components.
    /// </summary>
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

    /// <summary>
    /// Sets up the timer for periodic updates.
    /// </summary>
    private void SetupTimer()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    /// <summary>
    /// Sets up message handling for incoming MAVLink messages.
    /// </summary>
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

    /// <summary>
    /// Removes GCS traffic from the TreeView.
    /// </summary>
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
        }
    }

    /// <summary>
    /// Refreshes the TreeView with the latest messages.
    /// </summary>
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
        }
    }

    /// <summary>
    /// Sets up the timer for updating message details.
    /// </summary>
    private void SetupDetailsTimer()
    {
        _detailsUpdateTimer.Interval = TimeSpan.FromMilliseconds(100); // 10 Hz update rate
        _detailsUpdateTimer.Tick += DetailsTimer_Tick;
        _detailsUpdateTimer.Start();
    }

    /// <summary>
    /// Handles the tick event of the details update timer.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void DetailsTimer_Tick(object sender, EventArgs e)
    {
        if (_currentlyDisplayedMessage != null)
        {
            UpdateMessageDetailsIfNeeded(_currentlyDisplayedMessage);
        }
    }

    /// <summary>
    /// Updates message details if needed.
    /// </summary>
    /// <param name="message">The MAVLink message.</param>
    private void UpdateMessageDetailsIfNeeded(MAVLink.MAVLinkMessage message)
    {
        if (_mavInspector.TryGetLatestMessage(message.sysid, message.compid, message.msgid, out var latestMessage))
        {
            UpdateMessageDetails(latestMessage);
        }
    }

    /// <summary>
    /// Handles the click event of the Connect button.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
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

    /// <summary>
    /// Connects to the MAVLink source asynchronously.
    /// </summary>
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

    /// <summary>
    /// Disconnects from the MAVLink source asynchronously.
    /// </summary>
    private async Task DisconnectAsync()
    {
        await _connectionManager.DisconnectAsync();
        ConnectButton.Content = "Connect";
        UpdateUIState(false);
        statusConnectionText.Text = "Disconnected";
    }

    /// <summary>
    /// Processes incoming MAVLink data asynchronously.
    /// </summary>
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

    /// <summary>
    /// Handles incoming MAVLink messages.
    /// </summary>
    /// <param name="message">The MAVLink message.</param>
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

    /// <summary>
    /// Processes a MAVLink message.
    /// </summary>
    /// <param name="message">The MAVLink message.</param>
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

    /// <summary>
    /// Updates the UI asynchronously with the given MAVLink message.
    /// </summary>
    /// <param name="message">The MAVLink message.</param>
    private async Task UpdateUIAsync(MAVLink.MAVLinkMessage message)
    {
        var rate = _mavInspector.GetMessageRate(message.sysid, message.compid, message.msgid);
        await Dispatcher.InvokeAsync(() =>
        {
            UpdateTreeViewForMessage(message, rate);
            CleanupOldMessages();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Cleans up old messages from the TreeView.
    /// </summary>
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

    /// <summary>
    /// Gets or creates a vehicle node in the TreeView.
    /// </summary>
    /// <param name="sysid">The system ID.</param>
    /// <returns>The TreeViewItem representing the vehicle node.</returns>
    private TreeViewItem GetOrCreateVehicleNode(byte sysid)
    {
        var header = $"Vehicle {sysid}";
        return treeView1.FindOrCreateChild(header, sysid);
    }

    /// <summary>
    /// Gets or creates a component node in the TreeView.
    /// </summary>
    /// <param name="vehicleNode">The vehicle node.</param>
    /// <param name="message">The MAVLink message.</param>
    /// <returns>The TreeViewItem representing the component node.</returns>
    private TreeViewItem GetOrCreateComponentNode(TreeViewItem vehicleNode, MAVLink.MAVLinkMessage message)
    {
        var header = $"Component {message.compid}";
        return vehicleNode.FindOrCreateChild(header, (message.sysid << 8) | message.compid);
    }

    /// <summary>
    /// Updates the TreeView for the given MAVLink message.
    /// </summary>
    /// <param name="message">The MAVLink message.</param>
    /// <param name="rate">The message rate.</param>
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
        }
    }

    /// <summary>
    /// Formats the message header for display in the TreeView.
    /// </summary>
    /// <param name="message">The MAVLink message.</param>
    /// <param name="rate">The message rate.</param>
    /// <param name="bps">The message bits per second.</param>
    /// <returns>The formatted message header.</returns>
    private string FormatMessageHeader(MAVLink.MAVLinkMessage message, double rate, double bps)
    {
        if (bps >= 1000000)
            return $"{message.msgtypename} ({rate:F1} Hz, {bps / 1000000:F1} Mbps)";
        if (bps >= 1000)
            return $"{message.msgtypename} ({rate:F1} Hz, {bps / 1000:F1} kbps)";
        return $"{message.msgtypename} ({rate:F1} Hz, {bps:F0} bps)";
    }

    /// <summary>
    /// Updates the color of a TreeView node.
    /// </summary>
    /// <param name="node">The TreeView node.</param>
    /// <param name="sysid">The system ID.</param>
    /// <param name="compid">The component ID.</param>
    /// <param name="msgid">The message ID.</param>
    /// <param name="rate">The message rate.</param>
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

    /// <summary>
    /// Generates a color based on the message rate.
    /// </summary>
    /// <param name="rate">The message rate.</param>
    /// <returns>The generated color.</returns>
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

    /// <summary>
    /// Handles the tick event of the main timer.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void Timer_Tick(object sender, EventArgs e)
    {
        UpdateStatistics();
    }

    /// <summary>
    /// Updates the UI state based on the connection status.
    /// </summary>
    /// <param name="isConnected">Indicates whether the connection is active.</param>
    private void UpdateUIState(bool isConnected)
    {
        ConnectionTypeComboBox.IsEnabled = !isConnected;
        SerialPanel.IsEnabled = !isConnected;
        NetworkPanel.IsEnabled = !isConnected;
    }

    /// <summary>
    /// Updates the system list in the UI.
    /// </summary>
    private void UpdateSystemList()
    {
        var sysids = _mavInspector.SeenSysid();
        // Update UI with system IDs if needed
    }

    /// <summary>
    /// Updates the message details in the UI.
    /// </summary>
    /// <param name="message">The MAVLink message.</param>
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
                return;
            }

            _currentlyDisplayedMessage = message;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    // Update header information
                    msgTypeText.Text = message.GetType().Name;
                    sysidText.Text = message.sysid.ToString();
                    compidText.Text = message.compid.ToString();
                    msgidText.Text = message.msgid.ToString();
                    lengthText.Text = message.Length.ToString();
                    msgTypeNameText.Text = message.msgtypename;
                    crc16Text.Text = message.crc16.ToString("X4");
                    seqText.Text = message.seq.ToString();
                    headerText.Text = message.header.ToString();
                    isMavlink2Text.Text = message.ismavlink2 ? "Mavlink V2" : "Mavlink V1";

                    // Mevcut seçili öğeleri kaydet
                    var selectedFields = fieldsListView.SelectedItems.Cast<dynamic>()
                        .Select(item => item.Field.ToString())
                        .ToList();

                    // Mevcut seçili alanları kaydet
                    var currentSelections = new HashSet<string>();
                    foreach (var field in _selectedFieldsForGraph)
                    {
                        if (field.sysid == message.sysid &&
                            field.compid == message.compid &&
                            field.msgid == message.msgid)
                        {
                            currentSelections.Add(field.field);
                        }
                    }

                    fieldsListView.Items.Clear();

                    var fields = message.data.GetType().GetFields();
                    foreach (var field in fields)
                    {
                        var value = field.GetValue(message.data);
                        var typeName = field.FieldType.ToString().Replace("System.", ""); // System. kısmını kaldır

                        var item = new
                        {
                            Field = field.Name,
                            Value = value,
                            Type = typeName
                        };

                        fieldsListView.Items.Add(item);

                        // Eğer bu alan daha önce seçiliyse, tekrar seç
                        if (selectedFields.Contains(field.Name))
                        {
                            fieldsListView.SelectedItems.Add(item);
                        }

                        // Eğer bu alan daha önce seçiliyse, tekrar seç
                        if (currentSelections.Contains(field.Name))
                        {
                            fieldsListView.SelectedItems.Add(item);
                        }
                    }
                }
                catch (Exception ex)
                {
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

    /// <summary>
    /// Handles the selection changed event of the TreeView.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        try
        {
            _selectedMessages.Clear();

            // Tüm seçili öğeleri bul
            var selectedItems = GetAllSelectedTreeViewItems(treeView1);

            foreach (var item in selectedItems)
            {
                if (item.DataContext is MAVLink.MAVLinkMessage message)
                {
                    _selectedMessages.Add(message);
                }
            }

            // En az bir mesaj seçiliyse ilk mesajın detaylarını göster
            if (_selectedMessages.Any())
            {
                UpdateMessageDetails(_selectedMessages.First());
            }
        }
        catch (Exception ex)
        {
        }
    }

    // Yeni yardımcı metod ekleyin
    private List<TreeViewItem> GetAllSelectedTreeViewItems(TreeView treeView)
    {
        var selectedItems = new List<TreeViewItem>();
        var queue = new Queue<ItemsControl>();
        queue.Enqueue(treeView);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var item in current.Items)
            {
                if (item is TreeViewItem tvi)
                {
                    if (tvi.IsSelected)
                    {
                        selectedItems.Add(tvi);
                    }
                    queue.Enqueue(tvi);
                }
            }
        }

        return selectedItems;
    }

    /// <summary>
    /// Updates the statistics in the UI.
    /// </summary>
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

    /// <summary>
    /// Handles the selection changed event of the ConnectionTypeComboBox.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void ConnectionTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionTypeComboBox.SelectedItem is string selectedType)
        {
            SerialPanel.Visibility = selectedType == "Serial" ? Visibility.Visible : Visibility.Collapsed;
            NetworkPanel.Visibility = selectedType != "Serial" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Handles the click event of the Reset button.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
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

    /// <summary>
    /// Handles the closing event of the window.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _ = _connectionManager.DisposeAsync();
    }

    /// <summary>
    /// Handles the closing event of the window.
    /// </summary>
    /// <param name="e">The event arguments.</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        _treeViewUpdateTimer.Stop();
        _detailsUpdateTimer.Stop();
        _isDisposed = true;
        base.OnClosing(e);
    }

    /// <summary>
    /// Determines if the given message is the currently selected message.
    /// </summary>
    /// <param name="message">The MAVLink message.</param>
    /// <returns>True if the message is selected, otherwise false.</returns>
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

    /// <summary>
    /// Configures the update timers for the TreeView and message details.
    /// </summary>
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

    /// <summary>
    /// Updates the TreeView with the latest messages.
    /// </summary>
    private void UpdateTreeView()
    {
        try
        {
            lock (_treeUpdateLock)
            {
                var messages = _mavInspector.GetPacketMessages()
                    .Where(m => ShowGCSTraffic.IsChecked.GetValueOrDefault() || m.sysid != 255)
                    .Take(MAX_TREE_ITEMS) // Maksimum öğe sayısını sınırla
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
        }
    }

    /// <summary>
    /// Updates the message details if needed.
    /// </summary>
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

    /// <summary>
    /// Initializes the update interval ComboBox.
    /// </summary>
    private void InitializeUpdateIntervalComboBox()
    {
        // Direkt olarak ComboBoxItem'lar oluştur
        UpdateIntervalComboBox.Items.Add(new ComboBoxItem { Content = "100" });
        UpdateIntervalComboBox.Items.Add(new ComboBoxItem { Content = "200" });
        UpdateIntervalComboBox.Items.Add(new ComboBoxItem { Content = "500" });
        UpdateIntervalComboBox.Items.Add(new ComboBoxItem { Content = "1000" });
        UpdateIntervalComboBox.SelectedIndex = 1; // 200ms varsayılan
    }

    /// <summary>
    /// Handles the selection changed event of the UpdateIntervalComboBox.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void UpdateIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UpdateIntervalComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content.ToString(), out int newInterval))
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

    /// <summary>
    /// Handles the source initialized event of the window.
    /// </summary>
    /// <param name="e">The event arguments.</param>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        fieldsListView.SelectionChanged += FieldsListView_SelectionChanged;
    }

    /// <summary>
    /// Handles the selection changed event of the fields ListView.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void FieldsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Birden fazla seçime izin ver
        var selectedItems = fieldsListView.SelectedItems.Cast<dynamic>().ToList();
        foreach (var item in selectedItems)
        {
            _selectedFieldItem = item;
        }

        // Yeni seçilen öğeleri ekle
        foreach (dynamic item in e.AddedItems)
        {
            if (_currentlyDisplayedMessage != null)
            {
                var fieldToAdd = (
                    _currentlyDisplayedMessage.sysid,
                    _currentlyDisplayedMessage.compid,
                    _currentlyDisplayedMessage.msgid,
                    item.Field.ToString()
                );

                if (IsNumericType(item.Type.ToString()))
                {
                    _selectedFieldsForGraph.Add(((byte sysid, byte compid, uint msgid, string field))fieldToAdd);
                }
            }
        }

        // Seçimi kaldırılan öğeleri çıkar
        foreach (dynamic item in e.RemovedItems)
        {
            if (_currentlyDisplayedMessage != null)
            {
                var fieldToRemove = (
                    _currentlyDisplayedMessage.sysid,
                    _currentlyDisplayedMessage.compid,
                    _currentlyDisplayedMessage.msgid,
                    item.Field.ToString()
                );
                _selectedFieldsForGraph.Remove(((byte sysid, byte compid, uint msgid, string field))fieldToRemove);
            }
        }

        // Graph butonunu güncelle
        _graphButton!.IsEnabled = _selectedFieldsForGraph.Count > 0;
    }

    /// <summary>
    /// Initializes the search features for the TreeView and ListView.
    /// </summary>
    private void InitializeSearchFeatures()
    {
        // TreeView için klavye olayını ekle
        treeView1.KeyDown += TreeView_KeyDown;
        // ListView için klavye olayını ekle
        fieldsListView.KeyDown += FieldsListView_KeyDown;
    }

    /// <summary>
    /// Handles the key down event of the TreeView.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
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

    /// <summary>
    /// Searches for the given text in the TreeView.
    /// </summary>
    /// <param name="searchText">The search text.</param>
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

    /// <summary>
    /// Gets all TreeView items recursively.
    /// </summary>
    /// <param name="parent">The parent ItemsControl.</param>
    /// <returns>An enumerable of TreeViewItem.</returns>
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

    /// <summary>
    /// Handles the key down event of the fields ListView.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
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

    /// <summary>
    /// Searches for the given text in the fields ListView.
    /// </summary>
    /// <param name="searchText">The search text.</param>
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

    private void InitializeGraphingFeatures()
    {
        // Enable multiple selection
        fieldsListView.SelectionMode = SelectionMode.Extended;

        // Add context menu
        var contextMenu = new ContextMenu();
        var graphMenuItem = new MenuItem { Header = "Graph It" };
        graphMenuItem.Click += GraphMenuItem_Click;
        contextMenu.Items.Add(graphMenuItem);
        fieldsListView.ContextMenu = contextMenu;
    }

    private void GraphMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMessages.Count == 0 || fieldsListView.SelectedItems.Count == 0)
            return;

        var fieldsToGraph = new List<(byte sysid, byte compid, uint msgid, string field)>();
        var invalidFields = new List<string>();

        foreach (dynamic selectedField in fieldsListView.SelectedItems)
        {
            string fieldName = selectedField.Field;
            var fieldType = selectedField.Type.ToString();

            if (IsNumericType(fieldType))
            {
                foreach (var message in _selectedMessages)
                {
                    fieldsToGraph.Add((
                        message.sysid,
                        message.compid,
                        message.msgid,
                        fieldName
                    ));
                }
            }
            else
            {
                invalidFields.Add($"{fieldName} ({fieldType})");
            }
        }

        if (invalidFields.Any())
        {
            MessageBox.Show(
                $"The following fields cannot be graphed because they are not numeric:\n\n{string.Join("\n", invalidFields)}",
                "Invalid Fields",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (fieldsToGraph.Count > 0)
        {
            var graphWindow = new GraphWindow(_mavInspector, fieldsToGraph);
            graphWindow.Show();
        }
    }

    // CTRL tuşu ile çoklu seçim için event handler ekle
    private void TreeViewItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            var item = sender as TreeViewItem;
            if (item != null)
            {
                item.IsSelected = !item.IsSelected;
                e.Handled = true;
            }
        }
    }

    // IsNumericType metodunu güncelle
    private bool IsNumericType(string typeName)
    {
        return typeName.Contains("Int") ||
               typeName.Contains("Float") ||
               typeName.Contains("Double") ||
               typeName.Contains("Decimal") ||
               typeName.Contains("Single") || // System.Single için ekle
               typeName == "System.Single" ||
               typeName == "System.Byte";   // Tam tip adı kontrolü
    }

    private void InitializeGraphButton()
    {

        _graphButton.Click += GraphButton_Click;
        // Button'u StatusBar'a ekle
        var separator = new Separator();
        statusBar.Items.Add(separator);
        statusBar.Items.Add(new StatusBarItem { Content = _graphButton });
    }

    private void GraphButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFieldsForGraph.Count > 0)
        {
            var graphWindow = new GraphWindow(_mavInspector, _selectedFieldsForGraph);
            graphWindow.Show();

            // Graph açıldıktan sonra seçimleri temizle
            _selectedFieldsForGraph.Clear();
            _graphButton!.IsEnabled = false;

            // ListView seçimlerini temizle
            fieldsListView.SelectedItems.Clear();
        }
    }

    /// <summary>
    /// Bellek temizleme işlemini gerçekleştirir
    /// </summary>
    private void CleanupTimer_Tick(object sender, EventArgs e)
    {
        try
        {
            var now = DateTime.Now;

            // Eski mesaj renklerini temizle
            foreach (var key in _messageColors.Keys)
            {
                if (_messageColors.Count > MAX_CACHED_COLORS)
                {
                    _messageColors.TryRemove(key, out _);
                }
            }

            // Message queue'yu temizle
            while (_messageQueue.Count > MAX_MESSAGE_QUEUE_SIZE)
            {
                _messageQueue.TryDequeue(out _);
            }

            // Eski update bilgilerini temizle
            var outdatedKeys = _messageUpdateInfo.Where(x =>
                (now - x.Value.LastUpdate).TotalSeconds > 60).Select(x => x.Key).ToList();

            foreach (var key in outdatedKeys)
            {
                _messageUpdateInfo.TryRemove(key, out _);
            }
        }
        catch
        {
            // Temizleme hatası kritik değil, sessizce devam et
        }
    }

    /// <summary>
    /// Mesaj renk önbelleğini yönetir
    /// </summary>
    private Color GetMessageColor(uint messageKey, double rate)
    {
        return _messageColors.GetOrAdd(messageKey, _ => GenerateMessageColor(rate));
    }
} // MainWindow class ends here