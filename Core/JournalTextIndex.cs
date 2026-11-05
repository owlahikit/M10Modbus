namespace M10Modbus
{
    public readonly record struct JournalAnchor(long EntryId, Guid ExchangeId)
    {
        public long BlockOrder { get; init; }
    }

    public sealed record JournalTextRange(JournalAnchor Anchor, int Start, int Length);

    /// <summary>Maps rendered characters to records without parsing visible transaction numbers.</summary>
    public sealed class JournalTextIndex
    {
        private readonly List<JournalTextRange> _rows = new();
        public int Count => _rows.Count;
        public int Length { get; private set; }

        public void Append(JournalAnchor anchor, int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            _rows.Add(new JournalTextRange(anchor, Length, length));
            Length += length;
        }

        public int InsertionPosition(JournalAnchor anchor) =>
            _rows.FirstOrDefault(row => row.Anchor.BlockOrder > anchor.BlockOrder)?.Start ?? Length;

        public void Insert(JournalAnchor anchor, int position, int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            if (position < 0 || position > Length) throw new ArgumentOutOfRangeException(nameof(position));
            int index = _rows.FindIndex(row => row.Start >= position);
            if (index < 0)
            {
                if (position != Length) throw new ArgumentException("Вставка должна быть на границе записи.", nameof(position));
                Append(anchor, length); return;
            }
            if (_rows[index].Start != position) throw new ArgumentException("Вставка должна быть на границе записи.", nameof(position));
            for (int i = index; i < _rows.Count; i++)
                _rows[i] = _rows[i] with { Start = _rows[i].Start + length };
            _rows.Insert(index, new JournalTextRange(anchor, position, length));
            Length += length;
        }

        public JournalTextRange? At(int position) =>
            _rows.FirstOrDefault(row => position >= row.Start && position < row.Start + row.Length);

        public JournalTextRange? Find(JournalAnchor anchor) =>
            _rows.FirstOrDefault(row => row.Anchor.EntryId == anchor.EntryId) ??
            (anchor.ExchangeId == Guid.Empty ? null :
                _rows.FirstOrDefault(row => row.Anchor.ExchangeId == anchor.ExchangeId));

        public IReadOnlyList<JournalTextRange> TransactionRanges(JournalAnchor anchor)
        {
            var ranges = new List<JournalTextRange>();
            foreach (var row in _rows.Where(row => anchor.ExchangeId == Guid.Empty
                ? row.Anchor.EntryId == anchor.EntryId : row.Anchor.ExchangeId == anchor.ExchangeId))
            {
                if (ranges.Count > 0 && ranges[^1].Start + ranges[^1].Length == row.Start)
                    ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + row.Length };
                else ranges.Add(row);
            }
            return ranges;
        }

        public int Trim(int maxCharacters, int maxEntries)
        {
            int count = 0, cut = 0;
            while (_rows.Count - count > 1 &&
                (Length - cut > maxCharacters || _rows.Count - count > maxEntries))
            {
                cut += _rows[count++].Length;
            }
            if (count == 0) return 0;
            _rows.RemoveRange(0, count);
            for (int i = 0; i < _rows.Count; i++)
                _rows[i] = _rows[i] with { Start = _rows[i].Start - cut };
            Length -= cut;
            return cut;
        }

        public void Clear() { _rows.Clear(); Length = 0; }
    }
}
