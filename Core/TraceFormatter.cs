using System.Globalization;
using System.Text;

namespace M10Modbus
{
    public enum TraceTone { Tx, Rx, Error, Warning }

    /// <summary>All journal line formats (TX/RX, decoded data, events, legend). Pure functions.</summary>
    public static class TraceFormatter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string Legend = "Обозначения: число перед временем — общий номер транзакции журнала для обоих приборов (одинаков в TX, RX и расшифровке); " +
            "Id — адрес устройства (Unit Identifier, 7-й байт, как адрес в RTU); FC — код функции (hex); " +
            "адрес и регистров — dec (hex), адреса как в документе, без смещения −1; " +
            "HEX: 6 байт TCP-префикса (номер транзакции, протокол 0, длина) → Id + PDU (= кадр RTU без CRC); " +
            "реальный номер Modbus — первые 2 байта HEX, он не обязан совпадать с номером журнала; " +
            "«период» — от предыдущего TX показаний этого прибора; «ответ через» — от отправки TX до последнего байта ответа; " +
            "инициализация (1000, 1100…) идёт подряд без периода.";

        public static string WithJournalNumber(string text, long number)
        {
            int separator = text.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0 || !ushort.TryParse(text.AsSpan(0, separator), NumberStyles.None, Inv, out _))
                throw new ArgumentException("Ожидался номер транзакции перед временем.", nameof(text));
            string label = number.ToString(Inv);
            string[] lines = text.Split('\n');
            lines[0] = label + lines[0][separator..];
            int delta = label.Length - separator;
            for (int i = 1; i < lines.Length; i++)
            {
                if (delta >= 0) lines[i] = new string(' ', delta) + lines[i];
                else
                {
                    int remove = 0;
                    while (remove < -delta && remove < lines[i].Length && lines[i][remove] == ' ') remove++;
                    lines[i] = lines[i][remove..];
                }
            }
            return string.Join("\n", lines);
        }

        /// <summary>"HH:mm:ss.fff [Прибор N] " or "HH:mm:ss.fff [Система] ".</summary>
        public static string Prefix(DateTime time, int device) =>
            Prefix(time, device > 0 ? "Прибор " + device.ToString(Inv) : "Система");

        private static string Prefix(DateTime time, string source) => $"{time.ToString("HH:mm:ss.fff", Inv)} [{source}] ";

        /// <summary>Milliseconds with one decimal and a decimal comma, no thousands separator: 1000,3.</summary>
        public static string FormatMs(double ms) => ms.ToString("F1", Inv).Replace('.', ',');

        /// <summary>Space-separated bytes; " → " separates the 6-byte TCP prefix from Id + PDU.</summary>
        public static string FormatHex(ReadOnlySpan<byte> bytes)
        {
            var text = new StringBuilder(bytes.Length * 3 + 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i == 6) text.Append(" → ");
                else if (i > 0) text.Append(' ');
                text.Append(bytes[i].ToString("X2", Inv));
            }
            return text.ToString();
        }

        /// <param name="device">Device number; 0 labels the line with the trace endpoint (used by ToString()).</param>
        public static (string Text, TraceTone Tone) Format(ModbusTrace t, int device)
        {
            string prefix = device > 0 || t.Endpoint.Length == 0 ? Prefix(t.Timestamp, device) : Prefix(t.Timestamp, t.Endpoint);
            prefix = $"{t.TransactionId}: " + prefix;
            (string description, TraceTone tone) = t.Direction == "TX" ? DescribeTx(t) : DescribeRx(t);
            string text = prefix + description;
            if (t.Bytes.Length > 0)
                text += Environment.NewLine + new string(' ', prefix.Length + 1) + FormatHex(t.Bytes);
            return (text, tone);
        }

        private static (string, TraceTone) DescribeTx(ModbusTrace t)
        {
            byte fc = t.Bytes.Length > 7 ? t.Bytes[7] : (byte)3;
            string text = $"TX Id={t.UnitId} FC={fc:X2} адрес={t.StartAddress} (0x{t.StartAddress:X4}) " +
                $"регистров={t.RegisterCount} (0x{t.RegisterCount:X4}) · ";
            switch (t.Phase)
            {
                case TracePhase.Identification:
                    return (text + "ИНИЦИАЛИЗАЦИЯ 1/2: идентификация (тип, SN, FW, порядок слов)", TraceTone.Tx);
                case TracePhase.ScaleConfig:
                    return (text + $"ИНИЦИАЛИЗАЦИЯ 2/2: конфигурация весов {t.ScaleNumber} (SN весов, единица)", TraceTone.Tx);
                default:
                    text += "показания · " + (t.PeriodMs.HasValue ? $"период {FormatMs(t.PeriodMs.Value)} мс" : "первый после инициализации");
                    if (!t.Overrun) return (text, TraceTone.Tx);
                    return (text + $" · ПРЕВЫШЕН (задан {FormatPlainMs(t.PlannedPeriodMs)} мс)", TraceTone.Warning);
            }
        }

        private static (string, TraceTone) DescribeRx(ModbusTrace t)
        {
            byte[] b = t.Bytes;
            int? receivedId = b.Length >= 2 ? (b[0] << 8) | b[1] : null;
            string request = receivedId.HasValue && receivedId != t.TransactionId ? $" · номер ответа={receivedId}" : "";
            if (!t.IsComplete)
            {
                string head = b.Length == 0 ? "RX НЕТ ОТВЕТА"
                    : "RX НЕПОЛНЫЙ" +
                      (t.ExpectedLength.HasValue ? $" получено {b.Length} из {t.ExpectedLength.Value} байт" : $" получено {b.Length} байт (длина неизвестна)");
                string aborted = t.ResponseMs.HasValue ? $" · прервано через {FormatMs(t.ResponseMs.Value)} мс" : " · прервано";
                return (head + request + aborted + (t.Note.Length > 0 ? ": " + t.Note : ""), TraceTone.Error);
            }

            string answered = t.ResponseMs.HasValue ? $" · ответ через {FormatMs(t.ResponseMs.Value)} мс" : "";
            string text = "RX" + (b.Length > 6 ? $" Id={b[6]}" : "");
            if (b.Length <= 7) return (text + request + answered, TraceTone.Rx);
            byte fc = b[7];
            text += $" FC={fc:X2}";
            if ((fc & 0x80) != 0)
            {
                string exception = b.Length > 8
                    ? $" ИСКЛЮЧЕНИЕ к FC={fc & 0x7F:X2}: код {b[8]:X2} — {M10ModbusClient.ExceptionDescription(b[8])}"
                    : $" ИСКЛЮЧЕНИЕ к FC={fc & 0x7F:X2}";
                return (text + exception + request + answered, TraceTone.Error);
            }
            if (b.Length > 8) text += $" байт данных={b[8]} (0x{b[8]:X2})";
            if (t.Note.Length > 0) return (text + request + answered + " · ОТВЕРГНУТ: " + t.Note, TraceTone.Error);
            return (text + request + answered, TraceTone.Rx);
        }

        private static string FormatPlainMs(double ms) =>
            Math.Abs(ms - Math.Round(ms)) < 1e-9 ? Math.Round(ms).ToString("F0", Inv) : FormatMs(ms);

        /// <summary>Decoded current-weight line (or the exchange error) of one poll result.</summary>
        public static (string Text, bool IsError) FormatData(M10PollResult r, int device, bool rawValues = false)
        {
            string Number(float value) => rawValues ? value.ToString("G9", Inv) : M10PollResult.FormatNumber(value, Inv);
            string prefix = Prefix(r.Timestamp, device);
            if (!r.IsConnected) return ((r.ExchangeId != Guid.Empty ? $"{r.TransactionId}: " : "") +
                prefix + "Ошибка обмена: " + r.ConnectionError, true);
            string text = $"{r.TransactionId}: {prefix}Весы {r.ScaleNumber}: " +
                $"Брутто={Number(r.Brutto)} Нетто={Number(r.Netto)} " +
                $"Тара={Number(r.Tare)} {r.Unit} d={Number(r.Division)} | " +
                $"Диапазон={r.Range} | Stable={(r.IsStable ? 1 : 0)} Zero={(r.IsZero ? 1 : 0)} " +
                $"BelowMin={(r.IsBelowMinimum ? 1 : 0)} | {r.GetMetrologicalErrorDescription()}";
            return (text, r.HasScaleError || !r.HasValidWeight);
        }

        public static (string Text, bool IsError)? FormatInitializationData(ModbusTrace t, int device)
        {
            if (t.Direction != "RX" || t.Phase == TracePhase.Poll || !t.IsComplete || t.Note.Length > 0)
                return null;
            int count = t.Phase == TracePhase.Identification ? 9 : 22;
            byte[] b = t.Bytes;
            if (t.RegisterCount != count || b.Length != 9 + 2 * count ||
                b[7] != 3 || b[8] != 2 * count || b[6] != t.UnitId ||
                ((b[0] << 8) | b[1]) != t.TransactionId || b[2] != 0 || b[3] != 0 ||
                ((b[4] << 8) | b[5]) != b.Length - 6)
                return null;
            var words = new ushort[count];
            for (int i = 0; i < count; i++) words[i] = (ushort)((b[9 + 2 * i] << 8) | b[10 + 2 * i]);
            string prefix = $"{t.TransactionId}: " + Prefix(t.Timestamp, device);
            if (t.Phase == TracePhase.Identification)
            {
                string text = prefix + $"Идентификация | Тип={words[0]} ({(words[0] == 100 ? "M10" : "неизвестный")}) | SN={words[1]}";
                if (words[8] > 1)
                    return (text + $" | Endians={words[8]}: неизвестный порядок слов, FW и DT не расшифрованы", true);
                bool lowWordFirst = words[8] == 1;
                uint firmware = M10ModbusClient.DecodeUInt32(words, 2, lowWordFirst);
                text += $" | FW={firmware >> 16}.{firmware & 0xFFFF:D3}" +
                    $" | DT_Current={M10ModbusClient.DecodeUInt32(words, 4, lowWordFirst)} | DT_Boot={M10ModbusClient.DecodeUInt32(words, 6, lowWordFirst)}" +
                    $" | Endians={words[8]} ({(lowWordFirst ? "младшее" : "старшее")} слово первым)";
                bool invalid = words[0] != 100;
                return (text + (invalid ? " | НЕКОРРЕКТНО: ожидался M10 (100)" : ""), invalid);
            }

            string unit = words[1] switch { 0 => "кг", 1 => "т", _ => "неизвестная" };
            string rangeType = words[2] switch
            {
                0 => "однодиапазонные", 1 => "многоинтервальные", 2 => "многодиапазонные", _ => "неизвестный"
            };
            var config = new StringBuilder(prefix + $"Конфигурация · Весы {t.ScaleNumber}" +
                $" | SN весов={words[0]} | Единица={words[1]} ({unit}) | Тип диапазонов={words[2]} ({rangeType}) | Диапазонов={words[3]}");
            bool invalidConfig = words[1] > 1 || words[2] > 2 || words[3] < 1 || words[3] > 3;
            if (words[3] is >= 1 and <= 3)
            {
                for (int i = 0; i < words[3]; i++)
                {
                    int offset = 4 + 6 * i;
                    float min = M10ModbusClient.DecodeFloat(words, offset, t.LowWordFirst);
                    float max = M10ModbusClient.DecodeFloat(words, offset + 2, t.LowWordFirst);
                    float d = M10ModbusClient.DecodeFloat(words, offset + 4, t.LowWordFirst);
                    config.Append($" | Диапазон {i + 1}: Min={M10PollResult.FormatNumber(min, Inv)} Max={M10PollResult.FormatNumber(max, Inv)} d={M10PollResult.FormatNumber(d, Inv)}");
                    invalidConfig |= !float.IsFinite(min) || !float.IsFinite(max) || !float.IsFinite(d) ||
                        min < 0 || max < min || d <= 0;
                }
            }
            if (invalidConfig) config.Append(" | НЕКОРРЕКТНО: единица или параметры диапазонов — проверьте настройки");
            return (config.ToString(), invalidConfig);
        }

        /// <summary>Device event: "HH:mm:ss.fff [Прибор N] 10.0.1.18:502 | text".</summary>
        public static string FormatEvent(DateTime time, int device, string endpoint, string text) =>
            Prefix(time, device) + (endpoint.Length > 0 ? endpoint + " | " : "") + text;

        public static string FormatSystem(DateTime time, string text) => Prefix(time, 0) + text;

        public static string SessionOpened(DateTime time, string directory) => FormatSystem(time, "Сессия журнала открыта: " + directory);

        public static string SessionClosed(DateTime time) => FormatSystem(time, "Сессия журнала закрыта.");

        public static string LegendLine(DateTime time) => FormatSystem(time, Legend);
    }
}
