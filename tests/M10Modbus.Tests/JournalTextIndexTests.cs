namespace M10Modbus.Tests;

public class JournalTextIndexTests
{
    [Fact]
    public void TransactionSelection_IncludesTxRxDecodedAndEvents_ButNotNextExchange()
    {
        Guid exchange = Guid.NewGuid();
        var index = new JournalTextIndex();
        index.Append(new JournalAnchor(1, exchange), 20);
        index.Append(new JournalAnchor(2, exchange), 40);
        index.Append(new JournalAnchor(3, exchange), 30);
        var selectedEvent = new JournalAnchor(4, exchange);
        index.Append(selectedEvent, 15);
        index.Append(new JournalAnchor(5, Guid.NewGuid()), 20);
        var block = Assert.Single(index.TransactionRanges(selectedEvent));
        Assert.Equal(0, block.Start);
        Assert.Equal(105, block.Length);
        Assert.Equal(15, index.Find(selectedEvent)!.Length);
    }

    [Fact]
    public void InterleavedTransaction_DoesNotSelectOtherDeviceOrUnrelatedEvents()
    {
        Guid exchange = Guid.NewGuid();
        var index = new JournalTextIndex();
        var selectedEvent = new JournalAnchor(4, exchange);
        index.Append(new JournalAnchor(1, exchange), 20);
        index.Append(new JournalAnchor(2, Guid.NewGuid()), 30);
        index.Append(new JournalAnchor(3, exchange), 40);
        index.Append(selectedEvent, 15);
        index.Append(new JournalAnchor(5, Guid.Empty), 10);
        var blocks = index.TransactionRanges(selectedEvent);
        Assert.Equal(new[] { (0, 20), (50, 55) }, blocks.Select(block => (block.Start, block.Length)));
        Assert.Equal(10, Assert.Single(index.TransactionRanges(new JournalAnchor(5, Guid.Empty))).Length);
        Assert.Empty(index.TransactionRanges(new JournalAnchor(6, Guid.NewGuid())));
    }

    [Fact]
    public void ClickOnHeaderOrHexContinuation_IdentifiesTheSameEntry()
    {
        var index = new JournalTextIndex();
        var first = new JournalAnchor(1, Guid.NewGuid());
        var second = new JournalAnchor(2, Guid.NewGuid());
        index.Append(first, "TX header\n  HEX bytes\n".Length);
        index.Append(second, "RX header\n".Length);
        Assert.Equal(first, index.At(0)!.Anchor);
        Assert.Equal(first, index.At(15)!.Anchor);
        Assert.Equal(second, index.At(22)!.Anchor);
        Assert.Null(index.At(index.Length));
        Assert.Null(index.At(-1));
    }

    [Fact]
    public void Navigation_PrefersExactEvent_ThenRelatedExchange()
    {
        Guid exchange = Guid.NewGuid();
        var tx = new JournalAnchor(1, exchange);
        var reading = new JournalAnchor(2, exchange);
        var firstEvent = new JournalAnchor(3, exchange);
        var selectedEvent = new JournalAnchor(4, exchange);
        var all = new JournalTextIndex();
        foreach (var anchor in new[] { tx, reading, firstEvent, selectedEvent }) all.Append(anchor, 10);
        Assert.Equal(selectedEvent, all.Find(selectedEvent)!.Anchor);

        var decoded = new JournalTextIndex();
        decoded.Append(reading, 10);
        Assert.Equal(reading, decoded.Find(selectedEvent)!.Anchor);
        var events = new JournalTextIndex();
        events.Append(firstEvent, 10);
        events.Append(selectedEvent, 10);
        Assert.Equal(firstEvent, events.Find(reading)!.Anchor);
        Assert.Equal(selectedEvent, all.Find(selectedEvent)!.Anchor);
    }

    [Fact]
    public void Navigation_DoesNotMixDevicesRestartsOrWrappedTransactionNumbers()
    {
        var index = new JournalTextIndex();
        index.Append(new JournalAnchor(1, Guid.NewGuid()), 10);
        index.Append(new JournalAnchor(2, Guid.NewGuid()), 10);
        Assert.Null(index.Find(new JournalAnchor(3, Guid.NewGuid())));
        Assert.Null(index.Find(new JournalAnchor(4, Guid.Empty)));
    }

    [Fact]
    public void Trimming_RemovesWholeEntriesAndRebasesPositions()
    {
        var index = new JournalTextIndex();
        var removed = new JournalAnchor(1, Guid.Empty);
        var retained = new JournalAnchor(2, Guid.Empty);
        index.Append(removed, 15);
        index.Append(retained, 20);
        index.Append(new JournalAnchor(3, Guid.Empty), 10);
        Assert.Equal(15, index.Trim(30, 5000));
        Assert.Equal(30, index.Length);
        Assert.Equal(0, index.Find(retained)!.Start);
        Assert.Equal(retained, index.At(19)!.Anchor);
        Assert.Null(index.Find(removed));
        Assert.Equal(20, index.Trim(30, 1));
        Assert.Equal(1, index.Count);
        index.Clear();
        Assert.Null(index.Find(retained));
        Assert.Equal(0, index.Length);
    }
}
