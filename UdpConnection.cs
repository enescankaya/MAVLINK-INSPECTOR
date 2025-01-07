using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Net;
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
    private IPEndPoint? _remoteEndPoint;

    public bool IsConnected => _client != null;  // Changed from Client.Connected check
    public bool IsDisposed => _isDisposed;
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

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (IsDisposed) return;

            // Close existing client if any
            if (_client != null)
            {
                _client.Close();
                _client = null;
            }

            // Create endpoint
            _remoteEndPoint = new IPEndPoint(IPAddress.Any, _port);

            try
            {
                // Create new client bound to the port
                _client = new UdpClient(_port);

                _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _ = ReadLoopAsync(_readCts.Token);
            }
            catch (Exception ex)
            {
                throw;
            }
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
            while (!ct.IsCancellationRequested && _client != null)
            {
                try
                {
                    var result = await _client.ReceiveAsync(ct);

                    if (result.Buffer.Length > 0)
                    {
                        await _dataChannel.Writer.WriteAsync(result.Buffer, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await Task.Delay(100, ct); // Prevent tight loop on error
                }
            }
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
            if (_client != null)
            {
                _client.Close();
                _client.Dispose();
                _client = null;
            }
            _dataChannel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
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
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _client == null) return;

        try
        {
            // Send to the specific remote endpoint if we have one
            if (_remoteEndPoint != null)
            {
                await _client.SendAsync(data, data.Length, _remoteEndPoint);
            }
        }
        catch (Exception ex)
        {
        }
    }
}
