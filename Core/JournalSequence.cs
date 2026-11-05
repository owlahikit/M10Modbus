namespace M10Modbus
{
    public readonly record struct JournalGroup(long Order, long? Number);

    public sealed class JournalSequence
    {
        private readonly Dictionary<Guid, JournalGroup> _groups = new();
        private readonly Queue<Guid> _recent = new();
        private long _order, _number;

        public JournalGroup Get(Guid exchangeId)
        {
            if (exchangeId == Guid.Empty) return new JournalGroup(++_order, null);
            if (_groups.TryGetValue(exchangeId, out var group)) return group;
            group = new JournalGroup(++_order, ++_number);
            _groups.Add(exchangeId, group);
            _recent.Enqueue(exchangeId);
            // More than the maximum number of concurrent requests and the screen buffer combined.
            while (_recent.Count > 5000) _groups.Remove(_recent.Dequeue());
            return group;
        }
    }
}
