﻿using System.Collections.Concurrent;

namespace MavlinkInspector;

/// <summary>
/// Paket denetleyici sınıfı.
/// </summary>
public class PacketInspector<T>
{
    private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, T>> _history = new();
    private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, List<irate>>> _rate = new();
    private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, List<irate>>> _bps = new();

    private const int MAX_HISTORY_TIME_MS = 3000;
    public int RateHistory { get; set; } = 200;
    private readonly object _lock = new object();
    public event EventHandler NewSysidCompid;

    private struct irate
    {
        public DateTime dateTime { get; }
        public int value { get; }

        public irate(DateTime dateTime, int value)
        {
            this.dateTime = dateTime;
            this.value = value;
        }
    }

    private readonly ConcurrentDictionary<(uint id, uint msgid), CircularBuffer<irate>> _rateBuffers =
        new ConcurrentDictionary<(uint id, uint msgid), CircularBuffer<irate>>();
    private readonly ConcurrentDictionary<(uint id, uint msgid), CircularBuffer<irate>> _bpsBuffers =
        new ConcurrentDictionary<(uint id, uint msgid), CircularBuffer<irate>>();

    private class CircularBuffer<T> where T : struct
    {
        private readonly T[] _buffer;
        private int _start;
        private int _count;
        private readonly object _lock = new();

        public CircularBuffer(int capacity)
        {
            _buffer = new T[capacity];
        }

        public void Add(T item)
        {
            lock (_lock)
            {
                if (_count == _buffer.Length)
                {
                    _start = (_start + 1) % _buffer.Length;
                }
                else
                {
                    _count++;
                }
                _buffer[(_start + _count - 1) % _buffer.Length] = item;
            }
        }

        public IEnumerable<T> GetItems(DateTime cutoff)
        {
            lock (_lock)
            {
                var items = new List<T>();
                for (int i = 0; i < _count; i++)
                {
                    var item = _buffer[(_start + i) % _buffer.Length];
                    if (item is irate rate && rate.dateTime >= cutoff)
                        items.Add(item);
                }
                return items;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _start = 0;
                _count = 0;
            }
        }
    }

    /// <summary>
    /// Görülen sistem kimliklerini döndürür.
    /// </summary>
    /// <returns>Görülen sistem kimliklerinin listesi.</returns>
    public List<byte> SeenSysid()
    {
        return toArray(_history.Keys).Select(id => GetFromID(id).sysid).ToList();
    }

    /// <summary>
    /// Görülen bileşen kimliklerini döndürür.
    /// </summary>
    /// <returns>Görülen bileşen kimliklerinin listesi.</returns>
    public List<byte> SeenCompid()
    {
        return toArray(_history.Keys).Select(id => GetFromID(id).compid).ToList();
    }

    /// <summary>
    /// Belirtilen mesajın gönderim hızını hesaplar.
    /// </summary>
    /// <param name="sysid">Sistem kimliği.</param>
    /// <param name="compid">Bileşen kimliği.</param>
    /// <param name="msgid">Mesaj kimliği.</param>
    /// <returns>Mesaj gönderim hızı (Hz).</returns>
    public double GetMessageRate(byte sysid, byte compid, uint msgid)
    {
        var id = GetID(sysid, compid);
        var key = (id, msgid);
        var now = DateTime.UtcNow;
        var cutoff = now.AddMilliseconds(-MAX_HISTORY_TIME_MS);

        var buffer = _rateBuffers.GetOrAdd(key, _ => new CircularBuffer<irate>(RateHistory));
        var samples = buffer.GetItems(cutoff).Cast<irate>().ToList();

        if (samples.Count < 2) return 0;

        var timeSpan = (samples.Last().dateTime - samples.First().dateTime).TotalSeconds;
        if (timeSpan <= 0) return 0;

        var messageCount = samples.Sum(s => s.value);
        return messageCount / timeSpan;
    }

    /// <summary>
    /// Belirtilen mesajın bant genişliğini hesaplar.
    /// </summary>
    /// <param name="sysid">Sistem kimliği.</param>
    /// <param name="compid">Bileşen kimliği.</param>
    /// <param name="msgid">Mesaj kimliği.</param>
    /// <returns>Bant genişliği (bps).</returns>
    public double GetBps(byte sysid, byte compid, uint msgid)
    {
        var id = GetID(sysid, compid);
        var key = (id, msgid);
        var now = DateTime.UtcNow;
        var cutoff = now.AddMilliseconds(-MAX_HISTORY_TIME_MS);

        var buffer = _bpsBuffers.GetOrAdd(key, _ => new CircularBuffer<irate>(RateHistory));
        var samples = buffer.GetItems(cutoff).Cast<irate>().ToList();

        if (samples.Count < 2) return 0;

        var timeSpan = (samples.Last().dateTime - samples.First().dateTime).TotalSeconds;
        if (timeSpan <= 0) return 0;

        var totalBits = samples.Sum(s => s.value) * 8.0;
        return totalBits / timeSpan;
    }

    /// <summary>
    /// Yeni bir mesaj ekler.
    /// </summary>
    /// <param name="sysid">Sistem kimliği.</param>
    /// <param name="compid">Bileşen kimliği.</param>
    /// <param name="msgid">Mesaj kimliği.</param>
    /// <param name="message">Mesaj içeriği.</param>
    /// <param name="size">Mesaj boyutu.</param>
    public void Add(byte sysid, byte compid, uint msgid, T message, int size)
    {
        var id = GetID(sysid, compid);
        var now = DateTime.UtcNow;
        var key = (id, msgid);

        _history.GetOrAdd(id, _ => new ConcurrentDictionary<uint, T>())[msgid] = message;

        var rateBuffer = _rateBuffers.GetOrAdd(key, _ => new CircularBuffer<irate>(RateHistory));
        rateBuffer.Add(new irate(now, 1));

        var totalSize = size + 8;
        var bpsBuffer = _bpsBuffers.GetOrAdd(key, _ => new CircularBuffer<irate>(RateHistory));
        bpsBuffer.Add(new irate(now, totalSize));

        NewSysidCompid?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<T1> toArray<T1>(IEnumerable<T1> input)
    {
        lock (_lock)
        {
            return input.ToArray();
        }
    }

    /// <summary>
    /// Paket mesajlarını döndürür.
    /// </summary>
    /// <returns>Paket mesajlarının listesi.</returns>
    public IEnumerable<T> GetPacketMessages()
    {
        return toArray(_history.Values)
            .SelectMany(messages => toArray(messages.Values));
    }

    /// <summary>
    /// Tüm verileri temizler.
    /// </summary>
    public void Clear()
    {
        _history.Clear();
        foreach (var buffer in _rateBuffers.Values)
            buffer.Clear();
        foreach (var buffer in _bpsBuffers.Values)
            buffer.Clear();

        NewSysidCompid?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Belirtilen sistem ve bileşen kimliklerine ait verileri temizler.
    /// </summary>
    /// <param name="sysid">Sistem kimliği.</param>
    /// <param name="compid">Bileşen kimliği.</param>
    public void Clear(byte sysid, byte compid)
    {
        var id = GetID(sysid, compid);
        lock (_lock)
        {
            _history.GetOrAdd(id, _ => new ConcurrentDictionary<uint, T>());
            _rate.GetOrAdd(id, _ => new ConcurrentDictionary<uint, List<irate>>());
            _bps.GetOrAdd(id, _ => new ConcurrentDictionary<uint, List<irate>>());

            if (_history.TryGetValue(id, out var history))
                history.Clear();
            if (_rate.TryGetValue(id, out var rate))
                rate.Clear();
            if (_bps.TryGetValue(id, out var bps))
                bps.Clear();
        }
        NewSysidCompid?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Belirtilen sistem ve bileşen kimliklerine ait mesajları döndürür.
    /// </summary>
    /// <param name="sysid">Sistem kimliği.</param>
    /// <param name="compid">Bileşen kimliği.</param>
    /// <returns>Mesajların listesi.</returns>
    public IEnumerable<T> this[byte sysid, byte compid]
    {
        get
        {
            var id = GetID(sysid, compid);
            return _history.TryGetValue(id, out var messages)
                ? toArray(messages.Values)
                : Enumerable.Empty<T>();
        }
    }

    private static uint GetID(byte sysid, byte compid)
    {
        return (uint)(sysid << 8) | compid;
    }

    private static (byte sysid, byte compid) GetFromID(uint id)
    {
        return ((byte)(id >> 8), (byte)(id & 0xFF));
    }

    /// <summary>
    /// Belirtilen sistem ve bileşen kimliklerine ait en son mesajı döndürmeye çalışır.
    /// </summary>
    /// <param name="sysid">Sistem kimliği.</param>
    /// <param name="compid">Bileşen kimliği.</param>
    /// <param name="msgid">Mesaj kimliği.</param>
    /// <param name="message">En son mesaj.</param>
    /// <returns>En son mesajın başarıyla döndürülüp döndürülmediği.</returns>
    public bool TryGetLatestMessage(byte sysid, byte compid, uint msgid, out T message)
    {
        message = default;
        var id = GetID(sysid, compid);

        return _history.TryGetValue(id, out var messages) &&
               messages.TryGetValue(msgid, out message);
    }
}