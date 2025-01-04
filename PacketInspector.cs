using System.Collections.Concurrent;

namespace MavlinkInspector
{
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
            return SeenRate(sysid, compid, msgid);
        }

        public double SeenRate(byte sysid, byte compid, uint msgid)
        {
            var id = GetID(sysid, compid);
            var end = DateTime.Now;
            var start = end.AddSeconds(-3);

            lock (_lock)
            {
                if (!_rate.TryGetValue(id, out var rates) || !rates.TryGetValue(msgid, out var data))
                    return 0;

                try
                {
                    var starttime = data.First().dateTime;
                    starttime = starttime < start ? start : starttime;
                    var msgrate = data.Where(a => a.dateTime > start && a.dateTime < end)
                                    .Sum(a => a.value / (end - starttime).TotalSeconds);
                    return msgrate;
                }
                catch
                {
                    return 0;
                }
            }
        }

        public double GetBps(byte sysid, byte compid, uint msgid)
        {
            var id = GetID(sysid, compid);
            var end = DateTime.Now;
            var start = end.AddSeconds(-3);

            lock (_lock)
            {
                if (!_bps.TryGetValue(id, out var rates) || !rates.TryGetValue(msgid, out var data))
                    return 0;

                try
                {
                    var starttime = data.First().dateTime;
                    starttime = starttime < start ? start : starttime;
                    var msgbps = data.Where(a => a.dateTime > start && a.dateTime < end)
                                    .Sum(a => a.value / (end - starttime).TotalSeconds);
                    return msgbps * 8; // Convert to bits
                }
                catch
                {
                    return 0;
                }
            }
        }

        public double GetBps(byte sysid, byte compid)
        {
            var id = GetID(sysid, compid);
            var end = DateTime.Now;
            var start = end.AddSeconds(-3);

            lock (_lock)
            {
                if (!_bps.TryGetValue(id, out var rates))
                    return 0;

                try
                {
                    var data = rates.Values.SelectMany(list => list).ToList();
                    var starttime = data.First().dateTime;
                    starttime = starttime < start ? start : starttime;
                    var msgbps = data.Where(a => a.dateTime > start && a.dateTime < end)
                                    .Sum(a => a.value / (end - starttime).TotalSeconds);
                    return msgbps * 8; // Convert to bits
                }
                catch
                {
                    return 0;
                }
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
                    .SelectMany(q => q.Where(p => p.dateTime >= cutoff))
                    .ToList();

                if (!allPackets.Any())
                    return 0;

                var timeSpan = (now - allPackets.Min(p => p.dateTime)).TotalSeconds;
                if (timeSpan <= 0)
                    return 0;

                return allPackets.Count / timeSpan;
            }
        }

        public void Add(byte sysid, byte compid, uint msgid, T message, int size)
        {
            var id = GetID(sysid, compid);
            var now = DateTime.Now;

            lock (_lock)
            {
                // Initialize dictionaries for new ID
                if (!_history.ContainsKey(id))
                    Clear(sysid, compid);

                // Update message history
                _history.GetOrAdd(id, _ => new ConcurrentDictionary<uint, T>())[msgid] = message;

                // Update rate tracking
                var rateDict = _rate.GetOrAdd(id, _ => new ConcurrentDictionary<uint, List<irate>>());
                if (!rateDict.ContainsKey(msgid))
                    rateDict[msgid] = new List<irate>();
                rateDict[msgid].Add(new irate(now, 1));

                // Update bandwidth tracking
                var bpsDict = _bps.GetOrAdd(id, _ => new ConcurrentDictionary<uint, List<irate>>());
                if (!bpsDict.ContainsKey(msgid))
                    bpsDict[msgid] = new List<irate>();
                bpsDict[msgid].Add(new irate(now, size + 8)); // Include MAVLink overhead

                // Cleanup old entries
                while (rateDict[msgid].Count > RateHistory)
                    rateDict[msgid].RemoveAt(0);
                while (bpsDict[msgid].Count > RateHistory)
                    bpsDict[msgid].RemoveAt(0);
            }

            NewSysidCompid?.Invoke(this, EventArgs.Empty);
        }

        private void CleanupQueue(ConcurrentQueue<irate> queue, DateTime now)
        {
            while (queue.TryPeek(out var oldest) &&
                   (now - oldest.dateTime).TotalMilliseconds > MAX_HISTORY_TIME_MS)
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
                // Dictionary'leri yeniden oluşturmak yerine içeriklerini temizle
                foreach (var dict in _history.Values)
                    dict.Clear();

                foreach (var dict in _rate.Values)
                    dict.Clear();

                foreach (var dict in _bps.Values)
                    dict.Clear();

                // Ana dictionary yapılarını koru
                // Yeni mesajlar geldiğinde kullanılacak
            }

            // Event'i tetikle ama null ile değil
            NewSysidCompid?.Invoke(this, EventArgs.Empty);
        }

        public void Clear(byte sysid, byte compid)
        {
            var id = GetID(sysid, compid);
            lock (_lock)
            {
                // Ana dictionary'leri oluştur (yoksa)
                _history.GetOrAdd(id, _ => new ConcurrentDictionary<uint, T>());
                _rate.GetOrAdd(id, _ => new ConcurrentDictionary<uint, List<irate>>());
                _bps.GetOrAdd(id, _ => new ConcurrentDictionary<uint, List<irate>>());

                // İçerikleri temizle
                if (_history.TryGetValue(id, out var history))
                    history.Clear();
                if (_rate.TryGetValue(id, out var rate))
                    rate.Clear();
                if (_bps.TryGetValue(id, out var bps))
                    bps.Clear();
            }
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