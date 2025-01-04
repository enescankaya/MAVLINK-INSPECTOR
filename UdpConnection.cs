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
    private volatile bool _isDisposed;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    public bool IsConnected => _client != null;
    public bool IsDisposed => _isDisposed; // IConnection interface için eklendi
    public ChannelReader<byte[]> DataChannel => _dataChannel.Reader;

    public UdpConnection(string host, int port)
    {
        _host = host;
        _port = port;
        _dataChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
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

    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _readCts?.Cancel();
            _client?.Dispose();
            _client = null;
            // CompleteAsync yerine TryComplete kullan
            _dataChannel.Writer.TryComplete();
        }
        finally
        {
            _connectionLock.Release();
        }
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
