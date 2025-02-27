﻿using MavlinkInspector.Connections;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace MavlinkInspector;

/// <summary>
/// Bağlantı yöneticisi sınıfı.
/// </summary>
public class ConnectionManager : IAsyncDisposable
{
    private IConnection? _connection;
    private readonly Channel<byte[]> _dataChannel;
    private CancellationTokenSource? _cts;

    public bool IsConnected => _connection?.IsConnected ?? false;
    public ChannelReader<byte[]> DataChannel => _dataChannel.Reader;

    // Events for message handling
    public event Action<MAVLink.MAVLinkMessage>? OnMessageReceived;
    public event Action<MAVLink.MAVLinkMessage>? OnMessageSent;

    /// <summary>
    /// Bağlantı yöneticisi oluşturur.
    /// </summary>
    public ConnectionManager()
    {
        _dataChannel = Channel.CreateUnbounded<byte[]>();
    }

    /// <summary>
    /// Bağlantıyı asenkron olarak başlatır.
    /// </summary>
    /// <param name="parameters">Bağlantı parametreleri.</param>
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

    /// <summary>
    /// Gelen verileri iletir.
    /// </summary>
    /// <param name="ct">İptal belirteci.</param>
    private async Task ForwardDataAsync(CancellationToken ct)
    {
        if (_connection == null) return;

        var parser = new MAVLink.MavlinkParse();
        var buffer = new List<byte>();

        try
        {
            await foreach (var data in _connection.DataChannel.ReadAllAsync(ct))
            {
                buffer.AddRange(data);

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

                        var bytesRead = (int)ms.Position;
                        buffer.RemoveRange(0, bytesRead);

                        OnMessageReceived?.Invoke(message);
                    }
                    catch
                    {
                        buffer.RemoveAt(0);
                    }
                }

                if (buffer.Count > 16384)
                {
                    buffer.RemoveRange(0, buffer.Count - 16384);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Mesajı asenkron olarak gönderir.
    /// </summary>
    /// <param name="message">Gönderilecek mesaj.</param>
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
        }
    }

    /// <summary>
    /// Bağlantıyı asenkron olarak sonlandırır.
    /// </summary>
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

    /// <summary>
    /// Nesneyi asenkron olarak imha eder.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts?.Dispose();
    }
}
