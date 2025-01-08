using System.Threading.Channels;

namespace MavlinkInspector.Connections;

public interface IConnection : IAsyncDisposable
{
    /// <summary>
    /// Bağlantının aktif olup olmadığını belirtir.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Veri kanalını döndürür.
    /// </summary>
    ChannelReader<byte[]> DataChannel { get; }

    /// <summary>
    /// Bağlantıyı asenkron olarak başlatır.
    /// </summary>
    /// <param name="cancellationToken">İptal belirteci.</param>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Bağlantıyı asenkron olarak sonlandırır.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Veriyi asenkron olarak gönderir.
    /// </summary>
    /// <param name="data">Gönderilecek veri.</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    Task SendAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nesnenin imha edilip edilmediğini belirtir.
    /// </summary>
    bool IsDisposed { get; }
}
