namespace M10Modbus.Tests;

public class LogViewTests
{
    [Theory]
    [InlineData(LogView.All, LogKind.Hex, LogKind.InitializationHex, LogKind.Readings, LogKind.Initialization, LogKind.Event)]
    [InlineData(LogView.Hex, LogKind.Hex, LogKind.InitializationHex)]
    [InlineData(LogView.Decoded, LogKind.Readings, LogKind.Initialization)]
    [InlineData(LogView.Events, LogKind.Event)]
    [InlineData(LogView.Readings, LogKind.Readings)]
    [InlineData(LogView.Initialization, LogKind.InitializationHex, LogKind.Initialization)]
    public void Views_IncludeOnlyTheirEntryKinds(LogView view, params LogKind[] expected)
    {
        foreach (LogKind kind in Enum.GetValues<LogKind>())
            Assert.Equal(expected.Contains(kind), LogViewFilter.Includes(view, kind));
    }

    [Fact]
    public void SavedViewIndices_KeepTheirExistingMeaning()
    {
        Assert.Equal(0, (int)LogView.All);
        Assert.Equal(1, (int)LogView.Hex);
        Assert.Equal(2, (int)LogView.Decoded);
        Assert.Equal(3, (int)LogView.Events);
        Assert.Equal(4, (int)LogView.Readings);
        Assert.Equal(5, (int)LogView.Initialization);
    }
}
