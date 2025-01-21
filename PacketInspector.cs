using System.Collections.Concurrent;

namespace MavlinkInspector;

public enum PacketInspectorConstants
{
    MaxHistoryTimeMs = 3000,
    DefaultRateHistory = 200,
    MaxQueueSize = 10000
}

public class PacketInspector<T>
{
    private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, T>> _history = new();
    private readonly ConcurrentDictionary<(uint id, uint msgid), CircularBuffer> _rateBuffers = new();
    private readonly ConcurrentDictionary<(uint id, uint msgid), CircularBuffer> _bpsBuffers = new();

    private const int MAX_HISTORY_SIZE = 10000;
    private const int MAX_RATE_BUFFERS = 1000;
    private const int MAX_BATCH_SIZE = 100;

    private int _rateHistory = (int)PacketInspectorConstants.DefaultRateHistory;
    public int RateHistory
    {
        get => _rateHistory;
        set => _rateHistory = Math.Max(1, Math.Min(value, (int)PacketInspectorConstants.MaxQueueSize));
    }

    private EventHandler? _newSysidCompid;
    public event EventHandler NewSysidCompid
    {
        add => _newSysidCompid += value;
        remove => _newSysidCompid -= value;
    }

    // Using struct for better memory efficiency and to avoid garbage collection
    private readonly struct PacketRate
    {
        public readonly long Ticks;
        public readonly int Value;

        public PacketRate(long ticks, int value)
        {
            Ticks = ticks;
            Value = value;
        }
    }

    private sealed class CircularBuffer
    {
        private readonly PacketRate[] _buffer;
        private int _start;
        private int _count;
        private readonly int _capacity;
        private SpinLock _spinLock = new(false);
        private readonly int _maxBatchSize;
        private readonly object _batchLock = new();

        public CircularBuffer(int capacity)
        {
            _capacity = capacity;
            _buffer = new PacketRate[capacity];
        }

        public void Add(PacketRate item)
        {
            bool lockTaken = false;
            try
            {
                _spinLock.Enter(ref lockTaken);

                if (_count == _capacity)
                {
                    _start = (_start + 1) % _capacity;
                }
                else
                {
                    _count++;
                }
                _buffer[(_start + _count - 1) % _capacity] = item;
            }
            finally
            {
                if (lockTaken) _spinLock.Exit();
            }
        }

        public void AddBatch(List<PacketRate> items)
        {
            lock (_batchLock)
            {
                foreach (var item in items.Take(MAX_BATCH_SIZE))
                {
                    Add(item);
                }
            }
        }

        public double CalculateRate(long cutoffTicks)
        {
            bool lockTaken = false;
            try
            {
                _spinLock.Enter(ref lockTaken);

                if (_count < 2) return 0;

                int validCount = 0;
                int totalValue = 0;
                long firstTicks = 0;
                long lastTicks = 0;
                bool isFirst = true;

                for (int i = 0; i < _count; i++)
                {
                    var item = _buffer[(_start + i) % _capacity];
                    if (item.Ticks >= cutoffTicks)
                    {
                        if (isFirst)
                        {
                            firstTicks = item.Ticks;
                            isFirst = false;
                        }
                        lastTicks = item.Ticks;
                        totalValue += item.Value;
                        validCount++;
                    }
                }

                if (validCount < 2) return 0;

                double timeSpan = (lastTicks - firstTicks) / (double)TimeSpan.TicksPerSecond;
                return timeSpan <= 0 ? 0 : totalValue / timeSpan;
            }
            finally
            {
                if (lockTaken) _spinLock.Exit();
            }
        }

        public void Clear()
        {
            bool lockTaken = false;
            try
            {
                _spinLock.Enter(ref lockTaken);
                _start = 0;
                _count = 0;
            }
            finally
            {
                if (lockTaken) _spinLock.Exit();
            }
        }

        public void RemoveOldItems(long cutoffTicks)
        {
            bool lockTaken = false;
            try
            {
                _spinLock.Enter(ref lockTaken);
                while (_count > 0 && _buffer[_start].Ticks < cutoffTicks)
                {
                    _start = (_start + 1) % _capacity;
                    _count--;
                }
            }
            finally
            {
                if (lockTaken) _spinLock.Exit();
            }
        }
    }

    public List<byte> SeenSysid()
    {
        return _history.Keys.Select(id => (byte)(id >> 8)).ToList();
    }

    public List<byte> SeenCompid()
    {
        return _history.Keys.Select(id => (byte)(id & 0xFF)).ToList();
    }

    public double GetMessageRate(byte sysid, byte compid, uint msgid)
    {
        var id = GetID(sysid, compid);
        var key = (id, msgid);
        var cutoffTicks = DateTime.UtcNow.AddMilliseconds(-(int)PacketInspectorConstants.MaxHistoryTimeMs).Ticks;

        return _rateBuffers.TryGetValue(key, out var buffer)
            ? buffer.CalculateRate(cutoffTicks)
            : 0;
    }

    public double GetBps(byte sysid, byte compid, uint msgid)
    {
        var id = GetID(sysid, compid);
        var key = (id, msgid);
        var cutoffTicks = DateTime.UtcNow.AddMilliseconds(-(int)PacketInspectorConstants.MaxHistoryTimeMs).Ticks;

        return _bpsBuffers.TryGetValue(key, out var buffer)
            ? buffer.CalculateRate(cutoffTicks) * 8.0
            : 0;
    }

    public void Add(byte sysid, byte compid, uint msgid, T message, int size)
    {
        // History size kontrolü
        if (_history.Count > MAX_HISTORY_SIZE)
        {
            var oldestKey = _history.Keys.First();
            _history.TryRemove(oldestKey, out _);
        }

        // RateBuffers size kontrolü
        if (_rateBuffers.Count > MAX_RATE_BUFFERS)
        {
            var oldestKey = _rateBuffers.Keys.First();
            _rateBuffers.TryRemove(oldestKey, out _);
        }

        var id = GetID(sysid, compid);
        var key = (id, msgid);
        var currentTicks = DateTime.UtcNow.Ticks;

        _history.GetOrAdd(id, _ => new ConcurrentDictionary<uint, T>())[msgid] = message;

        var rateBuffer = _rateBuffers.GetOrAdd(key, _ => new CircularBuffer(RateHistory));
        rateBuffer.Add(new PacketRate(currentTicks, 1));

        var bpsBuffer = _bpsBuffers.GetOrAdd(key, _ => new CircularBuffer(RateHistory));
        bpsBuffer.Add(new PacketRate(currentTicks, size + 8));

        _newSysidCompid?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<T> GetPacketMessages()
    {
        return _history.Values.SelectMany(messages => messages.Values);
    }

    public void Clear()
    {
        _history.Clear();
        foreach (var buffer in _rateBuffers.Values)
            buffer.Clear();
        foreach (var buffer in _bpsBuffers.Values)
            buffer.Clear();

        _newSysidCompid?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(byte sysid, byte compid)
    {
        var id = GetID(sysid, compid);
        _history.TryRemove(id, out _);

        foreach (var key in _rateBuffers.Keys.Where(k => k.id == id).ToList())
        {
            if (_rateBuffers.TryGetValue(key, out var buffer))
                buffer.Clear();
        }

        foreach (var key in _bpsBuffers.Keys.Where(k => k.id == id).ToList())
        {
            if (_bpsBuffers.TryGetValue(key, out var buffer))
                buffer.Clear();
        }

        _newSysidCompid?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<T> this[byte sysid, byte compid]
    {
        get
        {
            var id = GetID(sysid, compid);
            return _history.TryGetValue(id, out var messages)
                ? messages.Values
                : Enumerable.Empty<T>();
        }
    }

    private static uint GetID(byte sysid, byte compid) => (uint)(sysid << 8) | compid;

    public bool TryGetLatestMessage(byte sysid, byte compid, uint msgid, out T message)
    {
        message = default;
        var id = GetID(sysid, compid);

        return _history.TryGetValue(id, out var messages) &&
               messages.TryGetValue(msgid, out message);
    }

    public void CleanupOldData()
    {
        var cutoffTicks = DateTime.UtcNow.AddMilliseconds(-(int)PacketInspectorConstants.MaxHistoryTimeMs).Ticks;

        foreach (var buffer in _rateBuffers.Values)
        {
            buffer.RemoveOldItems(cutoffTicks);
        }

        foreach (var buffer in _bpsBuffers.Values)
        {
            buffer.RemoveOldItems(cutoffTicks);
        }
    }
}