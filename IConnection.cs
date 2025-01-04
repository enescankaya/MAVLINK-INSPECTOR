using System.Threading.Channels;

namespace MavlinkInspector.Connections;

public interface IConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    ChannelReader<byte[]> DataChannel { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task SendAsync(byte[] data, CancellationToken cancellationToken = default);
    bool IsDisposed { get; }  // Yeni property
}
