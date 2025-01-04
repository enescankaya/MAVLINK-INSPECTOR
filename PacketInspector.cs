using System.Collections.Concurrent;

namespace MavlinkInspector
{
    public class PacketInspector<T>
    {
        private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, T>> _history =
            new ConcurrentDictionary<uint, ConcurrentDictionary<uint, T>>();
        private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, ConcurrentQueue<PacketRate>>> _rate =
            new ConcurrentDictionary<uint, ConcurrentDictionary<uint, ConcurrentQueue<PacketRate>>>();
        private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, ConcurrentQueue<PacketRate>>> _bps =
            new ConcurrentDictionary<uint, ConcurrentDictionary<uint, ConcurrentQueue<PacketRate>>>();

        private const int MAX_HISTORY_TIME_MS = 3000; // 3 saniye geçmişi tut
        private const int MAVLINK_HEADER_SIZE = 8; // MAVLink v1 header (6) + CRC (2)

        private readonly object _lock = new object();
        public event EventHandler NewSysidCompid;

        private struct PacketRate
        {
            public DateTime Timestamp { get; }
            public int Value { get; }

            public PacketRate(DateTime timestamp, int value)
            {
                Timestamp = timestamp;
                Value = value;
            }
        }

        public List<byte> SeenSysid()
        {
            return toArray(_history.Keys).Select(id => GetFromID(id).sysid).ToList();
        }

        public List<byte> SeenCompid()
        {
            return toArray(_history.Keys).Select(id => GetFromID(id).compid).ToList();
        }

        public double GetMessageRate(byte sysid, byte compid, uint msgid)
        {
            var id = GetID(sysid, compid);
            var now = DateTime.UtcNow;
            var cutoff = now.AddMilliseconds(-MAX_HISTORY_TIME_MS);

            if (!_rate.TryGetValue(id, out var rates) || !rates.TryGetValue(msgid, out var queue))
                return 0;

            lock (_lock)
            {
                var packets = queue.Where(p => p.Timestamp >= cutoff).ToList();
                if (!packets.Any())
                    return 0;

                var timeSpan = (now - packets.Min(p => p.Timestamp)).TotalSeconds;
                if (timeSpan <= 0)
                    return 0;

                return packets.Count / timeSpan; // Paket sayısı / zaman = Hz
            }
        }

        public double GetBps(byte sysid, byte compid, uint msgid)
        {
            var id = GetID(sysid, compid);
            var now = DateTime.UtcNow;
            var cutoff = now.AddMilliseconds(-MAX_HISTORY_TIME_MS);

            if (!_bps.TryGetValue(id, out var rates) || !rates.TryGetValue(msgid, out var queue))
                return 0;

            lock (_lock)
            {
                var packets = queue.Where(p => p.Timestamp >= cutoff).ToList();
                if (!packets.Any())
                    return 0;

                var timeSpan = (now - packets.Min(p => p.Timestamp)).TotalSeconds;
                if (timeSpan <= 0)
                    return 0;

                var totalBits = packets.Sum(p => p.Value * 8); // Byte'ları bite çevir
                return totalBits / timeSpan; // Toplam bit / zaman = bps
            }
        }

        public double GetBps(byte sysid, byte compid)
        {
            var id = GetID(sysid, compid);
            var now = DateTime.UtcNow;
            var cutoff = now.AddMilliseconds(-MAX_HISTORY_TIME_MS);

            if (!_bps.TryGetValue(id, out var rates))
                return 0;

            lock (_lock)
            {
                var allPackets = rates.Values
                    .SelectMany(q => q.Where(p => p.Timestamp >= cutoff))
                    .ToList();

                if (!allPackets.Any())
                    return 0;

                var timeSpan = (now - allPackets.Min(p => p.Timestamp)).TotalSeconds;
                if (timeSpan <= 0)
                    return 0;

                var totalBits = allPackets.Sum(p => p.Value * 8);
                return totalBits / timeSpan;
            }
        }

        public double GetPacketRate(byte sysid, byte compid)
        {
            var id = GetID(sysid, compid);
            var now = DateTime.UtcNow;
            var cutoff = now.AddMilliseconds(-MAX_HISTORY_TIME_MS);

            if (!_rate.TryGetValue(id, out var rates))
                return 0;

            lock (_lock)
            {
                var allPackets = rates.Values
                    .SelectMany(q => q.Where(p => p.Timestamp >= cutoff))
                    .ToList();

                if (!allPackets.Any())
                    return 0;

                var timeSpan = (now - allPackets.Min(p => p.Timestamp)).TotalSeconds;
                if (timeSpan <= 0)
                    return 0;

                return allPackets.Count / timeSpan;
            }
        }

        public void Add(byte sysid, byte compid, uint msgid, T message, int size)
        {
            var id = GetID(sysid, compid);
            var now = DateTime.UtcNow;

            // Ana sözlükleri başlat
            _history.GetOrAdd(id, _ => new ConcurrentDictionary<uint, T>());
            _rate.GetOrAdd(id, _ => new ConcurrentDictionary<uint, ConcurrentQueue<PacketRate>>());
            _bps.GetOrAdd(id, _ => new ConcurrentDictionary<uint, ConcurrentQueue<PacketRate>>());

            // Mesaj geçmişini güncelle
            _history[id][msgid] = message;

            // Hız takibi için kuyruk
            var rateQueue = _rate[id].GetOrAdd(msgid, _ => new ConcurrentQueue<PacketRate>());
            rateQueue.Enqueue(new PacketRate(now, 1));
            CleanupQueue(rateQueue, now);

            // Bant genişliği takibi için kuyruk (header dahil toplam boyut)
            var totalSize = size + MAVLINK_HEADER_SIZE;
            var bpsQueue = _bps[id].GetOrAdd(msgid, _ => new ConcurrentQueue<PacketRate>());
            bpsQueue.Enqueue(new PacketRate(now, totalSize));
            CleanupQueue(bpsQueue, now);

            NewSysidCompid?.Invoke(this, EventArgs.Empty);
        }

        private void CleanupQueue(ConcurrentQueue<PacketRate> queue, DateTime now)
        {
            while (queue.TryPeek(out var oldest) &&
                   (now - oldest.Timestamp).TotalMilliseconds > MAX_HISTORY_TIME_MS)
            {
                queue.TryDequeue(out _);
            }
        }

        private IEnumerable<T1> toArray<T1>(IEnumerable<T1> input)
        {
            lock (_lock)
            {
                return input.ToArray();
            }
        }

        public IEnumerable<T> GetPacketMessages()
        {
            return toArray(_history.Values)
                .SelectMany(messages => toArray(messages.Values));
        }

        public void Clear()
        {
            lock (_lock)
            {
                foreach (var id in _history.Keys)
                {
                    _history[id].Clear();
                    _rate[id].Clear();
                    _bps[id].Clear();
                }
            }
            NewSysidCompid?.Invoke(this, EventArgs.Empty);
        }

        public void Clear(byte sysid, byte compid)
        {
            var id = GetID(sysid, compid);

            if (_history.TryGetValue(id, out var history))
                history.Clear();
            if (_rate.TryGetValue(id, out var rate))
                rate.Clear();
            if (_bps.TryGetValue(id, out var bps))
                bps.Clear();

            NewSysidCompid?.Invoke(this, EventArgs.Empty);
        }

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

        public bool TryGetLatestMessage(byte sysid, byte compid, uint msgid, out T message)
        {
            message = default;
            var id = GetID(sysid, compid);

            return _history.TryGetValue(id, out var messages) &&
                   messages.TryGetValue(msgid, out message);
        }
    }
}