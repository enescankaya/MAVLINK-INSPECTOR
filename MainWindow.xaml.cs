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
using System.Windows.Data;
using MavlinkInspector.Controls;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using MavlinkInspector.Services;

using static MAVLink;
using System.Reflection;

namespace MavlinkInspector;

/// <summary>
/// Specifies the sorting order for messages in the TreeView
/// </summary>
public enum SortOrder
{
    Name,
    Hz,
    Bps
}

/// <summary>
/// Represents a range of message rates with associated priority
/// </summary>
public class MessageRateRange
{
    /// <summary>
    /// Gets or sets the minimum value of the range
    /// </summary>
    public double Min { get; set; }

    /// <summary>
    /// Gets or sets the maximum value of the range
    /// </summary>
    public double Max { get; set; }

    /// <summary>
    /// Gets or sets the priority of this range
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Determines if a given value falls within this range
    /// </summary>
    /// <param name="value">The value to check</param>
    /// <returns>True if the value is within range, false otherwise</returns>
    public bool IsInRange(double value) => value >= Min && value < Max;
}

public partial class MainWindow : Window
{
    // Sabit değerler için const tanımlamaları
    /// <summary>
    /// Maximum number of colors to cache for message visualization
    /// </summary>
    private const int MAX_CACHED_COLORS = 1000;
    /// <summary>
    /// Maximum size of the message queue for processing
    /// </summary>
    private const int MAX_MESSAGE_QUEUE_SIZE = 5000;
    /// <summary>
    /// Interval in milliseconds between cleanup operations
    /// </summary>
    private const int CLEANUP_INTERVAL_MS = 30000; // 30 saniye

    // Mevcut field tanımlamaları yerine daha optimize versiyonları
    /// <summary>
    /// Cache of colors used for message visualization
    /// </summary>
    private readonly ConcurrentDictionary<uint, Color> _messageColors = new(Environment.ProcessorCount, MAX_CACHED_COLORS);
    /// <summary>
    /// Queue for storing incoming MAVLink messages
    /// </summary>
    private readonly ConcurrentQueue<MAVLink.MAVLinkMessage> _messageQueue = new();
    /// <summary>
    /// Timer for periodic cleanup operations
    /// </summary>
    private readonly DispatcherTimer _cleanupTimer = new();

    /// <summary>
    /// Inspector for processing MAVLink packets
    /// </summary>
    private readonly PacketInspector<MAVLink.MAVLinkMessage> _mavInspector = new();

    /// <summary>
    /// Timer for periodic updates
    /// </summary>
    private readonly DispatcherTimer _timer = new();

    /// <summary>
    /// Random number generator for various operations
    /// </summary>
    private readonly Random _random = new();

    /// <summary>
    /// Manager for handling MAVLink connections
    /// </summary>
    private ConnectionManager _connectionManager = new();

    /// <summary>
    /// Total number of messages processed
    /// </summary>
    private int _totalMessages;
    private DateTime _lastRateUpdate = DateTime.Now;
    private int _messagesSinceLastUpdate;
    private ConcurrentDictionary<uint, DateTime> _lastUpdateTime = new();
    private readonly object _updateLock = new();
    private ConcurrentDictionary<uint, MessageUpdateInfo> _messageUpdateInfo = new();
    private readonly object _treeUpdateLock = new();
    private MAVLink.MAVLinkMessage? _currentlyDisplayedMessage;
    private const int MESSAGE_BUFFER_SIZE = 1024 * 16;
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

    private SortOrder currentSortOrder = SortOrder.Name;
    private readonly List<MessageRateRange> rateRanges = new()
    {
        new MessageRateRange { Min = 100, Max = double.MaxValue, Priority = 0 },
        new MessageRateRange { Min = 50, Max = 100, Priority = 1 },
        new MessageRateRange { Min = 20, Max = 50, Priority = 2 },
        new MessageRateRange { Min = 10, Max = 20, Priority = 3 },
        new MessageRateRange { Min = 5, Max = 10, Priority = 4 },
        new MessageRateRange { Min = 1, Max = 5, Priority = 5 },
        new MessageRateRange { Min = 0.1, Max = 1, Priority = 6 },
        new MessageRateRange { Min = 0, Max = 0.1, Priority = 7 }
    };

    /// <summary>
    /// Stores information about message updates
    /// </summary>
    private class MessageUpdateInfo
    {
        /// <summary>
        /// Gets or sets the timestamp of the last update
        /// </summary>
        public DateTime LastUpdate { get; set; }
        /// <summary>
        /// Gets or sets the last recorded message rate
        /// </summary>
        public double LastRate { get; set; }
        /// <summary>
        /// Gets or sets the last message header
        /// </summary>
        public string LastHeader { get; set; } = string.Empty;
    }

    private MessageDetailsControl defaultDetailsControl;

    // Yeni field ekleyin
    private ObservableCollection<SelectedFieldInfo> selectedFieldsPanelItems;

    /// <summary>
    /// Represents selected field information for visualization
    /// </summary>
    public class SelectedFieldInfo
    {
        /// <summary>
        /// Gets or sets the system ID
        /// </summary>
        public byte SysId { get; set; }
        /// <summary>
        /// Gets or sets the component ID
        /// </summary>
        public byte CompId { get; set; }
        /// <summary>
        /// Gets or sets the message ID
        /// </summary>
        public uint MsgId { get; set; }
        /// <summary>
        /// Gets or sets the field name
        /// </summary>
        public string Field { get; set; }
        /// <summary>
        /// Gets or sets the display text for the field
        /// </summary>
        public string DisplayText { get; set; }
        /// <summary>
        /// Gets or sets the source tab containing this field
        /// </summary>
        public TabItem SourceTab { get; set; } // Yeni özellik ekle
    }

    private MAVLink.message_info[] _defaultMessageInfos;
    private MAVLink.message_info[] _uploadedMessageInfos;
    private Assembly _loadedCustomDll;

    // Yeni batch update değişkeni
    private readonly DispatcherTimer _batchUpdateTimer;
    private readonly Queue<Action> _pendingUpdates = new();

    /// <summary>
    /// Initializes a new instance of the MainWindow class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        defaultDetailsControl = new MessageDetailsControl(_mavInspector);
        defaultDetailsControl.FieldsSelectedForGraph += OnFieldsSelectedForGraph;
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

        selectedFieldsPanelItems = new ObservableCollection<SelectedFieldInfo>();
        selectedFieldsPanel.ItemsSource = selectedFieldsPanelItems;

        // Batch update timer'ı başlat
        _batchUpdateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UI_UPDATE_DELAY_MS)
        };
        _batchUpdateTimer.Tick += ProcessPendingUpdates;
        _batchUpdateTimer.Start();
    }

    private void OnFieldsSelectedForGraph(object? sender, IEnumerable<(byte sysid, byte compid, uint msgid, string field)> fields)
    {
        var graphWindow = new GraphWindow(_mavInspector, fields.ToList());
        graphWindow.Show();
    }

    /// <summary>
    /// Initializes the UI components.
    /// </summary>
    private void InitializeUI()
    {
        // ComboBox'ı elle doldurmayacağız çünkü XAML'da tanımladık
        PortComboBox.ItemsSource = SerialPort.GetPortNames();
        BaudRateComboBox.ItemsSource = new[] { 9600, 19200, 38400, 57600, 115200 };
        BaudRateComboBox.SelectedValue = 57600;

        if (PortComboBox.Items.Count > 0)
            PortComboBox.SelectedIndex = 0;

        // Varsayılan olarak GCS UI seçili olsun
        ConnectionTypeComboBox.SelectedIndex = 0;
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
            ResetButton_Click(null, null);
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
        _detailsUpdateTimer.Interval = TimeSpan.FromMilliseconds(200); // 10 Hz update rate
        _detailsUpdateTimer.Tick += DetailsTimer_Tick;
        _detailsUpdateTimer.Start();
    }

    /// <summary>
    /// Handles the tick event of the details update timer.
    /// </summary>
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
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
    /// <param="message">The MAVLink message.</param>
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
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
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
            if (ConnectionTypeComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                string selectedType = selectedItem.Content.ToString();

                if (selectedType == "GCS UI")
                {
                    // GCS UI seçildiğinde sadece mesaj göster
                    MessageBoxService.ShowInfo("GCS UI Connection is not available for standard mode please support", this);
                    return;
                }

                var parameters = new ConnectionParameters
                {
                    ConnectionType = selectedType,
                    Port = PortComboBox.SelectedItem?.ToString(),
                    BaudRate = (int?)BaudRateComboBox.SelectedValue ?? 57600,
                    IpAddress = IpAddressTextBox.Text,
                    NetworkPort = int.Parse(PortTextBox.Text)
                };

                await _connectionManager.ConnectAsync(parameters);
                ConnectButton.Content = "Disconnect";
                UpdateUIState(true);
                statusConnectionText.Text = "Connected";
            }
        }
        catch (Exception ex)
        {
            MessageBoxService.ShowError($"Connection failed: {ex.Message}", this);
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
                            //ProcessMessage(packet);
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
    /// <param="message">The MAVLink message.</param>
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
    /// <param="message">The MAVLink message.</param>
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
    /// <param="message">The MAVLink message.</param>
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
    /// <param="sysid">The system ID.</param>
    /// <returns>The TreeViewItem representing the vehicle node.</returns>
    private TreeViewItem GetOrCreateVehicleNode(byte sysid)
    {
        var header = $"Vehicle {sysid}";
        return treeView1.FindOrCreateChild(header, sysid);
    }

    /// <summary>
    /// Gets or creates a component node in the TreeView.
    /// </summary>
    /// <param="vehicleNode">The vehicle node.</param>
    /// <param="message">The MAVLink message.</param>
    /// <returns>The TreeViewItem representing the component node.</returns>
    private TreeViewItem GetOrCreateComponentNode(TreeViewItem vehicleNode, MAVLink.MAVLinkMessage message)
    {
        var header = $"Component {message.compid}";
        return vehicleNode.FindOrCreateChild(header, (message.sysid << 8) | message.compid);
    }

    /// <summary>
    /// Updates the TreeView for the given MAVLink message.
    /// </summary>
    /// <param="message">The MAVLink message.</param>
    /// <param="rate">The message rate.</param>
    private void UpdateTreeViewForMessage(MAVLink.MAVLinkMessage message, double rate)
    {
        _pendingUpdates.Enqueue(() => {
            try
            {

                // Benzersiz anahtar oluştur
                var messageKey = (uint)((message.sysid << 16) | (message.compid << 8) | message.msgid);
                var bps = _mavInspector.GetBps(message.sysid, message.compid, message.msgid);
                var header = FormatMessageHeader(message, rate, bps);

                // Seçili öğeyi kaydet
                var selectedItem = treeView1.SelectedItem as TreeViewItem;
                var selectedMessage = selectedItem?.DataContext as MAVLink.MAVLinkMessage;

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

                if (currentSortOrder != SortOrder.Name)
                {
                    var treeViewState = new Dictionary<string, bool>();
                    SaveTreeViewState(treeView1, treeViewState);

                    SortTreeView();

                    // Seçili öğeyi geri yükle
                    if (selectedMessage != null)
                    {
                        RestoreSelection(selectedMessage);
                    }

                    RestoreTreeViewState(treeView1, treeViewState);
                }
            }
            catch (Exception ex)
            {
            }
        });
    }

    /// <summary>
    /// Formats the message header for display in the TreeView.
    /// </summary>
    /// <param="message">The MAVLink message.</param>
    /// <param="rate">The message rate.</param>
    /// <param="bps">The message bits per second.</param>
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
    /// <param="node">The TreeView node.</param>
    /// <param="sysid">The system ID.</param>
    /// <param="compid">The component ID.</param>
    /// <param="msgid">The message ID.</param>
    /// <param="rate">The message rate.</param>
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
    /// Generates a color for message visualization based on message rate
    /// </summary>
    /// <param name="rate">The message rate in Hz</param>
    /// <returns>A Color object representing the visualization color</returns>
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
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
    private void Timer_Tick(object sender, EventArgs e)
    {
        UpdateStatistics();
    }

    /// <summary>
    /// Updates the UI state based on the connection status.
    /// </summary>
    /// <param="isConnected">Indicates whether the connection is active.</param>
    private void UpdateUIState(bool isConnected)
    {
        ConnectionTypeComboBox.IsEnabled = !isConnected;
        SerialPanel.IsEnabled = !isConnected;
        NetworkPanel.IsEnabled = !isConnected;

        // Update the appearance of disabled controls
        UpdateControlAppearance(ConnectionTypeComboBox);
        UpdateControlAppearance(PortComboBox);
        UpdateControlAppearance(BaudRateComboBox);
        UpdateControlAppearance(IpAddressTextBox);
        UpdateControlAppearance(PortTextBox);
    }

    private void UpdateControlAppearance(Control control)
    {
        if (control.IsEnabled)
        {
            control.Opacity = 1.0;
        }
        else
        {
            control.Opacity = 0.5;
        }
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
    /// <param="message">The MAVLink message.</param>
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
                    defaultTab.Header = message.msgtypename;
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

                        // Array tipinde değerleri işle
                        string displayValue;
                        if (value != null && field.FieldType.IsArray)
                        {
                            var array = value as Array;
                            displayValue = string.Join(", ", array.Cast<object>().Select(x => x?.ToString() ?? "null"));
                        }
                        else
                        {
                            displayValue = value?.ToString() ?? "null";
                        }

                        var item = new
                        {
                            Field = field.Name,
                            Value = displayValue,
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
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
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
        if (!_connectionManager.IsConnected)
        {
            statusMessagesText.Text = "Messages: 0";
            statusRateText.Text = "Rate: 0 msg/s";
            _messagesSinceLastUpdate = 0;
            return;
        }

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
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
    private void ConnectionTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionTypeComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            string selectedType = selectedItem.Content.ToString();

            // Tüm panelleri gizle
            SerialPanel.Visibility = Visibility.Collapsed;
            NetworkPanel.Visibility = Visibility.Collapsed;

            // Seçime göre ilgili paneli göster
            switch (selectedType)
            {
                case "GCS UI":
                    // GCS UI seçildiğinde hiçbir panel gösterilmeyecek
                    break;
                case "Serial":
                    SerialPanel.Visibility = Visibility.Visible;
                    break;
                case "TCP":
                case "UDP":
                    NetworkPanel.Visibility = Visibility.Visible;
                    break;
            }
        }
    }

    /// <summary>
    /// Handles the click event of the Reset button.
    /// </summary>
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            lock (_treeUpdateLock)
            {
                // Tüm ikincil tabları kapat
                var tabsToRemove = messageTabControl.Items.Cast<TabItem>()
                    .Where(tab => tab != defaultTab)
                    .ToList();

                foreach (var tab in tabsToRemove)
                {
                    if (tab.Tag is DispatcherTimer timer)
                    {
                        timer.Stop();
                    }
                    messageTabControl.Items.Remove(tab);
                }

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
                    defaultTab.Header = "Message Details";
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

                // Tüm seçimleri temizle
                ClearAllFields_Click(null, null);
            }
        }
        catch (Exception ex)
        {
            MessageBoxService.ShowWarning("Reset operation encountered an error but connection is maintained.", this);
        }
    }

    /// <summary>
    /// Handles the closing event of the window.
    /// </summary>
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _ = _connectionManager.DisposeAsync();
    }

    /// <summary>
    /// Handles the closing event of the window.
    /// </summary>
    /// <param="e">The event arguments.</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        foreach (TabItem tab in messageTabControl.Items)
        {
            if (tab.Content is MessageDetailsControl control)
            {
                control.StopUpdates();
            }
        }
        _treeViewUpdateTimer.Stop();
        _detailsUpdateTimer.Stop();
        _isDisposed = true;

        // Restore default message infos if needed
        if (_defaultMessageInfos != null)
        {
            RestoreDefaultMessageInfos();
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Determines if the given message is the currently selected message.
    /// </summary>
    /// <param="message">The MAVLink message.</param>
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
        UpdateIntervalComboBox.Items.Clear();
        var intervals = new[] { 100, 200, 500, 1000 }; // Değerler düzenlendi

        foreach (var interval in intervals)
        {
            UpdateIntervalComboBox.Items.Add(new ComboBoxItem
            {
                Content = interval.ToString(),
                Tag = interval
            });
        }

        // Varsayılan olarak 200ms seç
        UpdateIntervalComboBox.SelectedIndex = 1;
        UPDATE_INTERVAL_MS = 200;

        // Timer'ları hemen güncelle
        _treeViewUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
        _detailsUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
    }

    private void UpdateIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UpdateIntervalComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is int newInterval)
        {
            // Önce tüm timer'ları durdur
            _treeViewUpdateTimer?.Stop();
            _detailsUpdateTimer?.Stop();
            _timer?.Stop();

            // Yeni interval değerini ayarla
            UPDATE_INTERVAL_MS = newInterval;

            // Timer'ları yeni interval ile yapılandır ve başlat
            if (_treeViewUpdateTimer != null)
            {
                _treeViewUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
                _treeViewUpdateTimer.Start();
            }

            if (_detailsUpdateTimer != null)
            {
                _detailsUpdateTimer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
                _detailsUpdateTimer.Start();
            }

            if (_timer != null)
            {
                _timer.Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS);
                _timer.Start();
            }

            // Tüm açık sekmelerdeki kontrolleri güncelle
            foreach (TabItem tab in messageTabControl.Items)
            {
                if (tab.Content is MessageDetailsControl control)
                {
                    control.UpdateInterval = UPDATE_INTERVAL_MS;
                }
            }

            // Zaman damgalarını sıfırla
            _lastTreeViewUpdate = DateTime.Now;
            _lastDetailsUpdate = DateTime.Now;
            _lastUIUpdate = DateTime.Now;
        }
    }

    /// <summary>
    /// Handles the source initialized event of the window.
    /// </summary>
    /// <param="e">The event arguments.</param>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        fieldsListView.SelectionChanged += FieldsListView_SelectionChanged;

        // TreeView item'larına çift tıklama olayını ekle
        AddTreeViewItemDoubleClickHandler(treeView1);

        // TabControl'e orta tıklama olayını ekle
        messageTabControl.MouseDown += TabControl_MouseDown;
    }

    private void AddTreeViewItemDoubleClickHandler(TreeView treeView)
    {
        treeView.MouseRightButtonDown += (s, e) =>
        {
            var item = FindClickedItem(e.OriginalSource as DependencyObject);
            if (item?.DataContext is MAVLink.MAVLinkMessage message)
            {
                CreateNewMessageTab(message);
            }
        };
    }

    private TreeViewItem FindClickedItem(DependencyObject source)
    {
        while (source != null && !(source is TreeViewItem))
        {
            source = VisualTreeHelper.GetParent(source);
        }
        return source as TreeViewItem;
    }

    private void CreateNewMessageTab(MAVLink.MAVLinkMessage message)
    {
        var tabName = $"{message.msgtypename} ({message.sysid}:{message.compid})";

        // Check if tab already exists
        var existingTab = messageTabControl.Items.Cast<TabItem>()
            .FirstOrDefault(t => t.Header.ToString() == tabName);

        if (existingTab != null)
        {
            existingTab.IsSelected = true;
            return;
        }

        var newTab = new TabItem { Header = tabName };
        var detailsControl = new MessageDetailsControl(_mavInspector);
        detailsControl.SetMessage(message);
        detailsControl.FieldsSelectedForGraph += OnFieldsSelectedForGraph;
        detailsControl.UpdateInterval = UPDATE_INTERVAL_MS; // Update interval'i ayarla
        detailsControl.SelectionChanged += (s, fields) =>
        {
            // Herhangi bir sekmede seçim değiştiğinde graph butonunu güncelle
            UpdateGraphButtonState();
        };

        // Yeni event handler ekle
        detailsControl.SelectionChangedWithFields += (s, fields) =>
        {
            // Önce eski seçimleri temizle
            if (_currentlyDisplayedMessage != null &&
                _currentlyDisplayedMessage.sysid == message.sysid &&
                _currentlyDisplayedMessage.compid == message.compid &&
                _currentlyDisplayedMessage.msgid == message.msgid)
            {
                var oldFields = _selectedFieldsForGraph
                    .Where(f => f.sysid == message.sysid &&
                               f.compid == message.compid &&
                               f.msgid == message.msgid)
                    .ToList();

                foreach (var field in oldFields)
                {
                    _selectedFieldsForGraph.Remove(field);
                }
            }

            // Yeni seçimleri ekle
            foreach (var field in fields)
            {
                _selectedFieldsForGraph.Add(field);
            }

            UpdateSelectedFieldsPanel();
            UpdateGraphButtonState();
        };

        newTab.Content = detailsControl;
        newTab.Tag = detailsControl;

        messageTabControl.Items.Add(newTab);
        newTab.IsSelected = true;
    }

    private StackPanel CloneMessageDetailsPanel(MAVLink.MAVLinkMessage message)
    {
        var newPanel = new StackPanel { Margin = new Thickness(5) };

        // Header Section
        var headerBorder = new Border
        {
            Background = FindResource("ControlBackground") as Brush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 10),
            BorderBrush = FindResource("BorderColor") as Brush,
            BorderThickness = new Thickness(1)
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < 5; i++)
        {
            headerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        }

        // Add header fields (similar to main view)
        AddHeaderField(headerGrid, 0, 0, "Header:", message.header.ToString());
        AddHeaderField(headerGrid, 1, 0, "Length:", message.Length.ToString());
        AddHeaderField(headerGrid, 2, 0, "Sequence:", message.seq.ToString());
        AddHeaderField(headerGrid, 3, 0, "System ID:", message.sysid.ToString());
        AddHeaderField(headerGrid, 4, 0, "Component ID:", message.compid.ToString());
        AddHeaderField(headerGrid, 0, 2, "Message ID:", message.msgid.ToString());
        AddHeaderField(headerGrid, 1, 2, "Message Type:", message.GetType().Name);
        AddHeaderField(headerGrid, 2, 2, "Message Type Name:", message.msgtypename);
        AddHeaderField(headerGrid, 3, 2, "CRC16:", message.crc16.ToString("X4"));
        AddHeaderField(headerGrid, 4, 2, "MAVLink Version:", message.ismavlink2 ? "Mavlink V2" : "Mavlink V1");

        headerBorder.Child = headerGrid;
        newPanel.Children.Add(headerBorder);

        // Fields Section Title
        var fieldsTitle = new TextBlock
        {
            Text = "Message Fields",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(5),
            Foreground = FindResource("TextColor") as Brush,
            FontSize = 13
        };
        newPanel.Children.Add(fieldsTitle);

        // Fields ListView
        var newListView = new ListView
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(2),
            SelectionMode = SelectionMode.Extended // Çoklu seçime izin ver
        };

        // Create and apply the GridView with styled columns
        var gridView = new GridView();

        // Field Column
        var fieldColumn = new GridViewColumn
        {
            Header = "Field",
            Width = 140,
            CellTemplate = new DataTemplate
            {
                VisualTree = new FrameworkElementFactory(typeof(TextBlock)).With(tb =>
                {
                    tb.SetValue(TextBlock.TextProperty, new Binding("Field"));
                    tb.SetValue(TextBlock.ForegroundProperty, FindResource("TextColor") as Brush);
                    tb.SetValue(TextBlock.PaddingProperty, new Thickness(0, 10, 0, 0));
                    tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Bottom);
                })
            }
        };

        // Value Column
        var valueColumn = new GridViewColumn
        {
            Header = "Value",
            Width = 180,
            CellTemplate = new DataTemplate
            {
                VisualTree = new FrameworkElementFactory(typeof(TextBlock)).With(tb =>
                {
                    tb.SetValue(TextBlock.TextProperty, new Binding("Value"));
                    tb.SetValue(TextBlock.ForegroundProperty, FindResource("TextColor") as Brush);
                    tb.SetValue(TextBlock.PaddingProperty, new Thickness(0, 10, 0, 0));
                    tb.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
                    tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Bottom);
                })
            }
        };

        // Type Column
        var typeColumn = new GridViewColumn
        {
            Header = "Type",
            Width = 120,
            CellTemplate = new DataTemplate
            {
                VisualTree = new FrameworkElementFactory(typeof(TextBlock)).With(tb =>
                {
                    tb.SetValue(TextBlock.TextProperty, new Binding("Type"));
                    tb.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(160, 160, 160)));
                    tb.SetValue(TextBlock.PaddingProperty, new Thickness(0, 10, 0, 0));
                    tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Bottom);
                })
            }
        };

        // Apply header style to columns
        var headerStyle = new Style(typeof(GridViewColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(160, 160, 160))));
        headerStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 32d));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 0, 0, 0)));
        headerStyle.Setters.Add(new Setter(Control.TemplateProperty, CreateHeaderTemplate()));

        fieldColumn.HeaderContainerStyle = headerStyle;
        valueColumn.HeaderContainerStyle = headerStyle;
        typeColumn.HeaderContainerStyle = headerStyle;

        // Add columns to GridView
        gridView.Columns.Add(fieldColumn);
        gridView.Columns.Add(valueColumn);
        gridView.Columns.Add(typeColumn);
        newListView.View = gridView;

        // Apply item container style
        var itemContainerStyle = new Style(typeof(ListViewItem));
        itemContainerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        itemContainerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        itemContainerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, FindResource("BorderColor")));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 32d));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom));

        // Add triggers for mouse over and selection
        var mouseOverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        mouseOverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(62, 62, 66))));
        itemContainerStyle.Triggers.Add(mouseOverTrigger);

        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(9, 71, 113))));
        itemContainerStyle.Triggers.Add(selectedTrigger);

        newListView.ItemContainerStyle = itemContainerStyle;

        // Store the message reference in ListView's Tag
        newListView.Tag = message;

        // Add SelectionChanged handler
        newListView.SelectionChanged += (s, e) =>
        {
            foreach (dynamic item in e.AddedItems)
            {
                if (IsNumericType(item.Type.ToString()))
                {
                    _selectedFieldsForGraph.Add((
                        message.sysid,
                        message.compid,
                        message.msgid,
                        item.Field.ToString()
                    ));
                }
            }

            foreach (dynamic item in e.RemovedItems)
            {
                _selectedFieldsForGraph.Remove((
                    message.sysid,
                    message.compid,
                    message.msgid,
                    item.Field.ToString()
                ));
            }

            if (_graphButton != null)
            {
                _graphButton.IsEnabled = _selectedFieldsForGraph.Count > 0;
            }
        };

        // Add ListView to Border with same style as main view
        var listViewBorder = new Border
        {
            Background = FindResource("ControlBackground") as Brush,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 5, 0, 0),
            BorderBrush = FindResource("BorderColor") as Brush,
            BorderThickness = new Thickness(1),
            Child = newListView
        };

        newPanel.Children.Add(listViewBorder);
        newPanel.Tag = newListView;

        UpdateListViewFields(newListView, message);

        return newPanel;
    }

    private void AddHeaderField(Grid grid, int row, int column, string label, string value)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = FindResource("TextColor") as Brush,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, column);
        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, column + 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
    }

    private GridViewColumn CreateGridViewColumn(string header, double width)
    {
        // GridView header stilini ana pencereden kopyala
        var mainGridView = fieldsListView.View as GridView;
        var mainColumn = mainGridView.Columns.First(c => c.Header.ToString() == header);

        return new GridViewColumn
        {
            Header = header,
            Width = width,
            HeaderTemplate = mainColumn.HeaderTemplate,
            CellTemplate = mainColumn.CellTemplate,
            HeaderContainerStyle = mainColumn.HeaderContainerStyle
        };
    }

    private void UpdateListViewFields(ListView listView, MAVLink.MAVLinkMessage message)
    {
        // Mevcut seçili öğeleri kaydet
        var selectedItems = listView.SelectedItems.Cast<dynamic>()
            .Select(item => item.Field.ToString())
            .ToList();

        listView.Items.Clear();
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
            listView.Items.Add(item);

            // Eğer bu öğe daha önce seçiliyse, tekrar seç
            if (selectedItems.Contains(field.Name))
            {
                listView.SelectedItems.Add(item);
            }
        }
    }

    private void SetupTabUpdates(TabItem tab, MAVLink.MAVLinkMessage message)
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS)
        };

        timer.Tick += (s, e) =>
        {
            if (tab.IsSelected && _mavInspector.TryGetLatestMessage(message.sysid, message.compid, message.msgid, out var latestMessage))
            {
                var scrollViewer = tab.Content as ScrollViewer;
                var stackPanel = scrollViewer?.Content as StackPanel;

                // Header bilgilerini güncelle
                if (stackPanel?.Children[0] is Border headerBorder &&
                    headerBorder.Child is Grid headerGrid)
                {
                    UpdateHeaderFields(headerGrid, latestMessage);
                }

                // Fields listesini güncelle
                var listView = stackPanel?.Tag as ListView;
                if (listView != null)
                {
                    UpdateListViewFields(listView, latestMessage);
                }
            }
        };

        tab.Tag = timer;
        timer.Start();
    }

    private void UpdateHeaderFields(Grid headerGrid, MAVLink.MAVLinkMessage message)
    {
        UpdateHeaderValue(headerGrid, 0, 1, message.header.ToString());
        UpdateHeaderValue(headerGrid, 1, 1, message.Length.ToString());
        UpdateHeaderValue(headerGrid, 2, 1, message.seq.ToString());
        UpdateHeaderValue(headerGrid, 3, 1, message.sysid.ToString());
        UpdateHeaderValue(headerGrid, 4, 1, message.compid.ToString());
        UpdateHeaderValue(headerGrid, 0, 3, message.msgid.ToString());
        UpdateHeaderValue(headerGrid, 1, 3, message.GetType().Name);
        UpdateHeaderValue(headerGrid, 2, 3, message.msgtypename);
        UpdateHeaderValue(headerGrid, 3, 3, message.crc16.ToString("X4"));
        UpdateHeaderValue(headerGrid, 4, 3, message.ismavlink2 ? "Mavlink V2" : "Mavlink V1");
    }

    private void UpdateHeaderValue(Grid grid, int row, int column, string value)
    {
        var children = grid.Children.Cast<UIElement>()
            .Where(x => Grid.GetRow(x) == row && Grid.GetColumn(x) == column)
            .OfType<TextBlock>()
            .FirstOrDefault();

        if (children != null)
        {
            children.Text = value;
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var tabItem = button?.TemplatedParent as TabItem;

        if (tabItem != null && tabItem != defaultTab)
        {
            // Stop the update timer
            if (tabItem.Tag is DispatcherTimer timer)
            {
                timer.Stop();
            }

            messageTabControl.Items.Remove(tabItem);
        }
    }

    private void TabControl_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            var tabItem = GetTabItem(e.OriginalSource as DependencyObject);
            if (tabItem != null && tabItem != defaultTab)
            {
                CloseTab(tabItem);
                e.Handled = true;
            }
        }
    }

    private TabItem GetTabItem(DependencyObject source)
    {
        while (source != null && !(source is TabItem))
        {
            source = VisualTreeHelper.GetParent(source);
        }
        return source as TabItem;
    }

    private void CloseTab(TabItem tab)
    {
        if (tab.Tag is DispatcherTimer timer)
        {
            timer.Stop();
        }
        messageTabControl.Items.Remove(tab);
    }

    /// <summary>
    /// Handles the selection changed event of the fields ListView.
    /// </summary>
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
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

        // Panel'i güncelle
        UpdateSelectedFieldsPanel();
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
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
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
    /// <param="searchText">The search text.</param>
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
    /// <param="parent">The parent ItemsControl.</param>
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
    /// <param="sender">The event sender.</param>
    /// <param="e">The event arguments.</param>
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
    /// <param="searchText">The search text.</param>
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

        // Create context menu with modern style
        var contextMenu = new ContextMenu { Style = Application.Current.FindResource("ModernContextMenu") as Style };

        var graphMenuItem = new MenuItem
        {
            Header = "Graph Selected Fields",
            Style = Application.Current.FindResource("ModernMenuItem") as Style,
            Icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,8 L4,4 L8,8 M4,4 L4,12"), // Simple graph icon
                Stroke = (Brush)Application.Current.FindResource("TextColor"),
                StrokeThickness = 1.5,
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform
            }
        };

        graphMenuItem.Click += GraphMenuItem_Click;
        contextMenu.Items.Add(graphMenuItem);

        // Add separator
        contextMenu.Items.Add(new Separator { Style = Application.Current.FindResource("ModernSeparator") as Style });

        // Add "Copy Value" menu item
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
                    Clipboard.SetText(item.ToString());
                }
                catch { }
            }
        };

        contextMenu.Items.Add(copyValueMenuItem);

        fieldsListView.ContextMenu = contextMenu;
    }

    // Update the GraphMenuItem_Click method to show a visual feedback
    private void GraphMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // TreeView seçim kontrolünü kaldır

        // Seçili fieldları kontrol et
        if (fieldsListView.SelectedItems.Count == 0)
        {
            MessageBoxService.ShowWarning("Please select at least one field to graph.", this);
            return;
        }

        var fieldsToGraph = new List<(byte sysid, byte compid, uint msgid, string field)>();
        var invalidFields = new List<string>();

        // Seçili mesajı al
        if (_currentlyDisplayedMessage != null)
        {
            foreach (dynamic selectedField in fieldsListView.SelectedItems)
            {
                string fieldName = selectedField.Field;
                var fieldType = selectedField.Type.ToString();

                if (IsNumericType(fieldType))
                {
                    fieldsToGraph.Add((
                        _currentlyDisplayedMessage.sysid,
                        _currentlyDisplayedMessage.compid,
                        _currentlyDisplayedMessage.msgid,
                        fieldName
                    ));
                }
                else
                {
                    invalidFields.Add($"{fieldName} ({fieldType})");
                }
            }

            if (invalidFields.Any())
            {
                MessageBoxService.ShowWarning(
                    $"The following fields cannot be graphed because they are not numeric:\n\n{string.Join("\n", invalidFields)}",
                    this,
                    "Invalid Fields"
                );
            }

            if (fieldsToGraph.Count > 0)
            {
                var graphWindow = new GraphWindow(_mavInspector, fieldsToGraph);
                graphWindow.Show();
            }
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
        var allFieldsToGraph = new List<(byte sysid, byte compid, uint msgid, string field)>();

        // Ana sekmeden seçili alanları ekle
        if (_selectedFieldsForGraph.Count > 0)
        {
            allFieldsToGraph.AddRange(_selectedFieldsForGraph);
        }

        // Diğer sekmelerden seçili alanları ekle
        foreach (TabItem tab in messageTabControl.Items)
        {
            if (tab.Content is MessageDetailsControl control)
            {
                var selectedFields = control.GetSelectedFieldsForGraph();
                allFieldsToGraph.AddRange(selectedFields);
            }
        }

        if (allFieldsToGraph.Count > 0)
        {
            var graphWindow = new GraphWindow(_mavInspector, allFieldsToGraph);
            graphWindow.Show();

            // Seçimleri temizleme işlemini kaldır ve sadece Panel'i güncelle
            UpdateSelectedFieldsPanel();
        }
    }

    private void UpdateGraphButtonState()
    {
        bool hasSelectedFields = _selectedFieldsForGraph.Count > 0;

        // Diğer sekmelerdeki seçimleri kontrol et
        foreach (TabItem tab in messageTabControl.Items)
        {
            if (tab.Content is MessageDetailsControl control && control.HasSelectedFields())
            {
                hasSelectedFields = true;
                break;
            }
        }

        _graphButton!.IsEnabled = hasSelectedFields;
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

    private void AddGraphFeatures(ListView listView)
    {
        listView.SelectionMode = SelectionMode.Extended;

        var contextMenu = new ContextMenu { Style = fieldsListView.ContextMenu?.Style };
        var graphMenuItem = new MenuItem
        {
            Header = "Graph It",
            Style = fieldsListView.ContextMenu?.Items[0] is MenuItem mainMenuItem ?
                   mainMenuItem.Style :
                   Application.Current.FindResource("ModernMenuItem") as Style
        };

        graphMenuItem.Click += (s, e) => HandleGraphMenuItem_Click(listView);
        contextMenu.Items.Add(graphMenuItem);

        // Seçim değişikliği olayını ekle
        listView.SelectionChanged += (s, e) => HandleListViewSelectionChanged(listView, e);
    }

    private void HandleGraphMenuItem_Click(ListView listView)
    {
        if (listView.SelectedItems.Count == 0) return;

        var fieldsToGraph = new List<(byte sysid, byte compid, uint msgid, string field)>();
        var invalidFields = new List<string>();

        // Tag'den mesaj bilgisini al
        var message = listView.Tag as MAVLink.MAVLinkMessage;
        if (message == null) return;

        foreach (dynamic selectedField in listView.SelectedItems)
        {
            if (IsNumericType(selectedField.Type.ToString()))
            {
                fieldsToGraph.Add((
                    message.sysid,
                    message.compid,
                    message.msgid,
                    selectedField.Field.ToString()
                ));
            }
            else
            {
                invalidFields.Add($"{selectedField.Field} ({selectedField.Type})");
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

    private void HandleListViewSelectionChanged(ListView listView, SelectionChangedEventArgs e)
    {
        var message = listView.Tag as MAVLink.MAVLinkMessage;
        if (message == null) return;

        try
        {
            foreach (dynamic item in e.AddedItems)
            {
                if (IsNumericType(item.Type.ToString()))
                {
                    _selectedFieldsForGraph.Add((
                        message.sysid,
                        message.compid,
                        message.msgid,
                        item.Field.ToString()
                    ));
                }
            }

            foreach (dynamic item in e.RemovedItems)
            {
                _selectedFieldsForGraph.Remove((
                    message.sysid,
                    message.compid,
                    message.msgid,
                    item.Field.ToString()
                ));
            }

            // Graph butonunu güncelle
            if (_graphButton != null)
            {
                _graphButton.IsEnabled = _selectedFieldsForGraph.Count > 0;
            }
        }
        catch (Exception ex)
        {
            // Hata durumunda sessizce devam et
        }
    }

    private ControlTemplate CreateHeaderTemplate()
    {
        var template = new ControlTemplate(typeof(GridViewColumnHeader));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, FindResource("BorderColor"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var textBlock = new FrameworkElementFactory(typeof(TextBlock));
        textBlock.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        textBlock.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        textBlock.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        textBlock.SetValue(TextBlock.FontSizeProperty, 12d);

        border.AppendChild(textBlock);
        template.VisualTree = border;

        return template;
    }

    private void SortByName_Click(object sender, RoutedEventArgs e)
    {
        currentSortOrder = SortOrder.Name;
        SortTreeView();
    }

    private void SortByHz_Click(object sender, RoutedEventArgs e)
    {
        currentSortOrder = SortOrder.Hz;
        SortTreeView();
    }

    private void SortByBps_Click(object sender, RoutedEventArgs e)
    {
        currentSortOrder = SortOrder.Bps;
        SortTreeView();
    }

    /// <summary>
    /// Updates the TreeView with messages matching the current sort order
    /// </summary>
    private void SortTreeView()
    {
        // Mevcut seçimi ve ağaç durumunu kaydet
        var selectedMessage = treeView1.SelectedItem as TreeViewItem;
        var treeViewState = new Dictionary<string, bool>();
        SaveTreeViewState(treeView1, treeViewState);

        // Her bir Vehicle ve Component node'u için sıralama yap
        foreach (TreeViewItem vehicleNode in treeView1.Items)
        {
            vehicleNode.IsExpanded = treeViewState.GetValueOrDefault(GetItemIdentifier(vehicleNode), vehicleNode.IsExpanded);

            foreach (TreeViewItem componentNode in vehicleNode.Items)
            {
                componentNode.IsExpanded = treeViewState.GetValueOrDefault(GetItemIdentifier(componentNode), componentNode.IsExpanded);

                var messages = componentNode.Items.Cast<TreeViewItem>().ToList();
                var sortedMessages = SortMessages(messages);

                componentNode.Items.Clear();
                foreach (var message in sortedMessages)
                {
                    componentNode.Items.Add(message);
                }
            }
        }
    }

    private List<TreeViewItem> SortMessages(List<TreeViewItem> messages)
    {
        // Seçili öğeyi kaydet
        var selectedMessage = treeView1.SelectedItem as TreeViewItem;

        var sortedMessages = messages
            .GroupBy(item =>
            {
                var header = (item.Header as StackPanel)?.Children[1] as TextBlock;
                if (header == null) return -1;

                var text = header.Text;
                double value = currentSortOrder switch
                {
                    SortOrder.Hz => ExtractRate(text),
                    SortOrder.Bps => ExtractBps(text),
                    _ => 0
                };
                return GetRangeIndex(value);
            })
            .OrderByDescending(g => g.Key) // Grupları sırala
            .SelectMany(group =>
                // Her grup içindeki öğeleri adlarına göre sırala
                group.OrderBy(item =>
                {
                    var header = (item.Header as StackPanel)?.Children[1] as TextBlock;
                    return ExtractMessageName(header?.Text ?? string.Empty);
                })
            )
            .ToList();

        // TreeView durumunu kaydet
        var treeViewState = new Dictionary<string, bool>();
        SaveTreeViewState(treeView1, treeViewState);

        // Seçili öğeyi tekrar seç
        if (selectedMessage != null)
        {
            foreach (var item in sortedMessages)
            {
                if (CompareMessages(item, selectedMessage))
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }

        // TreeView durumunu geri yükle
        RestoreTreeViewState(treeView1, treeViewState);

        return sortedMessages;
    }

    // Yeni metod: Değer aralığının indeksini döndürür
    private int GetRangeIndex(double value)
    {
        if (currentSortOrder == SortOrder.Bps)
        {
            // BPS için aralıklar

            if (value >= 100_000) return 5;
            if (value >= 10_000) return 4;
            if (value >= 5_000) return 3;
            if (value >= 1_000) return 1;
            return 0;                          // < 1 kbps
        }
        else // Hz için mevcut aralıklar
        {
            if (value >= 100) return 7;
            if (value >= 50) return 6;
            if (value >= 20) return 5;
            if (value >= 10) return 4;
            if (value >= 5) return 3;
            if (value >= 1) return 2;
            if (value >= 0.1) return 1;
            return 0;
        }
    }

    // Yeni metod: İki TreeViewItem'ın mesajlarını karşılaştırır
    private bool CompareMessages(TreeViewItem item1, TreeViewItem item2)
    {
        var msg1 = item1.DataContext as MAVLink.MAVLinkMessage;
        var msg2 = item2.DataContext as MAVLink.MAVLinkMessage;

        if (msg1 == null || msg2 == null)
            return false;

        return msg1.sysid == msg2.sysid &&
               msg1.compid == msg2.compid &&
               msg1.msgid == msg2.msgid;
    }

    private double ExtractBps(string text)
    {
        try
        {
            var match = Regex.Match(text, @"(\d+\.?\d*)\s*(M|k)?bps");
            if (!match.Success) return 0;

            double value = double.Parse(match.Groups[1].Value);
            string unit = match.Groups[2].Value.ToLower();

            return unit switch
            {
                "m" => value * 1_000_000,
                "k" => value * 1_000,
                _ => value
            };
        }
        catch
        {
            return 0;
        }
    }

    private string ExtractMessageName(string text)
    {
        var match = Regex.Match(text, @"^[A-Za-z_]+");
        return match.Success ? match.Value : text;
    }

    // ExtractRate metodunu ekle (diğer Extract metodlarının yanına)
    private double ExtractRate(string text)
    {
        var match = Regex.Match(text, @"(\d+\.?\d*)\s*Hz");
        return match.Success ? double.Parse(match.Groups[1].Value) : 0;
    }

    // Yardımcı metotlar ekle
    private void SaveTreeViewState(ItemsControl parent, Dictionary<string, bool> state)
    {
        foreach (var item in parent.Items)
        {
            if (item is TreeViewItem tvi)
            {
                string key = GetItemIdentifier(tvi);
                state[key] = tvi.IsExpanded;

                if (tvi.HasItems)
                {
                    SaveTreeViewState(tvi, state);
                }
            }
        }
    }

    private void RestoreTreeViewState(ItemsControl parent, Dictionary<string, bool> state)
    {
        foreach (var item in parent.Items)
        {
            if (item is TreeViewItem tvi)
            {
                string key = GetItemIdentifier(tvi);
                if (state.TryGetValue(key, out bool wasExpanded))
                {
                    // TreeViewItem'ın durumunu geri yükle
                    tvi.IsExpanded = wasExpanded;

                    if (tvi.HasItems)
                    {
                        // Alt öğelerin durumunu recursive olarak geri yükle
                        RestoreTreeViewState(tvi, state);
                    }
                }
            }
        }
    }

    private string GetItemIdentifier(TreeViewItem item)
    {
        if (item.DataContext is MAVLink.MAVLinkMessage msg)
        {
            return $"msg_{msg.sysid}_{msg.compid}_{msg.msgid}";
        }
        else if (item.Header is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
        {
            // Vehicle ve Component node'ları için özel tanımlayıcı
            return $"node_{tb.Text}";
        }
        return $"item_{item.GetHashCode()}";
    }

    private void RestoreSelection(MAVLink.MAVLinkMessage selectedMessage)
    {
        if (selectedMessage == null) return;

        foreach (TreeViewItem vehicleNode in treeView1.Items)
        {
            foreach (TreeViewItem componentNode in vehicleNode.Items)
            {
                var messageItem = componentNode.Items.Cast<TreeViewItem>()
                    .FirstOrDefault(item =>
                    {
                        var msg = item.DataContext as MAVLink.MAVLinkMessage;
                        return msg != null &&
                               msg.sysid == selectedMessage.sysid &&
                               msg.compid == selectedMessage.compid &&
                               msg.msgid == selectedMessage.msgid;
                    });

                if (messageItem != null)
                {
                    messageItem.IsSelected = true;
                    // BringIntoView çağrısını kaldırdık
                    return;
                }
            }
        }
    }
    private string GetMessageName(uint msgid)
    {
        // Önce mavlink inspector'dan mesajı almaya çalış
        var messages = _mavInspector.GetPacketMessages();
        var message = messages.FirstOrDefault(m => m.msgid == msgid);
        if (message != null)
        {
            return message.msgtypename;
        }

        // Eğer aktif mesaj bulunamazsa, MAVLINK_MESSAGE_INFOS'dan bul
        var messageInfo = MAVLink.MAVLINK_MESSAGE_INFOS.FirstOrDefault(info => info.msgid == msgid);
        return messageInfo.name;
    }
    private void UpdateSelectedFieldsPanel()
    {
        try
        {
            selectedFieldsPanelItems.Clear();

            // Sadece aktif seçimleri ekle
            var selectedFields = new HashSet<(byte sysid, byte compid, uint msgid, string field)>();

            // Ana sekmede seçili olanları ekle
            foreach (dynamic item in fieldsListView.SelectedItems)
            {
                if (_currentlyDisplayedMessage != null && IsNumericType(item.Type.ToString()))
                {
                    selectedFields.Add((
                        _currentlyDisplayedMessage.sysid,
                        _currentlyDisplayedMessage.compid,
                        _currentlyDisplayedMessage.msgid,
                        item.Field.ToString()
                    ));
                }
            }

            // Diğer sekmelerde seçili olanları ekle
            foreach (TabItem tab in messageTabControl.Items)
            {
                if (tab != defaultTab && tab.Content is MessageDetailsControl control)
                {
                    selectedFields.UnionWith(control.GetSelectedFieldsForGraph());
                }
            }

            // Selected fields paneline ekle
            foreach (var field in selectedFields)
            {
                var messageName = GetMessageName(field.msgid);
                selectedFieldsPanelItems.Add(new SelectedFieldInfo
                {
                    SysId = field.sysid,
                    CompId = field.compid,
                    MsgId = field.msgid,
                    Field = field.field,
                    DisplayText = $"{messageName}.{field.field} ({field.sysid}:{field.compid})",
                    SourceTab = GetSourceTab(field)
                });
            }

            // _selectedFieldsForGraph'ı güncelle
            _selectedFieldsForGraph = selectedFields;

            selectedFieldsBorder.Visibility = selectedFieldsPanelItems.Any()
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception)
        {
            // Hata durumunda sessizce devam et
        }
    }

    private TabItem GetSourceTab((byte sysid, byte compid, uint msgid, string field) field)
    {
        foreach (TabItem tab in messageTabControl.Items)
        {
            if (tab.Content is MessageDetailsControl control)
            {
                var selectedFields = control.GetSelectedFieldsForGraph();
                if (selectedFields.Any(f =>
                    f.sysid == field.sysid &&
                    f.compid == field.compid &&
                    f.msgid == field.msgid &&
                    f.field == field.field))
                {
                    return tab;
                }
            }
        }
        return defaultTab;
    }

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is SelectedFieldInfo field)
        {
            // Field'ı HashSet'ten kaldır
            _selectedFieldsForGraph.Remove((field.SysId, field.CompId, field.MsgId, field.Field));

            // İlgili kontrolden seçimi kaldır
            if (field.SourceTab?.Content is MessageDetailsControl control)
            {
                control.RemoveSelectedField(field.SysId, field.CompId, field.MsgId, field.Field);
            }
            else if (field.SourceTab == defaultTab)
            {
                // Ana sekmedeki seçimi kaldır
                var itemToRemove = fieldsListView?.Items.Cast<dynamic>()
                    .FirstOrDefault(item => item.Field.ToString() == field.Field);
                if (itemToRemove != null)
                {
                    fieldsListView.SelectedItems.Remove(itemToRemove);
                }
            }

            // Paneli güncelle
            UpdateSelectedFieldsPanel();
            UpdateGraphButtonState();
        }
    }

    private void ClearAllFields_Click(object sender, RoutedEventArgs e)
    {
        // Ana koleksiyonu temizle
        _selectedFieldsForGraph.Clear();

        // Ana tab'daki seçimleri temizle
        fieldsListView?.SelectedItems.Clear();

        // Diğer tab'lardaki seçimleri temizle
        foreach (TabItem tab in messageTabControl.Items)
        {
            if (tab.Content is MessageDetailsControl control)
            {
                control.ClearSelectedFields();
            }
        }

        // Panel'i güncelle
        selectedFieldsPanelItems.Clear();
        selectedFieldsBorder.Visibility = Visibility.Collapsed;
        UpdateGraphButtonState();
    }

    private void LoadAssembly(string dllPath)
    {
        try
        {
            if (dllPath != string.Empty && Path.GetExtension(dllPath).ToLower() == ".dll")
            {
                Assembly assembly = Assembly.LoadFile(dllPath);
                Type[] types = assembly.GetTypes();

                // Backup existing message infos if not already backed up
                if (_defaultMessageInfos == null)
                {
                    _defaultMessageInfos = MAVLink.MAVLINK_MESSAGE_INFOS.ToArray();
                }

                var mavlinkType = types.FirstOrDefault(t => t.Name == "MAVLink");
                if (mavlinkType != null)
                {
                    var newMessageInfos = new List<MAVLink.message_info>();

                    // MAVLink sınıfının MAVLINK_MESSAGE_INFOS field'ını bul
                    var fieldInfo = mavlinkType.GetField("MAVLINK_MESSAGE_INFOS",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                    if (fieldInfo != null)
                    {
                        // Direkt olarak array'i al
                        if (fieldInfo.GetValue(null) is Array messageInfos && messageInfos.Length > 0)
                        {
                            foreach (dynamic i in messageInfos)
                            {
                                newMessageInfos.Add(new MAVLink.message_info(i.msgid, i.name, i.crc, i.minlength, i.length, i.type));
                            }
                            // MAVLink.MAVLINK_MESSAGE_INFOS'u yeni array ile güncelle
                            MAVLink.MAVLINK_MESSAGE_INFOS = newMessageInfos.ToArray();

                            RestoreDefaultButton.IsEnabled = true;
                            LoadCustomDllButton.Content = "Reload DLL";
                            ResetButton_Click(null, null);

                            MessageBoxService.ShowInfo(
                                $"Loaded {messageInfos.Length} custom MAVLink messages successfully\n" +
                                $"Previous message count: {_defaultMessageInfos.Length}\n" +
                                $"New message count: {MAVLink.MAVLINK_MESSAGE_INFOS.Length}",
                                this,
                                "Success"
                            );
                            return;
                        }
                    }

                    MessageBoxService.ShowError("Could not find or load MAVLINK_MESSAGE_INFOS from DLL", this);
                }
                else
                {
                    MessageBoxService.ShowError("MAVLink class not found in the DLL", this);
                }
            }
            else
            {
                MessageBoxService.ShowError("Invalid file selected! Please select a .dll file.", this);
            }
        }
        catch (Exception ex)
        {
            MessageBoxService.ShowError($"Error loading assembly: {ex.Message}", this);

            // Hata durumunda varsayılan mesajlara geri dön
            if (_defaultMessageInfos != null)
            {
                MAVLink.MAVLINK_MESSAGE_INFOS = _defaultMessageInfos;
            }

            ResetButton_Click(null, null);
        }
    }
    private void LoadCustomDllButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "DLL files (*.dll)|*.dll",
            Title = "Select MAVLink DLL"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            LoadAssembly(openFileDialog.FileName);
        }
        else
        {
            MessageBoxService.ShowError("No Available File!", this, "Error");
        }
    }

    private void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreDefaultMessageInfos();
    }

    private void RestoreDefaultMessageInfos()
    {
        if (_defaultMessageInfos != null)
        {
            MAVLink.MAVLINK_MESSAGE_INFOS = _defaultMessageInfos;
            _defaultMessageInfos = null;
            _loadedCustomDll = null;

            RestoreDefaultButton.IsEnabled = false;
            LoadCustomDllButton.Content = "Load DLL";

            // Reset view
            ResetButton_Click(null, null);
        }
    }

    private void SelectedField_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            if (sender is Border border && border.Tag is SelectedFieldInfo field)
            {
                // Field'ı HashSet'ten kaldır
                _selectedFieldsForGraph.Remove((field.SysId, field.CompId, field.MsgId, field.Field));

                // İlgili kontrolden seçimi kaldır
                if (field.SourceTab?.Content is MessageDetailsControl control)
                {
                    control.RemoveSelectedField(field.SysId, field.CompId, field.MsgId, field.Field);
                }
                else if (field.SourceTab == defaultTab)
                {
                    // Ana sekmedeki seçimi kaldır
                    var itemToRemove = fieldsListView?.Items.Cast<dynamic>()
                        .FirstOrDefault(item => item.Field.ToString() == field.Field);
                    if (itemToRemove != null)
                    {
                        fieldsListView.SelectedItems.Remove(itemToRemove);
                    }
                }

                // Paneli güncelle
                UpdateSelectedFieldsPanel();
                UpdateGraphButtonState();
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Processes batch updates for the UI components
    /// </summary>
    /// <param name="sender">The event sender</param>
    /// <param name="e">The event arguments</param>
    private void ProcessPendingUpdates(object sender, EventArgs e)
    {
        int processed = 0;
        while (_pendingUpdates.Count > 0 && processed < UI_BATCH_SIZE)
        {
            var action = _pendingUpdates.Dequeue();
            action();
            processed++;
        }
    }

} // MainWindow class ends here

// Helper extension method for cleaner FrameworkElementFactory configuration
/// <summary>
/// Helper extension class for configuring FrameworkElementFactory instances
/// </summary>
public static class FrameworkElementFactoryExtensions
{
    /// <summary>
    /// Configures a FrameworkElementFactory instance using the provided action
    /// </summary>
    /// <param name="factory">The factory to configure</param>
    /// <param="configure">The configuration action to apply</param>
    /// <returns>The configured factory instance</returns>
    public static FrameworkElementFactory With(this FrameworkElementFactory factory, Action<FrameworkElementFactory> configure)
    {
        configure(factory);
        return factory;
    }
}