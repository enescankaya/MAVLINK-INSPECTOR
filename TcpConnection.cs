using System.Net.Sockets;
using System.Threading.Channels;

namespace MavlinkInspector.Connections;

public class TcpConnection : IConnection
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly Channel<byte[]> _dataChannel;
    private CancellationTokenSource? _readCts;
    private bool _isDisposed;

    public bool IsConnected => _client?.Connected ?? false;
    public ChannelReader<byte[]> DataChannel => _dataChannel.Reader;

    public TcpConnection(string host, int port)
    {
        _host = host;
        _port = port;
        _dataChannel = Channel.CreateUnbounded<byte[]>();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, cancellationToken);
        _stream = _client.GetStream();

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = ReadLoopAsync(_readCts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                var bytesRead = await _stream!.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);
                await _dataChannel.Writer.WriteAsync(data, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task DisconnectAsync()
    {
        _readCts?.Cancel();
        if (_stream != null)
        {
            await _stream.DisposeAsync();
            _stream = null;
        }
        _client?.Dispose();
        _client = null;
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (_stream != null && IsConnected)
        {
            await _stream.WriteAsync(data, cancellationToken);
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
