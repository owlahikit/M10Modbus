using System.Globalization;

namespace M10Modbus.Tests;

/// <summary>Line formats of design.md 4.1–4.6 (exact samples of 4.3).</summary>
public class TraceFormatterTests
{
    private static string Indent(int id) => new(' ', 27 + id.ToString(CultureInfo.InvariantCulture).Length);
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
    public void JournalNumber_ChangesLabelAndHexIndent_NotActualModbusBytes()
    {
        var trace = TraceFormatter.Format(Tx("21:47:31.140", 1, 1000, 9, TracePhase.Identification), 2);
        string numbered = TraceFormatter.WithJournalNumber(trace.Text, 12345);
        Assert.StartsWith("12345: 21:47:31.140 [Прибор 2] TX ", numbered);
        Assert.Contains(NL + Indent(12345) + "00 01 00 00 00 06 → 01 03 03 E8 00 09", numbered);
        Assert.Equal(trace.Text, TraceFormatter.WithJournalNumber(numbered, 1));
        Assert.Throws<ArgumentException>(() => TraceFormatter.WithJournalNumber("нет номера", 1));
        var result = new M10PollResult { IsConnected = true, TransactionId = 0, Division = 0.02f };
        Assert.StartsWith("70000: ", TraceFormatter.WithJournalNumber(TraceFormatter.FormatData(result, 1).Text, 70000));
    }

    [Fact]
    public void Init_TxRx_MatchSamples()
    {
        var tx1 = TraceFormatter.Format(Tx("21:47:31.140", 1, 1000, 9, TracePhase.Identification), 1);
        Assert.Equal("1: 21:47:31.140 [Прибор 1] TX Id=1 FC=03 адрес=1000 (0x03E8) регистров=9 (0x0009) · ИНИЦИАЛИЗАЦИЯ 1/2: идентификация (тип, SN, FW, порядок слов)"
            + NL + Indent(1) + "00 01 00 00 00 06 → 01 03 03 E8 00 09", tx1.Text);
        Assert.Equal(TraceTone.Tx, tx1.Tone);

        var rx1 = TraceFormatter.Format(Rx("21:47:31.152", 1,
            "00 01 00 00 00 15 01 03 12 00 64 30 39 00 01 00 05 00 00 00 00 00 00 00 00 00 00", 11.8, start: 1000, count: 9), 1);
        Assert.Equal("1: 21:47:31.152 [Прибор 1] RX Id=1 FC=03 байт данных=18 (0x12) · ответ через 11,8 мс"
            + NL + Indent(1) + "00 01 00 00 00 15 → 01 03 12 00 64 30 39 00 01 00 05 00 00 00 00 00 00 00 00 00 00", rx1.Text);
        Assert.Equal(TraceTone.Rx, rx1.Tone);

        var tx2 = TraceFormatter.Format(Tx("21:47:31.153", 2, 1100, 4, TracePhase.ScaleConfig), 1);
        Assert.Equal("2: 21:47:31.153 [Прибор 1] TX Id=1 FC=03 адрес=1100 (0x044C) регистров=4 (0x0004) · ИНИЦИАЛИЗАЦИЯ 2/2: конфигурация весов 1 (SN весов, единица)"
            + NL + Indent(2) + "00 02 00 00 00 06 → 01 03 04 4C 00 04", tx2.Text);

        var rx2 = TraceFormatter.Format(Rx("21:47:31.164", 2, "00 02 00 00 00 0B 01 03 08 00 7B 00 00 00 00 00 00", 10.9, start: 1100, count: 4), 1);
        Assert.Equal("2: 21:47:31.164 [Прибор 1] RX Id=1 FC=03 байт данных=8 (0x08) · ответ через 10,9 мс"
            + NL + Indent(2) + "00 02 00 00 00 0B → 01 03 08 00 7B 00 00 00 00 00 00", rx2.Text);
    }

    [Fact]
    public void ScaleConfig_Scale2_Uses1133()
    {
        var tx = TraceFormatter.Format(Tx("21:47:31.153", 2, 1133, 4, TracePhase.ScaleConfig, scale: 2), 2);
        Assert.Equal("2: 21:47:31.153 [Прибор 2] TX Id=1 FC=03 адрес=1133 (0x046D) регистров=4 (0x0004) · ИНИЦИАЛИЗАЦИЯ 2/2: конфигурация весов 2 (SN весов, единица)"
            + NL + Indent(2) + "00 02 00 00 00 06 → 01 03 04 6D 00 04", tx.Text);
    }

    [Fact]
    public void PollTx_FirstAfterInit_Period_Overrun()
    {
        var first = TraceFormatter.Format(Tx("21:47:31.165", 3, 2000, 13, TracePhase.Poll), 1);
        Assert.Equal("3: 21:47:31.165 [Прибор 1] TX Id=1 FC=03 адрес=2000 (0x07D0) регистров=13 (0x000D) · показания · первый после инициализации"
            + NL + Indent(3) + "00 03 00 00 00 06 → 01 03 07 D0 00 0D", first.Text);

        var periodic = TraceFormatter.Format(Tx("21:47:35.677", 21, 2000, 13, TracePhase.Poll, period: 1000.3, planned: 1000), 1);
        Assert.Equal("21: 21:47:35.677 [Прибор 1] TX Id=1 FC=03 адрес=2000 (0x07D0) регистров=13 (0x000D) · показания · период 1000,3 мс"
            + NL + Indent(21) + "00 15 00 00 00 06 → 01 03 07 D0 00 0D", periodic.Text);
        Assert.Equal(TraceTone.Tx, periodic.Tone);

        var overrun = TraceFormatter.Format(Tx("21:47:45.043", 30, 2000, 13, TracePhase.Poll, period: 1043.7, planned: 1000, overrun: true), 1);
        Assert.Equal("30: 21:47:45.043 [Прибор 1] TX Id=1 FC=03 адрес=2000 (0x07D0) регистров=13 (0x000D) · показания · период 1043,7 мс · ПРЕВЫШЕН (задан 1000 мс)"
            + NL + Indent(30) + "00 1E 00 00 00 06 → 01 03 07 D0 00 0D", overrun.Text);
        Assert.Equal(TraceTone.Warning, overrun.Tone);
    }

    [Fact]
    public void PollRx_35Bytes_MatchesSample()
    {
        const string hex = "00 15 00 00 00 1D 01 03 1A 67 20 1A 2B 00 00 00 02 00 01 3F 00 00 00 44 9A 50 00 44 9A 50 00 00 00 00 00";
        Assert.Equal(35, Hex(hex).Length);
        var rx = TraceFormatter.Format(Rx("21:47:35.703", 21, hex, 26.1), 1);
        Assert.Equal("21: 21:47:35.703 [Прибор 1] RX Id=1 FC=03 байт данных=26 (0x1A) · ответ через 26,1 мс"
            + NL + Indent(21) + "00 15 00 00 00 1D → 01 03 1A 67 20 1A 2B 00 00 00 02 00 01 3F 00 00 00 44 9A 50 00 44 9A 50 00 00 00 00 00", rx.Text);
    }

    [Fact]
    public void Rx_TidMismatch_ShowsBothNumbers()
    {
        var rx = TraceFormatter.Format(Rx("21:47:35.703", 21, "00 16 00 00 00 1D 01 03 1A", 5.0), 1);
        Assert.StartsWith("21: 21:47:35.703 [Прибор 1] RX Id=1 FC=03 байт данных=26 (0x1A) · номер ответа=22 · ответ через 5,0 мс", rx.Text);
    }

    [Fact]
    public void Rx_Exception83_Code02()
    {
        var rx = TraceFormatter.Format(Rx("21:47:36.690", 22, "00 16 00 00 00 03 01 83 02", 12.4), 1);
        Assert.Equal("22: 21:47:36.690 [Прибор 1] RX Id=1 FC=83 ИСКЛЮЧЕНИЕ к FC=03: код 02 — недопустимый адрес регистра · ответ через 12,4 мс"
            + NL + Indent(22) + "00 16 00 00 00 03 → 01 83 02", rx.Text);
        Assert.Equal(TraceTone.Error, rx.Tone);
    }

    [Fact]
    public void Rx_Incomplete_9of35_4bytes_NoAnswer()
    {
        var part = TraceFormatter.Format(Rx("21:47:39.180", 23, "00 17 00 00 00 1D 01 03 1A", 1500.0, complete: false, expected: 35,
            note: "нет полного ответа за 1500 мс"), 1);
        Assert.Equal("23: 21:47:39.180 [Прибор 1] RX НЕПОЛНЫЙ получено 9 из 35 байт · прервано через 1500,0 мс: нет полного ответа за 1500 мс"
            + NL + Indent(23) + "00 17 00 00 00 1D → 01 03 1A", part.Text);
        Assert.Equal(TraceTone.Error, part.Tone);

        var four = TraceFormatter.Format(Rx("21:47:41.200", 24, "00 18 00 00", 3.2, complete: false, note: "прибор закрыл TCP-соединение"), 1);
        Assert.Equal("24: 21:47:41.200 [Прибор 1] RX НЕПОЛНЫЙ получено 4 байт (длина неизвестна) · прервано через 3,2 мс: прибор закрыл TCP-соединение"
            + NL + Indent(24) + "00 18 00 00", four.Text);

        var one = TraceFormatter.Format(Rx("21:47:41.200", 24, "00", 3.2, complete: false, note: "x"), 1);
        Assert.DoesNotContain("№24 получено", one.Text);
        Assert.Contains("RX НЕПОЛНЫЙ получено 1 байт (длина неизвестна)", one.Text);

        var none = TraceFormatter.Format(Rx("21:47:44.700", 25, "", 1500.0, complete: false, note: "нет полного ответа за 1500 мс"), 1);
        Assert.Equal("25: 21:47:44.700 [Прибор 1] RX НЕТ ОТВЕТА · прервано через 1500,0 мс: нет полного ответа за 1500 мс", none.Text);
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
            Assert.StartsWith("21: 21:47:35.677 [Прибор 1] ", tx.Text);
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
        Assert.Equal("21: 21:47:35.703 [Прибор 1] Весы 1: Брутто=1234.500 Нетто=1234.500 Тара=0.000 кг d=0.500 | Диапазон=1 | Stable=1 Zero=0 BelowMin=0 | Ошибок нет", text);
        Assert.False(isError);

        var failed = new M10PollResult { Timestamp = T("21:47:44.700"), ConnectionError = "Нет полного ответа за 1500 мс (адрес 2000, слов 13, получено 0 байт)." };
        var (errorText, err) = TraceFormatter.FormatData(failed, 1);
        Assert.Equal("21:47:44.700 [Прибор 1] Ошибка обмена: Нет полного ответа за 1500 мс (адрес 2000, слов 13, получено 0 байт).", errorText);
        Assert.True(err);
    }

    [Fact]
    public void Data_MatchesRequestedLayoutAndUsesCardNumber()
    {
        var r = new M10PollResult
        {
            Timestamp = T("18:36:07.701"), IsConnected = true, TransactionId = 1474, ScaleNumber = 1, StatusMask = 2,
            Brutto = 75.6f, Netto = 75.6f, Tare = 0, Unit = "т", Division = 0.05f,
            Range = 2, MeasurementUnixTime = 1793643944
        };
        var (text, isError) = TraceFormatter.FormatData(r, 2);
        Assert.Equal("1474: 18:36:07.701 [Прибор 2] Весы 1: Брутто=75.600 Нетто=75.600 Тара=0.000 т d=0.050 | Диапазон=2 | Stable=1 Zero=0 BelowMin=0 | Ошибок нет", text);
        Assert.False(isError);
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void Data_UsesThreeDecimalsWithoutRoundingByDivision(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var r = new M10PollResult
            {
                IsConnected = true, TransactionId = 0, ExchangeId = Guid.NewGuid(),
                Brutto = 0.025f, Netto = -0.0004f, Tare = 1.2345f, Division = 0.02f, Unit = "т"
            };
            string text = TraceFormatter.FormatData(r, 1).Text;
            Assert.StartsWith("0: ", text);
            Assert.Contains("Брутто=0.025 Нетто=0.000 Тара=1.235 т d=0.020", text);
            Assert.Equal("0.001", M10PollResult.FormatNumber(0.0005f, CultureInfo.InvariantCulture));
            r.Division = float.NaN;
            Assert.Contains("d=NaN", TraceFormatter.FormatData(r, 1).Text);
            Assert.True(TraceFormatter.FormatData(r, 1).IsError);
            r.IsConnected = false;
            r.ConnectionError = "нет ответа";
            Assert.StartsWith("0: ", TraceFormatter.FormatData(r, 1).Text);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Readings_RetainFloatPrecision_AndNavigateToTheSameDecodedAnswer()
    {
        var r = new M10PollResult
        {
            Timestamp = T("18:36:07.701"), IsConnected = true, TransactionId = 1474,
            ExchangeId = Guid.NewGuid(), Brutto = 75.6f, Netto = -12.3456f,
            Tare = 0.1f, Division = 0.02f, Unit = "т", Range = 2, StatusMask = 2
        };
        var decoded = TraceFormatter.FormatData(r, 2);
        var raw = TraceFormatter.FormatData(r, 2, rawValues: true);
        Assert.Contains("Брутто=75.5999985 Нетто=-12.3456001 Тара=0.100000001 т d=0.0199999996", raw.Text);
        Assert.Contains("Брутто=75.600 Нетто=-12.346 Тара=0.100 т d=0.020", decoded.Text);
        Assert.StartsWith("1474: 18:36:07.701 [Прибор 2] ", raw.Text);
        Assert.Equal(decoded.IsError, raw.IsError);
        Assert.False(raw.IsError);

        var anchor = new JournalAnchor(2, r.ExchangeId);
        var index = new JournalTextIndex();
        index.Append(new JournalAnchor(1, Guid.NewGuid()), 10);
        index.Append(anchor, raw.Text.Length + 1);
        Assert.Equal(anchor, index.At(10)!.Anchor);
        index.Clear();
        index.Append(anchor, decoded.Text.Length + 1);
        Assert.Equal(0, index.Find(anchor)!.Start);
        Assert.Equal(anchor, index.Find(anchor)!.Anchor);

        r.ErrorCode = 2;
        Assert.True(TraceFormatter.FormatData(r, 2, rawValues: true).IsError);
        r.IsConnected = false;
        Assert.Equal(TraceFormatter.FormatData(r, 2), TraceFormatter.FormatData(r, 2, rawValues: true));
    }

    [Fact]
    public void Data_ErrorKeepsCodeAndDescription()
    {
        var r = new M10PollResult
        {
            Timestamp = T("18:36:07.701"), IsConnected = true, TransactionId = 1475, ScaleNumber = 1,
            ErrorCode = 2, StatusMask = 4, Division = 0.05f, Unit = "т"
        };
        var (text, isError) = TraceFormatter.FormatData(r, 2);
        Assert.StartsWith("1475: 18:36:07.701 [Прибор 2] Весы 1: Брутто=", text);
        Assert.EndsWith("0x0002: нагрузка ниже заданного порога", text);
        Assert.Contains("Stable=0 Zero=0 BelowMin=1", text);
        Assert.True(isError);
    }

    [Fact]
    public void System_Event_Legend_Lines()
    {
        Assert.Equal(@"21:47:31.102 [Система] Сессия журнала открыта: C:\logs\x", TraceFormatter.SessionOpened(T("21:47:31.102"), @"C:\logs\x"));
        Assert.Equal("21:50:02.412 [Система] Сессия журнала закрыта.", TraceFormatter.SessionClosed(T("21:50:02.412")));
        Assert.Equal("21:50:02.410 [Прибор 1] 10.0.1.18:502 | Опрос выключен оператором. Ответов 601, сбоев 0.",
            TraceFormatter.FormatEvent(T("21:50:02.410"), 1, "10.0.1.18:502", "Опрос выключен оператором. Ответов 601, сбоев 0."));
        Assert.Equal("21:47:31.102 [Система] " + TraceFormatter.Legend,
            TraceFormatter.LegendLine(T("21:47:31.102")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identification_DecodesRealSample_InBothWordOrders(bool lowWordFirst)
    {
        ushort[] words = { 100, 53596, 1, 85, 0x6AE7, 0x94C2, 0x6AE4, 0xC1A0, lowWordFirst ? (ushort)1 : (ushort)0 };
        if (lowWordFirst)
            for (int i = 2; i < 8; i += 2) (words[i], words[i + 1]) = (words[i + 1], words[i]);
        var result = TraceFormatter.FormatInitializationData(Initialization(TracePhase.Identification, words, lowWordFirst), 1)!.Value;
        Assert.Equal("1: 21:47:31.152 [Прибор 1] Идентификация | Тип=100 (M10) | SN=53596 | FW=1.085 | DT_Current=1793561794 | DT_Boot=1793376672" +
            $" | Endians={(lowWordFirst ? 1 : 0)} ({(lowWordFirst ? "младшее" : "старшее")} слово первым)", result.Text);
        Assert.False(result.IsError);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void Configuration_DecodesUsedRangesOnly(bool lowWordFirst, int scale)
    {
        var result = TraceFormatter.FormatInitializationData(
            Initialization(TracePhase.ScaleConfig, ConfigWords(lowWordFirst), lowWordFirst, scale), 2)!.Value;
        Assert.Equal($"2: 21:47:31.152 [Прибор 2] Конфигурация · Весы {scale} | SN весов=45000 | Единица=1 (т)" +
            " | Тип диапазонов=2 (многодиапазонные) | Диапазонов=2" +
            " | Диапазон 1: Min=0.400 Max=60.000 d=0.020" +
            " | Диапазон 2: Min=1.000 Max=80.000 d=0.050", result.Text);
        Assert.DoesNotContain("Диапазон 3:", result.Text);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Initialization_InvalidSemanticValues_AreExplicitErrors()
    {
        ushort[] info = { 100, 1, 1, 85, 0, 0, 0, 0, 2 };
        var badEndian = TraceFormatter.FormatInitializationData(Initialization(TracePhase.Identification, info), 1)!.Value;
        Assert.True(badEndian.IsError);
        Assert.Contains("FW и DT не расшифрованы", badEndian.Text);
        var missing = TraceFormatter.FormatInitializationData(
            Initialization(TracePhase.ScaleConfig, new ushort[22], scale: 2), 2)!.Value;
        Assert.True(missing.IsError);
        Assert.Contains("Весы 2", missing.Text);
        Assert.Contains("Диапазонов=0", missing.Text);
        Assert.Contains("НЕКОРРЕКТНО", missing.Text);
        var words = ConfigWords(false);
        words[3] = ushort.MaxValue;
        var count = TraceFormatter.FormatInitializationData(Initialization(TracePhase.ScaleConfig, words), 1)!.Value;
        Assert.True(count.IsError);
        Assert.DoesNotContain("Диапазон 1:", count.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(65535)]
    public void Identification_DoesNotRejectOtherFirmwareVersions(ushort major)
    {
        ushort[] words = { 100, 1, major, 85, 0, 0, 0, 0, 0 };
        var decoded = TraceFormatter.FormatInitializationData(Initialization(TracePhase.Identification, words), 1)!.Value;
        Assert.False(decoded.IsError);
        Assert.Contains($"FW={major}.085", decoded.Text);
        words[0] = 99;
        decoded = TraceFormatter.FormatInitializationData(Initialization(TracePhase.Identification, words), 1)!.Value;
        Assert.True(decoded.IsError);
        Assert.Contains("ожидался M10 (100)", decoded.Text);
    }

    [Fact]
    public void Initialization_DoesNotDecodeRejectedOrIncompleteFrames()
    {
        var words = ConfigWords(false);
        foreach (var trace in new[]
        {
            Initialization(TracePhase.ScaleConfig, words, complete: false),
            Initialization(TracePhase.ScaleConfig, words, note: "№ ответа не совпадает"),
            Initialization(TracePhase.ScaleConfig, words, direction: "TX"),
            Initialization(TracePhase.Poll, new ushort[13])
        })
            Assert.Null(TraceFormatter.FormatInitializationData(trace, 1));
        foreach (int byteIndex in new[] { 0, 2, 5, 6, 7, 8 })
        {
            var trace = Initialization(TracePhase.ScaleConfig, words);
            trace.Bytes[byteIndex] ^= 1;
            Assert.Null(TraceFormatter.FormatInitializationData(trace, 1));
        }
    }

    private static ushort[] ConfigWords(bool lowWordFirst)
    {
        var words = new ushort[22];
        words[0] = 45000; words[1] = 1; words[2] = 2; words[3] = 2;
        float[] values = { BitConverter.Int32BitsToSingle(0x3ECCCCCC), 60, 0.02f, 1, 80, 0.05f, 999, 999, 999 };
        for (int i = 0; i < values.Length; i++)
        {
            uint bits = BitConverter.SingleToUInt32Bits(values[i]);
            words[4 + 2 * i] = lowWordFirst ? (ushort)bits : (ushort)(bits >> 16);
            words[5 + 2 * i] = lowWordFirst ? (ushort)(bits >> 16) : (ushort)bits;
        }
        return words;
    }

    private static ModbusTrace Initialization(TracePhase phase, ushort[] words, bool lowWordFirst = false, int scale = 1,
        bool complete = true, string note = "", string direction = "RX")
    {
        ushort tid = phase == TracePhase.Identification ? (ushort)1 : (ushort)2;
        var bytes = new byte[9 + 2 * words.Length];
        bytes[1] = (byte)tid; bytes[5] = (byte)(3 + 2 * words.Length);
        bytes[6] = 1; bytes[7] = 3; bytes[8] = (byte)(2 * words.Length);
        for (int i = 0; i < words.Length; i++)
        {
            bytes[9 + 2 * i] = (byte)(words[i] >> 8);
            bytes[10 + 2 * i] = (byte)words[i];
        }
        return new ModbusTrace
        {
            Timestamp = T("21:47:31.152"), TransactionId = tid, UnitId = 1, Direction = direction,
            Phase = phase, RegisterCount = (ushort)words.Length, ScaleNumber = scale, LowWordFirst = lowWordFirst,
            StartAddress = phase == TracePhase.Identification ? (ushort)1000 : (ushort)(1100 + 33 * (scale - 1)),
            Bytes = bytes, IsComplete = complete, Note = note
        };
    }
}
