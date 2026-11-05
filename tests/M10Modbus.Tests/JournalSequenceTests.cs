namespace M10Modbus.Tests;

public class JournalSequenceTests
{
    [Fact]
    public void DevicesAndRestartsShareOneSequence_WhileAllPartsKeepTheirNumber()
    {
        var sequence = new JournalSequence();
        JournalGroup start = sequence.Get(Guid.Empty);
        Guid device1 = Guid.NewGuid(), device2 = Guid.NewGuid(), restarted = Guid.NewGuid();
        JournalGroup first = sequence.Get(device1);
        JournalGroup second = sequence.Get(device2);
        Assert.Null(start.Number);
        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.Equal(first, sequence.Get(device1));
        Assert.Equal(second, sequence.Get(device2));
        JournalGroup eventWithoutTransaction = sequence.Get(Guid.Empty);
        Assert.Null(eventWithoutTransaction.Number);
        Assert.Equal(3, sequence.Get(restarted).Number);
        Assert.True(start.Order < first.Order && first.Order < second.Order &&
            second.Order < eventWithoutTransaction.Order);
    }

    [Fact]
    public void LateResponseIsInsertedIntoItsBlock_NotAfterTheOtherDevice()
    {
        var index = new JournalTextIndex();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var tx1 = new JournalAnchor(1, first) { BlockOrder = 1 };
        var tx2 = new JournalAnchor(2, second) { BlockOrder = 2 };
        var rx1 = new JournalAnchor(3, first) { BlockOrder = 1 };
        var event1 = new JournalAnchor(4, first) { BlockOrder = 1 };
        index.Append(tx1, 20);
        index.Append(tx2, 30);
        Assert.Equal(20, index.InsertionPosition(rx1));
        index.Insert(rx1, index.InsertionPosition(rx1), 40);
        index.Insert(event1, index.InsertionPosition(event1), 15);
        Assert.Equal(75, index.Find(tx2)!.Start);
        Assert.Equal(20, index.Find(rx1)!.Start);
        Assert.Equal(60, index.Find(event1)!.Start);
        Assert.Equal(75, Assert.Single(index.TransactionRanges(event1)).Length);
        Assert.Equal(tx2, index.At(75)!.Anchor);
        Assert.Equal(105, index.Length);
        Assert.Throws<ArgumentException>(() => index.Insert(rx1, 1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Insert(rx1, 106, 10));
    }
}
