using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;

namespace MavlinkInspector.Connections;

/// <summary>
/// TCP bağlantısı sınıfı.
/// </summary>
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

    /// <summary>
    /// TCP bağlantısı oluşturur.
    /// </summary>
    /// <param name="host">Sunucu adresi.</param>
    /// <param name="port">Port numarası.</param>
    public TcpConnection(string host, int port)
    {
        _host = host;
        _port = port;
        _dataChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>
    /// Bağlantıyı asenkron olarak başlatır.
    /// </summary>
    /// <param name="cancellationToken">İptal belirteci.</param>
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

    /// <summary>
    /// TCP verilerini okuma döngüsü.
    /// </summary>
    /// <param name="ct">İptal belirteci.</param>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested && IsConnected && _stream != null)
            {
                var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (bytesRead == 0) break;

                var data = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);
                await _dataChannel.Writer.WriteAsync(data, ct);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await DisconnectAsync();
        }
        finally
        {
            _dataChannel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Bağlantıyı asenkron olarak sonlandırır.
    /// </summary>
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

    /// <summary>
    /// Veriyi asenkron olarak gönderir.
    /// </summary>
    /// <param name="data">Gönderilecek veri.</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
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

    /// <summary>
    /// Nesneyi asenkron olarak imha eder.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await DisconnectAsync();
        _readCts?.Dispose();
        _connectionLock.Dispose();
    }

    /// <summary>
    /// Soket yapılandırmasını ayarlar.
    /// </summary>
    private void ConfigureSocket()
    {
        if (_client?.Client == null) return;

        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
        _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
        _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
    }
}
