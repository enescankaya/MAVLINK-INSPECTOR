using System.Diagnostics;
using System.IO.Ports;
using System.Threading.Channels;

namespace MavlinkInspector.Connections;

public class SerialConnection : IConnection
{
    private readonly SerialPort _port;
    private readonly Channel<byte[]> _dataChannel;
    private readonly byte[] _readBuffer = new byte[4096];
    private volatile bool _isDisposed;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    public bool IsConnected => _port?.IsOpen ?? false;
    public bool IsDisposed => _isDisposed;
    public ChannelReader<byte[]> DataChannel => _dataChannel.Reader;

    public SerialConnection(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
            ReadBufferSize = 4096,
            WriteBufferSize = 4096
        };
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


            _port.DataReceived += Port_DataReceived;
            _port.ErrorReceived += Port_ErrorReceived;
            _port.Open();

        }
        catch (Exception ex)
        {
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_isDisposed || !IsConnected) return;

        try
        {
            var bytesToRead = _port.BytesToRead;

            if (bytesToRead <= 0) return;

            var buffer = new byte[bytesToRead];
            var bytesRead = _port.Read(buffer, 0, bytesToRead);

            if (bytesRead > 0)
            {
                _dataChannel.Writer.TryWrite(buffer);
            }
        }
        catch (Exception ex)
        {
        }
    }

    private void Port_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        _ = DisconnectAsync();
    }

    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            if (!IsConnected) return;

            _port.DataReceived -= Port_DataReceived;
            _port.ErrorReceived -= Port_ErrorReceived;
            _port.Close();
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
        if (_isDisposed || !IsConnected) return;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await _port.BaseStream.WriteAsync(data, cancellationToken);
            await _port.BaseStream.FlushAsync(cancellationToken);
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
        _port?.Dispose();
        _connectionLock.Dispose();
    }
}
