namespace M10Modbus
{
    public enum LogView { All, Hex, Decoded, Events, Readings, Initialization }
    public enum LogKind { Hex, InitializationHex, Readings, Initialization, Event }

    public static class LogViewFilter
    {
        public static bool Includes(LogView view, LogKind kind) => view switch
        {
            LogView.All => true,
            LogView.Hex => kind is LogKind.Hex or LogKind.InitializationHex,
            LogView.Decoded => kind is LogKind.Readings or LogKind.Initialization,
            LogView.Events => kind == LogKind.Event,
            LogView.Readings => kind == LogKind.Readings,
            LogView.Initialization => kind is LogKind.Initialization or LogKind.InitializationHex,
            _ => throw new ArgumentOutOfRangeException(nameof(view))
        };
    }
}
