namespace M10Modbus.Tests;

public class SessionJournalTests
{
    [Fact]
    public async Task SessionNames_ContainOnlyTime_AndCollisionsDoNotOverwriteLogs()
    {
        string root = Path.Combine(Path.GetTempPath(), "M10-journal-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTime(2026, 11, 5, 12, 52, 12, 147);
            string first = SessionJournal.CreateSessionDirectory(root, now);
            await File.WriteAllTextAsync(Path.Combine(first, "traffic.txt"), "existing");
            string[] others = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() => SessionJournal.CreateSessionDirectory(root, now))));
            Assert.Equal("2026-11-05_12-52-12-147", Path.GetFileName(first));
            Assert.Equal(5, others.Append(first).Distinct().Count());
            Assert.All(others, directory =>
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3}$", Path.GetFileName(directory)));
            Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(first, "traffic.txt")));
            Assert.DoesNotContain(Directory.GetDirectories(root), directory => Path.GetFileName(directory).StartsWith("."));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
