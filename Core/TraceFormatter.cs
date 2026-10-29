using System.Globalization;
using System.Text;

namespace M10Modbus
{
    public enum TraceTone { Tx, Rx, Error, Warning }

    /// <summary>All journal line formats (TX/RX, decoded data, events, legend). Pure functions.</summary>
    public static class TraceFormatter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string Legend = "Обозначения: № — номер транзакции Modbus TCP (одинаков в запросе и ответе); " +
            "Id — адрес устройства (Unit Identifier, 7-й байт, как адрес в RTU); FC — код функции (hex); " +
            "адрес и регистров — dec (hex), адреса как в документе, без смещения −1; " +
            "HEX: 6 байт TCP-префикса (№, протокол 0, длина) → Id + PDU (= кадр RTU без CRC); " +
            "«период» — от предыдущего TX показаний этого прибора; «ответ через» — от отправки TX до последнего байта ответа; " +
            "инициализация (1000, 1100…) идёт подряд без периода.";

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
            (string description, TraceTone tone) = t.Direction == "TX" ? DescribeTx(t) : DescribeRx(t);
            string text = prefix + description;
            if (t.Bytes.Length > 0)
                text += Environment.NewLine + new string(' ', prefix.Length + 1) + FormatHex(t.Bytes);
            return (text, tone);
        }

        private static (string, TraceTone) DescribeTx(ModbusTrace t)
        {
            byte fc = t.Bytes.Length > 7 ? t.Bytes[7] : (byte)3;
            string text = $"TX №{t.TransactionId} Id={t.UnitId} FC={fc:X2} адрес={t.StartAddress} (0x{t.StartAddress:X4}) " +
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
            string request = $" · на запрос №{t.TransactionId}";
            if (!t.IsComplete)
            {
                string head = b.Length == 0 ? "RX НЕТ ОТВЕТА"
                    : "RX НЕПОЛНЫЙ" + (b.Length >= 2 ? $" №{(b[0] << 8) | b[1]}" : "") +
                      (t.ExpectedLength.HasValue ? $" получено {b.Length} из {t.ExpectedLength.Value} байт" : $" получено {b.Length} байт (длина неизвестна)");
                string aborted = t.ResponseMs.HasValue ? $" · прервано через {FormatMs(t.ResponseMs.Value)} мс" : " · прервано";
                return (head + request + aborted + (t.Note.Length > 0 ? ": " + t.Note : ""), TraceTone.Error);
            }

            string answered = t.ResponseMs.HasValue ? $" · ответ через {FormatMs(t.ResponseMs.Value)} мс" : "";
            string text = "RX" + (b.Length >= 2 ? $" №{(b[0] << 8) | b[1]}" : "") + (b.Length > 6 ? $" Id={b[6]}" : "");
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
        public static (string Text, bool IsError) FormatData(M10PollResult r, int device)
        {
            string prefix = Prefix(r.Timestamp, device);
            if (!r.IsConnected) return (prefix + "Ошибка обмена: " + r.ConnectionError, true);
            string text = prefix + $"Данные №{r.TransactionId} · Весы {r.ScaleNumber} | Errors=0x{r.ErrorCode:X4}: {r.GetMetrologicalErrorDescription()} | " +
                $"Status=0x{r.StatusMask:X4} | Stable={(r.IsStable ? 1 : 0)} Zero={(r.IsZero ? 1 : 0)} BelowMin={(r.IsBelowMinimum ? 1 : 0)} | " +
                $"Брутто={r.Brutto.ToString("G9", Inv)} Нетто={r.Netto.ToString("G9", Inv)} " +
                $"Тара={r.Tare.ToString("G9", Inv)} {r.Unit} | d={r.Division.ToString("G9", Inv)} | " +
                $"Диапазон={r.Range} DT_LastWeight={r.MeasurementUnixTime}" +
                (r.BruttoUnrounded.HasValue ? $" | Брутто без округления={r.BruttoUnrounded.Value.ToString("G9", Inv)} {r.Unit}" : "") +
                (r.UnroundedError.Length > 0 ? " | " + r.UnroundedError : "");
            return (text, r.HasScaleError || !r.HasValidWeight || r.UnroundedError.Length > 0);
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
