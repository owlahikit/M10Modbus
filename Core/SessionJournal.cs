using System.Text;
using System.Threading.Channels;
using System.Globalization;

namespace M10Modbus
{
    // File I/O runs separately from the UI. An unbounded queue avoids silently losing HEX frames.
    // At <= 10 polls/s per device this stays small on a healthy local disk.
    public sealed class SessionJournal : IAsyncDisposable
    {
        public static string CreateSessionDirectory(string root, DateTime time)
        {
            Directory.CreateDirectory(root);
            string temporary = Path.Combine(root, ".session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                // A nonempty destination cannot be replaced by a concurrent rename on Unix.
                File.WriteAllText(Path.Combine(temporary, "traffic.txt"), "");
                while (true)
                {
                    string directory = Path.Combine(root, time.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture));
                    try { Directory.Move(temporary, directory); return directory; }
                    catch (IOException) when (Directory.Exists(directory)) { time = time.AddMilliseconds(1); }
                }
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    File.Delete(Path.Combine(temporary, "traffic.txt"));
                    Directory.Delete(temporary);
                }
            }
        }

        private readonly Channel<(string File, string Text)> _queue = Channel.CreateUnbounded<(string, string)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private Task _writer = Task.CompletedTask;
        public string? Error { get; private set; }
        public void Start(string directory, Action<string> failed)
        {
            _writer = Task.Run(async () =>
            {
                var files = new Dictionary<string, StreamWriter>();
                try
                {
                    await foreach (var entry in _queue.Reader.ReadAllAsync())
                    {
                        if (!files.TryGetValue(entry.File, out var writer))
                        {
                            writer = new StreamWriter(Path.Combine(directory, entry.File), false, new UTF8Encoding(false)) { AutoFlush = true };
                            files.Add(entry.File, writer);
                        }
                        await writer.WriteLineAsync(entry.Text);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { Error = ex.Message; _queue.Writer.TryComplete(); failed(ex.Message); }
                finally
                {
                    foreach (var writer in files.Values)
                    {
                        try { await writer.DisposeAsync(); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        { if (Error == null) { Error = ex.Message; failed(ex.Message); } }
                    }
                }
            });
        }
        public void Write(string file, string text) { if (Error == null) _queue.Writer.TryWrite((file, text)); }
        public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); await _writer; }
    }
}
