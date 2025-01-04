using System.Diagnostics;
using System.IO.Ports;
using System.Threading.Channels;

namespace MavlinkInspector.Connections;

public class SerialConnection : IConnection
{
    private readonly SerialPort _port;
    private readonly Channel<byte[]> _dataChannel;
    private bool _isDisposed;

    public bool IsConnected => _port?.IsOpen ?? false;
    public ChannelReader<byte[]> DataChannel => _dataChannel.Reader;

    public SerialConnection(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        _dataChannel = Channel.CreateUnbounded<byte[]>();
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _port.DataReceived += Port_DataReceived;
        _port.Open();
        return Task.CompletedTask;
    }

    private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            Thread.Sleep(10); // Reduced sleep time
            var bytesToRead = _port.BytesToRead;
            if (bytesToRead > 0)
            {
                var buffer = new byte[bytesToRead];
                var bytesRead = _port.Read(buffer, 0, bytesToRead);
                if (bytesRead > 0)
                {
                    Debug.WriteLine($"Serial received {bytesRead} bytes");
                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);
                    if (!_dataChannel.Writer.TryWrite(data))
                    {
                        Debug.WriteLine("Failed to write to channel");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Serial port read error: {ex.Message}");
        }
    }

    public Task DisconnectAsync()
    {
        if (_port?.IsOpen == true)
        {
            _port.DataReceived -= Port_DataReceived;
            _port.Close();
        }
        return Task.CompletedTask;
    }

    public Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (_port?.IsOpen == true)
        {
            _port.Write(data, 0, data.Length);
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        await DisconnectAsync();
        _port?.Dispose();
        _isDisposed = true;
    }
}
