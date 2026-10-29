using System.Globalization;

namespace M10Modbus.Tests;

/// <summary>Line formats of design.md 4.1–4.6 (exact samples of 4.3).</summary>
public class TraceFormatterTests
{
    private static readonly string Indent = new(' ', 25);
    private static readonly string NL = Environment.NewLine;

    private static DateTime T(string hhmmssfff) =>
        DateTime.ParseExact("2026-10-29 " + hhmmssfff, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static byte[] Hex(string s) =>
        s.Replace("→", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray();

    private static ModbusTrace Tx(string time, ushort tid, ushort start, ushort count, TracePhase phase, int scale = 1,
        double? period = null, double planned = 0, bool overrun = false) => new()
    {
        Timestamp = T(time), Direction = "TX", Endpoint = "10.0.1.18:502", TransactionId = tid, UnitId = 1,
        StartAddress = start, RegisterCount = count, Phase = phase, ScaleNumber = scale,
        PeriodMs = period, PlannedPeriodMs = planned, Overrun = overrun,
        Bytes = new byte[] { (byte)(tid >> 8), (byte)tid, 0, 0, 0, 6, 1, 3, (byte)(start >> 8), (byte)start, (byte)(count >> 8), (byte)count }
    };

    private static ModbusTrace Rx(string time, ushort tid, string hex, double? responseMs, bool complete = true,
        int? expected = null, string note = "", ushort start = 2000, ushort count = 13) => new()
    {
        Timestamp = T(time), Direction = "RX", Endpoint = "10.0.1.18:502", TransactionId = tid, UnitId = 1,
        StartAddress = start, RegisterCount = count, Bytes = Hex(hex), IsComplete = complete,
        ResponseMs = responseMs, ExpectedLength = expected, Note = note
    };

    [Fact]
    public void Init_TxRx_MatchSamples()
    {
        var tx1 = TraceFormatter.Format(Tx("21:47:31.140", 1, 1000, 9, TracePhase.Identification), 1);
        Assert.Equal("21:47:31.140 [Прибор 1] TX №1 Id=1 FC=03 адрес=1000 (0x03E8) регистров=9 (0x0009) · ИНИЦИАЛИЗАЦИЯ 1/2: идентификация (тип, SN, FW, порядок слов)"
            + NL + Indent + "00 01 00 00 00 06 → 01 03 03 E8 00 09", tx1.Text);
        Assert.Equal(TraceTone.Tx, tx1.Tone);

        var rx1 = TraceFormatter.Format(Rx("21:47:31.152", 1,
            "00 01 00 00 00 15 01 03 12 00 64 30 39 00 01 00 05 00 00 00 00 00 00 00 00 00 00", 11.8, start: 1000, count: 9), 1);
        Assert.Equal("21:47:31.152 [Прибор 1] RX №1 Id=1 FC=03 байт данных=18 (0x12) · на запрос №1 · ответ через 11,8 мс"
            + NL + Indent + "00 01 00 00 00 15 → 01 03 12 00 64 30 39 00 01 00 05 00 00 00 00 00 00 00 00 00 00", rx1.Text);
        Assert.Equal(TraceTone.Rx, rx1.Tone);

        var tx2 = TraceFormatter.Format(Tx("21:47:31.153", 2, 1100, 4, TracePhase.ScaleConfig), 1);
        Assert.Equal("21:47:31.153 [Прибор 1] TX №2 Id=1 FC=03 адрес=1100 (0x044C) регистров=4 (0x0004) · ИНИЦИАЛИЗАЦИЯ 2/2: конфигурация весов 1 (SN весов, единица)"
            + NL + Indent + "00 02 00 00 00 06 → 01 03 04 4C 00 04", tx2.Text);

        var rx2 = TraceFormatter.Format(Rx("21:47:31.164", 2, "00 02 00 00 00 0B 01 03 08 00 7B 00 00 00 00 00 00", 10.9, start: 1100, count: 4), 1);
        Assert.Equal("21:47:31.164 [Прибор 1] RX №2 Id=1 FC=03 байт данных=8 (0x08) · на запрос №2 · ответ через 10,9 мс"
            + NL + Indent + "00 02 00 00 00 0B → 01 03 08 00 7B 00 00 00 00 00 00", rx2.Text);
    }

    [Fact]
    public void ScaleConfig_Scale2_Uses1133()
    {
        var tx = TraceFormatter.Format(Tx("21:47:31.153", 2, 1133, 4, TracePhase.ScaleConfig, scale: 2), 2);
        Assert.Equal("21:47:31.153 [Прибор 2] TX №2 Id=1 FC=03 адрес=1133 (0x046D) регистров=4 (0x0004) · ИНИЦИАЛИЗАЦИЯ 2/2: конфигурация весов 2 (SN весов, единица)"
            + NL + Indent + "00 02 00 00 00 06 → 01 03 04 6D 00 04", tx.Text);
    }

    [Fact]
    public void PollTx_FirstAfterInit_Period_Overrun()
    {
        var first = TraceFormatter.Format(Tx("21:47:31.165", 3, 2000, 13, TracePhase.Poll), 1);
        Assert.Equal("21:47:31.165 [Прибор 1] TX №3 Id=1 FC=03 адрес=2000 (0x07D0) регистров=13 (0x000D) · показания · первый после инициализации"
            + NL + Indent + "00 03 00 00 00 06 → 01 03 07 D0 00 0D", first.Text);

        var periodic = TraceFormatter.Format(Tx("21:47:35.677", 21, 2000, 13, TracePhase.Poll, period: 1000.3, planned: 1000), 1);
        Assert.Equal("21:47:35.677 [Прибор 1] TX №21 Id=1 FC=03 адрес=2000 (0x07D0) регистров=13 (0x000D) · показания · период 1000,3 мс"
            + NL + Indent + "00 15 00 00 00 06 → 01 03 07 D0 00 0D", periodic.Text);
        Assert.Equal(TraceTone.Tx, periodic.Tone);

        var overrun = TraceFormatter.Format(Tx("21:47:45.043", 30, 2000, 13, TracePhase.Poll, period: 1043.7, planned: 1000, overrun: true), 1);
        Assert.Equal("21:47:45.043 [Прибор 1] TX №30 Id=1 FC=03 адрес=2000 (0x07D0) регистров=13 (0x000D) · показания · период 1043,7 мс · ПРЕВЫШЕН (задан 1000 мс)"
            + NL + Indent + "00 1E 00 00 00 06 → 01 03 07 D0 00 0D", overrun.Text);
        Assert.Equal(TraceTone.Warning, overrun.Tone);
    }

    [Fact]
    public void PollRx_35Bytes_MatchesSample()
    {
        const string hex = "00 15 00 00 00 1D 01 03 1A 67 20 1A 2B 00 00 00 02 00 01 3F 00 00 00 44 9A 50 00 44 9A 50 00 00 00 00 00";
        Assert.Equal(35, Hex(hex).Length);
        var rx = TraceFormatter.Format(Rx("21:47:35.703", 21, hex, 26.1), 1);
        Assert.Equal("21:47:35.703 [Прибор 1] RX №21 Id=1 FC=03 байт данных=26 (0x1A) · на запрос №21 · ответ через 26,1 мс"
            + NL + Indent + "00 15 00 00 00 1D → 01 03 1A 67 20 1A 2B 00 00 00 02 00 01 3F 00 00 00 44 9A 50 00 44 9A 50 00 00 00 00 00", rx.Text);
    }

    [Fact]
    public void Rx_TidMismatch_ShowsBothNumbers()
    {
        var rx = TraceFormatter.Format(Rx("21:47:35.703", 21, "00 16 00 00 00 1D 01 03 1A", 5.0), 1);
        Assert.StartsWith("21:47:35.703 [Прибор 1] RX №22 Id=1 FC=03 байт данных=26 (0x1A) · на запрос №21 · ответ через 5,0 мс", rx.Text);
    }

    [Fact]
    public void Rx_Exception83_Code02()
    {
        var rx = TraceFormatter.Format(Rx("21:47:36.690", 22, "00 16 00 00 00 03 01 83 02", 12.4), 1);
        Assert.Equal("21:47:36.690 [Прибор 1] RX №22 Id=1 FC=83 ИСКЛЮЧЕНИЕ к FC=03: код 02 — недопустимый адрес регистра · на запрос №22 · ответ через 12,4 мс"
            + NL + Indent + "00 16 00 00 00 03 → 01 83 02", rx.Text);
        Assert.Equal(TraceTone.Error, rx.Tone);
    }

    [Fact]
    public void Rx_Incomplete_9of35_4bytes_NoAnswer()
    {
        var part = TraceFormatter.Format(Rx("21:47:39.180", 23, "00 17 00 00 00 1D 01 03 1A", 1500.0, complete: false, expected: 35,
            note: "нет полного ответа за 1500 мс"), 1);
        Assert.Equal("21:47:39.180 [Прибор 1] RX НЕПОЛНЫЙ №23 получено 9 из 35 байт · на запрос №23 · прервано через 1500,0 мс: нет полного ответа за 1500 мс"
            + NL + Indent + "00 17 00 00 00 1D → 01 03 1A", part.Text);
        Assert.Equal(TraceTone.Error, part.Tone);

        var four = TraceFormatter.Format(Rx("21:47:41.200", 24, "00 18 00 00", 3.2, complete: false, note: "прибор закрыл TCP-соединение"), 1);
        Assert.Equal("21:47:41.200 [Прибор 1] RX НЕПОЛНЫЙ №24 получено 4 байт (длина неизвестна) · на запрос №24 · прервано через 3,2 мс: прибор закрыл TCP-соединение"
            + NL + Indent + "00 18 00 00", four.Text);

        var one = TraceFormatter.Format(Rx("21:47:41.200", 24, "00", 3.2, complete: false, note: "x"), 1);
        Assert.DoesNotContain("№24 получено", one.Text);
        Assert.Contains("RX НЕПОЛНЫЙ получено 1 байт (длина неизвестна)", one.Text);

        var none = TraceFormatter.Format(Rx("21:47:44.700", 25, "", 1500.0, complete: false, note: "нет полного ответа за 1500 мс"), 1);
        Assert.Equal("21:47:44.700 [Прибор 1] RX НЕТ ОТВЕТА · на запрос №25 · прервано через 1500,0 мс: нет полного ответа за 1500 мс", none.Text);
        Assert.Equal(TraceTone.Error, none.Tone);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("00", "00")]
    [InlineData("00 15 00 00 00 06", "00 15 00 00 00 06")]
    [InlineData("00 15 00 00 00 06 01", "00 15 00 00 00 06 → 01")]
    [InlineData("00 15 00 00 00 06 01 03 07 D0 00 0D", "00 15 00 00 00 06 → 01 03 07 D0 00 0D")]
    public void Hex_ArrowOnlyAfterSixthByte(string input, string expected) => Assert.Equal(expected, TraceFormatter.FormatHex(Hex(input)));

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void Ms_DecimalComma_NoThousandsSeparator(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("1000,3", TraceFormatter.FormatMs(1000.25001));
            Assert.Equal("12345,7", TraceFormatter.FormatMs(12345.68));
            Assert.Equal("0,1", TraceFormatter.FormatMs(0.1));
            var tx = TraceFormatter.Format(Tx("21:47:35.677", 21, 2000, 13, TracePhase.Poll, period: 1000.3), 1);
            Assert.Contains("период 1000,3 мс", tx.Text);
            Assert.StartsWith("21:47:35.677 [Прибор 1] ", tx.Text);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Lines_DoNotContainOldTerms()
    {
        var lines = new[]
        {
            TraceFormatter.Format(Tx("21:47:31.140", 1, 1000, 9, TracePhase.Identification), 1).Text,
            TraceFormatter.Format(Tx("21:47:35.677", 21, 2000, 13, TracePhase.Poll, period: 1000.3), 1).Text,
            TraceFormatter.Format(Rx("21:47:35.703", 21, "00 15 00 00 00 1D 01 03 1A 67 20 1A 2B 00 00 00 02 00 01 3F 00 00 00 44 9A 50 00 44 9A 50 00 00 00 00 00", 26.1), 1).Text,
            TraceFormatter.Format(Rx("21:47:44.700", 25, "", 1500.0, complete: false, note: "n"), 1).Text,
            TraceFormatter.Legend,
        };
        foreach (string line in lines)
        {
            foreach (string bad in new[] { "UID", "TID", "слов=", "байт=12", "запрос=" })
                Assert.DoesNotContain(bad, line);
        }
        // Endpoint is not repeated in device trace lines.
        Assert.DoesNotContain("10.0.1.18", lines[1]);
    }

    [Fact]
    public void Data_MatchesSample()
    {
        var r = new M10PollResult
        {
            Timestamp = T("21:47:35.703"), IsConnected = true, TransactionId = 21, ScaleNumber = 1, StatusMask = 2,
            Brutto = 1234.5f, Netto = 1234.5f, Tare = 0, Unit = "кг", Division = 0.5f, Range = 1, MeasurementUnixTime = 1730228779
        };
        var (text, isError) = TraceFormatter.FormatData(r, 1);
        Assert.Equal("21:47:35.703 [Прибор 1] Данные №21 · Весы 1 | Errors=0x0000: Ошибок нет | Status=0x0002 | Stable=1 Zero=0 BelowMin=0 | Брутто=1234.5 Нетто=1234.5 Тара=0 кг | d=0.5 | Диапазон=1 DT_LastWeight=1730228779", text);
        Assert.False(isError);

        var failed = new M10PollResult { Timestamp = T("21:47:44.700"), ConnectionError = "Нет полного ответа за 1500 мс (адрес 2000, слов 13, получено 0 байт)." };
        var (errorText, err) = TraceFormatter.FormatData(failed, 1);
        Assert.Equal("21:47:44.700 [Прибор 1] Ошибка обмена: Нет полного ответа за 1500 мс (адрес 2000, слов 13, получено 0 байт).", errorText);
        Assert.True(err);
    }

    [Fact]
    public void System_Event_Legend_Lines()
    {
        Assert.Equal(@"21:47:31.102 [Система] Сессия журнала открыта: C:\logs\x", TraceFormatter.SessionOpened(T("21:47:31.102"), @"C:\logs\x"));
        Assert.Equal("21:50:02.412 [Система] Сессия журнала закрыта.", TraceFormatter.SessionClosed(T("21:50:02.412")));
        Assert.Equal("21:50:02.410 [Прибор 1] 10.0.1.18:502 | Опрос выключен оператором. Ответов 601, сбоев 0.",
            TraceFormatter.FormatEvent(T("21:50:02.410"), 1, "10.0.1.18:502", "Опрос выключен оператором. Ответов 601, сбоев 0."));
        Assert.Equal("21:47:31.102 [Система] Обозначения: № — номер транзакции Modbus TCP (одинаков в запросе и ответе); Id — адрес устройства (Unit Identifier, 7-й байт, как адрес в RTU); FC — код функции (hex); адрес и регистров — dec (hex), адреса как в документе, без смещения −1; HEX: 6 байт TCP-префикса (№, протокол 0, длина) → Id + PDU (= кадр RTU без CRC); «период» — от предыдущего TX показаний этого прибора; «ответ через» — от отправки TX до последнего байта ответа; инициализация (1000, 1100…) идёт подряд без периода.",
            TraceFormatter.LegendLine(T("21:47:31.102")));
    }
}
