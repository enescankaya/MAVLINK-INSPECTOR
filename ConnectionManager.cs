using MavlinkInspector.Connections;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace MavlinkInspector;

public class ConnectionManager : IAsyncDisposable
{
    private Connections.IConnection? _connection;
    private readonly Channel<byte[]> _dataChannel;
    private CancellationTokenSource? _cts;

    public bool IsConnected => _connection?.IsConnected ?? false;
    public ChannelReader<byte[]> DataChannel => _dataChannel.Reader;

    // Events for message handling
    public event Action<MAVLink.MAVLinkMessage>? OnMessageReceived;
    public event Action<MAVLink.MAVLinkMessage>? OnMessageSent;

    public ConnectionManager()
    {
        _dataChannel = Channel.CreateUnbounded<byte[]>();
    }

    public async Task ConnectAsync(ConnectionParameters parameters)
    {
        await DisconnectAsync();

        _connection = parameters.ConnectionType switch
        {
            "Serial" => new SerialConnection(parameters.Port!, parameters.BaudRate),
            "TCP" => new TcpConnection(parameters.IpAddress!, parameters.NetworkPort),
            "UDP" => new UdpConnection(parameters.IpAddress!, parameters.NetworkPort),
            _ => throw new ArgumentException("Invalid connection type")
        };

        _cts = new CancellationTokenSource();
        await _connection.ConnectAsync(_cts.Token);
        _ = ForwardDataAsync(_cts.Token);
    }

    private async Task ForwardDataAsync(CancellationToken ct)
    {
        if (_connection == null) return;

        var parser = new MAVLink.MavlinkParse();
        var buffer = new List<byte>();

        try
        {
            await foreach (var data in _connection.DataChannel.ReadAllAsync(ct))
            {
                // Tüm verileri buffer'a ekle
                buffer.AddRange(data);

                // Yeterli veri varsa paketleri işle
                while (buffer.Count >= 8)
                {
                    try
                    {
                        using var ms = new MemoryStream(buffer.ToArray());
                        var message = parser.ReadPacket(ms);

                        if (message == null)
                        {
                            buffer.RemoveAt(0);
                            continue;
                        }

                        // Başarılı okunan veriyi buffer'dan kaldır
                        var bytesRead = (int)ms.Position;
                        buffer.RemoveRange(0, bytesRead);

                        // Mesajı işle
                        OnMessageReceived?.Invoke(message);
                    }
                    catch
                    {
                        buffer.RemoveAt(0);
                    }
                }

                // Buffer'ı temizle (çok büyükse)
                if (buffer.Count > 16384)
                {
                    buffer.RemoveRange(0, buffer.Count - 16384);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
    }

    public async Task SendAsync(MAVLink.MAVLinkMessage message)
    {
        if (_connection == null) return;

        try
        {
            var parser = new MAVLink.MavlinkParse();
            //var data = parser.GenerateMAVLinkPacket(message);
            //await _connection.SendAsync(data);
            OnMessageSent?.Invoke(message);
        }
        catch (Exception)
        {
            // Hata durumunda sessizce devam et
        }
    }

    public async Task DisconnectAsync()
    {
        if (_connection != null)
        {
            _cts?.Cancel();
            await _connection.DisconnectAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts?.Dispose();
    }
}
