using System.Text.Json;
using System.Text.Json.Serialization;

namespace M10Modbus
{
    /// <summary>settings.json. JSON names of the fields of earlier versions are kept unchanged;
    /// unknown fields are ignored, so old and new versions can read each other's file.</summary>
    public sealed class AppSettings
    {
        public const int DefaultCardsHeight = 330;

        public DeviceSettings First { get; set; } = new DeviceSettings { Ip = "10.0.1.18" };
        public DeviceSettings Second { get; set; } = new DeviceSettings { Ip = "10.0.1.8" };

        /// <summary>Written as the state of «Опрос» of device 2; ignored on reading (polling never starts by itself).</summary>
        [JsonPropertyName("SecondEnabled")]
        public bool SecondEnabled { get; set; }

        [JsonPropertyName("Latch")]
        public bool Latch { get; set; } = true;

        [JsonPropertyName("PeriodMs")]
        public int PeriodMs { get; set; } = 250;

        [JsonPropertyName("TimeoutMs")]
        public int TimeoutMs { get; set; } = 1500;

        /// <summary>Restore bounds in physical pixels; null — center on screen with the default size.</summary>
        public WindowSettings? Window { get; set; }

        /// <summary>Height of the device cards area (splitter position), logical pixels (96 DPI).</summary>
        public int CardsHeight { get; set; } = DefaultCardsHeight;

        public int LogView { get; set; }

        public int LogDevice { get; set; }

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        /// <exception cref="JsonException">Malformed file.</exception>
        public static AppSettings FromJson(string json) => JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

        public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

        /// <summary>Returns null when the file does not exist.</summary>
        public static AppSettings? Load(string file) => File.Exists(file) ? FromJson(File.ReadAllText(file)) : null;

        /// <summary>Atomic write: temporary file, then replace.</summary>
        public void Save(string file)
        {
            string? directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(file + ".tmp", ToJson());
            File.Move(file + ".tmp", file, true);
        }
    }

    public sealed class DeviceSettings
    {
        public string Ip { get; set; } = "";
        public int Port { get; set; } = 502;
        public int UnitId { get; set; } = 1;
        public int Scale { get; set; } = 1;
        public bool ReadUnrounded { get; set; }
        public int? Digits { get; set; }
        public string UnroundedAddress { get; set; } = "";
    }

    public sealed class WindowSettings
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
    }
}
