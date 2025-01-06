using System.Diagnostics;
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
    private readonly byte[] _readBuffer = new byte[4096];
    private CancellationTokenSource? _readCts;
    private volatile bool _isDisposed;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    public bool IsConnected => _client?.Connected ?? false;
    public bool IsDisposed => _isDisposed;
    public ChannelReader<byte[]> DataChannel => _dataChannel.Reader;

    public TcpConnection(string host, int port)
    {
        _host = host;
        _port = port;
        _dataChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (IsDisposed || IsConnected) return;

            _client = new TcpClient();
            await _client.ConnectAsync(_host, _port, cancellationToken);
            _stream = _client.GetStream();

            _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = ReadLoopAsync(_readCts.Token);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                var bytesRead = await _stream!.ReadAsync(_readBuffer, ct);
                if (bytesRead == 0) break;

                var data = new byte[bytesRead];
                Buffer.BlockCopy(_readBuffer, 0, data, 0, bytesRead);
                await _dataChannel.Writer.WriteAsync(data, ct);
            }
        }
        catch when (!ct.IsCancellationRequested)
        {
            await DisconnectAsync();
        }
        finally
        {
            _dataChannel.Writer.TryComplete();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _readCts?.Cancel();

            if (_stream != null)
            {
                try
                {
                    _stream.Close();
                    await _stream.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Stream close error: {ex.Message}");
                }
                _stream = null;
            }

            if (_client != null)
            {
                try
                {
                    _client.Close();
                    _client.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Client close error: {ex.Message}");
                }
                _client = null;
            }

            _dataChannel.Writer.TryComplete();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !IsConnected) return;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await _stream!.WriteAsync(data, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await DisconnectAsync();
        _readCts?.Dispose();
        _connectionLock.Dispose();
    }

    // Socket timeout ve keep-alive ayarları ekle
    private void ConfigureSocket()
    {
        if (_client?.Client == null) return;

        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
        _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
        _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
    }
}
