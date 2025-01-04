using System.Net.Sockets;
using System.Threading.Channels;

namespace MavlinkInspector.Connections;

public class UdpConnection : IConnection
{
    private readonly string _host;
    private readonly int _port;
    private UdpClient? _client;
    private readonly Channel<byte[]> _dataChannel;
    private CancellationTokenSource? _readCts;
    private bool _isDisposed;

    public bool IsConnected => _client != null;
    public ChannelReader<byte[]> DataChannel => _dataChannel.Reader;

    public UdpConnection(string host, int port)
    {
        _host = host;
        _port = port;
        _dataChannel = Channel.CreateUnbounded<byte[]>();
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _client = new UdpClient();
        _client.Connect(_host, _port);

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = ReadLoopAsync(_readCts.Token);

        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                var result = await _client!.ReceiveAsync(ct);
                await _dataChannel.Writer.WriteAsync(result.Buffer, ct);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await DisconnectAsync();
        }
    }

    public Task DisconnectAsync()
    {
        _readCts?.Cancel();
        _client?.Dispose();
        _client = null;
        return Task.CompletedTask;
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (_client != null && IsConnected)
        {
            await _client.SendAsync(data, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        await DisconnectAsync();
        _readCts?.Dispose();
        _isDisposed = true;
    }
}
