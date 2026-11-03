using System.Globalization;

namespace M10Modbus.Tests;

/// <summary>CardView texts/tones of DeviceCardModel (design.md 2.3–2.4).</summary>
public class DeviceCardModelTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 21, 47, 35, 703);

    private static DeviceSettings Settings() =>
        new() { Ip = "10.0.1.18", Port = 502, UnitId = 1, Scale = 1 };

    private static M10PollResult Ok(DateTime t, ushort error = 0, ushort status = 2) => new()
    {
        Timestamp = t, IsConnected = true, ErrorCode = error, StatusMask = status, Division = 0.5f,
        Brutto = 1234.5f, Netto = 1234.5f, Tare = 0, Unit = "кг", DeviceInfo = "M10 SN 1", ResponseMs = 26.4
    };

    private static M10PollResult Fail(DateTime t) => new() { Timestamp = t, ConnectionError = "нет ответа" };

    private static DeviceCardModel Started(bool latch = true)
    {
        var m = new DeviceCardModel(1);
        m.Start(Settings(), latch, T0.AddSeconds(-1), 250, 1500);
        return m;
    }

    private static string Weight(M10PollResult r) => r.FormatWeight(r.Brutto) + " кг";

    [Fact]
    public void Off_WithoutSnapshot()
    {
        var v = new DeviceCardModel(1).View;
        Assert.Equal("Опрос выключен", v.StatusText);
        Assert.Equal(Tone.Stale, v.StatusTone);
        Assert.Equal("—", v.WeightText);
        Assert.Equal(Tone.Stale, v.WeightTone);
        Assert.Equal("Нет данных · включите «Опрос»", v.StateText);
        Assert.Equal(Tone.Neutral, v.StateTone);
        Assert.False(v.ShowLatchReset);
    }

    [Fact]
    public void Start_Connecting_Then_Live()
    {
        var m = new DeviceCardModel(1);
        var ev = m.Start(Settings(), true, T0, 250, 1500);
        Assert.Equal("Опрос включён: 10.0.1.18:502, Id 1, весы 1; период 250 мс, таймаут 1500 мс, фиксация ошибок: да.", ev.Single().Text);
        Assert.Equal(CardPhase.Connecting, m.Phase);
        Assert.Equal("Подключение к 10.0.1.18:502 · Id 1 · инициализация…", m.View.StatusText);
        Assert.Equal(Tone.Info, m.View.StatusTone);
        Assert.Equal(("—", Tone.Stale), (m.View.WeightText, m.View.WeightTone));
        Assert.Equal(("Ожидание первого ответа", Tone.Neutral), (m.View.StateText, m.View.StateTone));

        var r = Ok(T0);
        var events = m.Apply(r);
        Assert.Contains(events, e => e.Text == "Обмен установлен; M10 SN 1." && e.Time == T0);
        Assert.Equal("Подключено · ответ 21:47:35.703 за 26 мс · ответов 1 · сбоев 0", m.View.StatusText);
        Assert.Equal(Tone.Good, m.View.StatusTone);
        Assert.Equal(Weight(r), m.View.WeightText);
        Assert.Equal(Tone.Good, m.View.WeightTone);
        Assert.False(m.View.WeightIsMessage);
        Assert.Equal("Стабильно · Нетто " + r.FormatWeight(1234.5f) + " · Тара " + r.FormatWeight(0) + " кг · Ноль: нет", m.View.StateText);
        Assert.False(m.View.ShowLatchReset);

        m.Apply(Ok(T0.AddSeconds(1), status: 0));
        Assert.Equal(Tone.Warn, m.View.WeightTone);
        Assert.StartsWith("Не стабильно · не подлежит учёту · Нетто", m.View.StateText);
        m.Apply(Ok(T0.AddSeconds(2), status: 2 | 4 | 1));
        Assert.Equal(Tone.Warn, m.View.WeightTone);
        Assert.StartsWith("Ниже Min1 · не подлежит учёту", m.View.StateText);
        Assert.EndsWith("Ноль: да", m.View.StateText);
    }

    [Fact]
    public void Snapshot_AfterStop_IsStale()
    {
        var m = Started();
        var r = Ok(T0);
        m.Apply(r);
        m.Apply(Fail(T0.AddSeconds(1)));
        m.Apply(Ok(T0.AddSeconds(2)));
        var ev = m.Stop(T0.AddSeconds(3), "оператором");
        Assert.Equal("Опрос выключен оператором. Ответов 2, сбоев 1.", ev.Single().Text);
        var v = m.View;
        Assert.Equal(CardPhase.Off, m.Phase);
        Assert.Equal("Опрос остановлен · последние данные на 21:47:37.703 · ответов 2 · сбоев 1", v.StatusText);
        Assert.Equal(Tone.Stale, v.StatusTone);
        Assert.Equal(Weight(r), v.WeightText);
        Assert.Equal(Tone.Stale, v.WeightTone);
        Assert.StartsWith("Стабильно · Нетто", v.StateText);
        Assert.Equal(Tone.Stale, v.StateTone);
        Assert.False(v.ShowLatchReset);

        // Restart: snapshot stays grey until the first new response; counters reset.
        m.Start(Settings(), true, T0.AddSeconds(10));
        Assert.Equal(Weight(r), m.View.WeightText);
        Assert.Equal(Tone.Stale, m.View.WeightTone);
        Assert.Equal(Tone.Stale, m.View.StateTone);
        m.Stop(T0.AddSeconds(11), "оператором");
        Assert.Equal("Опрос остановлен · последние данные на 21:47:37.703 · ответов 0 · сбоев 0", m.View.StatusText);
    }

    [Fact]
    public void CommError_WithAndWithoutSnapshot()
    {
        var m = Started();
        m.Apply(Fail(T0));
        Assert.Equal("Нет связи · переподключение · сбоев 1", m.View.StatusText);
        Assert.Equal(Tone.Bad, m.View.StatusTone);
        Assert.Equal(("—", Tone.Stale), (m.View.WeightText, m.View.WeightTone));
        Assert.Equal(("Ошибка обмена: нет ответа", Tone.Bad), (m.View.StateText, m.View.StateTone));

        var r = Ok(T0.AddSeconds(1));
        var restored = m.Apply(r);
        Assert.Contains(restored, e => e.Text == "Обмен восстановлен; конфигурация прочитана заново.");
        var events = new List<CardEvent>();
        for (int i = 1; i <= 20; i++) events.AddRange(m.Apply(Fail(T0.AddSeconds(1 + i))));
        Assert.Equal(new[] { "Ошибка обмена #1: нет ответа", "Ошибка обмена #10: нет ответа", "Ошибка обмена #20: нет ответа" },
            events.Select(e => e.Text));
        var v = m.View;
        Assert.Equal("Нет связи · переподключение · последние данные на 21:47:36.703 · сбоев 21", v.StatusText);
        Assert.Equal(Weight(r), v.WeightText);
        Assert.Equal(Tone.Stale, v.WeightTone);
        Assert.Equal(Tone.Bad, v.StateTone);
    }

    [Fact]
    public void D2_ThreeErrors_Latch_Reset_TwelveClean()
    {
        var m = Started(latch: true);
        m.Apply(Ok(T0));
        Assert.False(m.View.ShowLatchReset);

        m.Apply(Ok(T0.AddSeconds(1), error: 2));
        Assert.Equal(("Авария", true, Tone.Bad), (m.View.WeightText, m.View.WeightIsMessage, m.View.WeightTone));
        Assert.Equal("0x0002: нагрузка ниже заданного порога · вес недействителен по флагу Errors", m.View.StateText);
        Assert.True(m.View.ShowLatchReset);
        m.Apply(Ok(T0.AddSeconds(2), error: 4));
        m.Apply(Ok(T0.AddSeconds(3), error: 4));
        Assert.Equal("0x0004: нагрузка выше заданного порога · ошибка зафиксирована — снимите фиксацию после проверки", m.View.StateText);

        for (int i = 0; i < 20; i++) m.Apply(Ok(T0.AddSeconds(4 + i)));
        Assert.Equal(("Фиксация Аварии 0x0006", Tone.Warn), (m.View.WeightText, m.View.WeightTone));
        Assert.Equal(("Сейчас ошибок нет · предыдущая ошибка зафиксирована", Tone.Warn), (m.View.StateText, m.View.StateTone));
        Assert.True(m.View.ShowLatchReset);

        var ev = m.ResetLatch(T0.AddSeconds(30));
        Assert.Equal("Локальная фиксация снята. Проверка 12 ответов; прибору ничего не отправлялось.", ev.Single().Text);
        Assert.Equal(("Повторная проверка", Tone.Warn), (m.View.WeightText, m.View.WeightTone));
        Assert.Equal("Ожидание 12 корректных ответов", m.View.StateText);
        Assert.False(m.View.ShowLatchReset);

        for (int i = 1; i <= 11; i++)
        {
            m.Apply(Ok(T0.AddSeconds(30 + i)));
            Assert.Equal($"Проверка {i}/12", m.View.WeightText);
            Assert.Equal("Показания скрыты после ошибки · нужно 12 ответов без ошибок", m.View.StateText);
        }
        var last = m.Apply(Ok(T0.AddSeconds(42)));
        Assert.Contains(last, e => e.Text == "Проверка завершена: 12 последовательных корректных ответов.");
        Assert.False(m.View.WeightIsMessage);
        Assert.Equal(Tone.Good, m.View.WeightTone);
    }

    [Fact]
    public void D2_OneError_TwelveClean_CommErrorResetsCount()
    {
        var m = Started(latch: true);
        m.Apply(Ok(T0, error: 1));
        for (int i = 1; i <= 5; i++) m.Apply(Ok(T0.AddSeconds(i)));
        Assert.Equal("Проверка 5/12", m.View.WeightText);
        m.Apply(Fail(T0.AddSeconds(6)));
        Assert.True(m.View.ShowLatchReset); // faults > 0, not Off
        for (int i = 1; i <= 11; i++) m.Apply(Ok(T0.AddSeconds(6 + i)));
        Assert.Equal("Проверка 11/12", m.View.WeightText);
        m.Apply(Ok(T0.AddSeconds(18)));
        Assert.False(m.View.WeightIsMessage);
        Assert.False(m.View.ShowLatchReset);
    }

    [Fact]
    public void D2_LatchDisabled_NoLatchNoRecheck()
    {
        var m = Started(latch: false);
        for (int i = 0; i < 5; i++) m.Apply(Ok(T0.AddSeconds(i), error: 2));
        Assert.EndsWith("· вес недействителен по флагу Errors", m.View.StateText);
        Assert.False(m.View.ShowLatchReset);
        var r = Ok(T0.AddSeconds(6));
        m.Apply(r);
        Assert.Equal(Weight(r), m.View.WeightText);
        Assert.Equal(Tone.Good, m.View.WeightTone);
    }

    [Fact]
    public void DisableLatchDuringLiveHeldAlarm_ReleasesImmediatelyUsingLastValidReading()
    {
        var m = Started();
        for (int i = 0; i < 3; i++) m.Apply(Ok(T0.AddSeconds(i), error: 2));
        var last = Ok(T0.AddSeconds(3));
        m.Apply(last);
        Assert.StartsWith("Фиксация Аварии", m.View.WeightText);
        long count = m.Ok;

        var events = m.SetLatchEnabled(false, T0.AddSeconds(4));
        Assert.Contains(events, e => e.Text == "Фиксация ошибок выключена.");
        Assert.Equal(Weight(last), m.View.WeightText);
        Assert.False(m.View.ShowLatchReset);
        Assert.Equal(count, m.Ok);
    }

    [Fact]
    public void DisableLatchDuringCurrentAlarm_DoesNotHideAlarmOrUseInvalidWeight()
    {
        var m = Started();
        for (int i = 0; i < 3; i++) m.Apply(Ok(T0.AddSeconds(i), error: 2));
        m.SetLatchEnabled(false, T0.AddSeconds(3));
        Assert.Equal("Авария", m.View.WeightText);
        Assert.Contains("вес недействителен по флагу Errors", m.View.StateText);
        Assert.False(m.View.ShowLatchReset);
        var recovered = Ok(T0.AddSeconds(4));
        m.Apply(recovered);
        Assert.Equal(Weight(recovered), m.View.WeightText);
    }

    [Fact]
    public void EnableLatchWhilePolling_TracksSubsequentErrors()
    {
        var m = Started(latch: false);
        m.Apply(Ok(T0, error: 2));
        m.SetLatchEnabled(true, T0.AddSeconds(1));
        Assert.Equal("Авария", m.View.WeightText);
        Assert.False(m.View.ShowLatchReset);
        for (int i = 0; i < 3; i++) m.Apply(Ok(T0.AddSeconds(2 + i), error: 4));
        m.Apply(Ok(T0.AddSeconds(5)));
        Assert.Equal("Фиксация Аварии 0x0004", m.View.WeightText);
        Assert.True(m.View.ShowLatchReset);
    }

    [Fact]
    public void DisableLatchDuringDisconnect_DoesNotShowStaleReadingAsLive()
    {
        var m = Started();
        for (int i = 0; i < 3; i++) m.Apply(Ok(T0.AddSeconds(i), error: 2));
        m.Apply(Ok(T0.AddSeconds(3)));
        m.Apply(Fail(T0.AddSeconds(4)));
        m.SetLatchEnabled(false, T0.AddSeconds(5));
        Assert.Equal(CardPhase.CommError, m.Phase);
        Assert.Equal(Tone.Stale, m.View.WeightTone);
        Assert.False(m.View.ShowLatchReset);
        var recovered = Ok(T0.AddSeconds(6));
        m.Apply(recovered);
        Assert.Equal(Weight(recovered), m.View.WeightText);
    }

    [Fact]
    public void DisableLatchDuringRecheck_ImmediatelyShowsLastValidReading()
    {
        var m = Started();
        m.Apply(Ok(T0, error: 2));
        var last = Ok(T0.AddSeconds(1));
        m.Apply(last);
        Assert.Equal("Проверка 1/12", m.View.WeightText);
        m.SetLatchEnabled(false, T0.AddSeconds(2));
        Assert.Equal(Weight(last), m.View.WeightText);
        m.SetLatchEnabled(true, T0.AddSeconds(3));
        m.Apply(Ok(T0.AddSeconds(4)));
        Assert.False(m.View.WeightIsMessage);
    }

    [Fact]
    public void Off_HidesLatchButton_SnapshotKeepsMessage()
    {
        var m = Started();
        m.Apply(Ok(T0, error: 2));
        Assert.True(m.View.ShowLatchReset);
        m.Stop(T0.AddSeconds(1), "оператором");
        Assert.False(m.View.ShowLatchReset);
        Assert.Equal(("Авария", true, Tone.Stale), (m.View.WeightText, m.View.WeightIsMessage, m.View.WeightTone));
        Assert.Equal(Tone.Stale, m.View.StateTone);
        Assert.Contains("последние данные на 21:47:35.703", m.View.StatusText);
    }

    [Fact]
    public void InvalidData_HidesWeightAndIndicators()
    {
        var m = Started();
        var bad = Ok(T0); bad.Division = 0;
        m.Apply(bad);
        Assert.Equal(("Некорректные данные", Tone.Bad), (m.View.WeightText, m.View.WeightTone));
        Assert.Equal("Числовые значения или дискретность d недопустимы — см. HEX/расшифровку", m.View.StateText);

        Assert.False(m.View.StableIndicator);
        Assert.False(m.View.ZeroIndicator);
    }

    [Theory]
    [InlineData("ru-RU", 0.02f, 10f, "10,000")]
    [InlineData("en-US", 0.02f, 10f, "10.000")]
    [InlineData("ru-RU", 0.05f, 10f, "10,000")]
    [InlineData("en-US", 0.5f, 0.48f, "0.480")]
    public void Weight_UsesThreePlaces_WithoutReroundingToDivision(
        string culture, float division, float brutto, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var m = Started(latch: false);
            var r = Ok(T0);
            r.Division = division; r.Brutto = brutto; r.Unit = "т";
            m.Apply(r);
            Assert.Equal(expected + " т", m.View.WeightText);
            Assert.Equal(expected, m.View.Instrument!.BruttoText);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(1.2345f, "1.235")]
    [InlineData(-1.2345f, "-1.235")]
    [InlineData(-0.0001f, "0.000")]
    [InlineData(10f, "10.000")]
    public void ThreePlaces_MidpointsAndNegativeZero(float raw, string expected)
    {
        var m = Started(latch: false);
        var r = Ok(T0);
        r.Brutto = raw;
        m.Apply(r);
        Assert.Equal(expected.Replace(".", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator) + " кг", m.View.WeightText);
    }

    [Fact]
    public void Indicators_FollowStatusBits_NotDisplayedNumbers()
    {
        var m = Started(latch: false);
        var r = Ok(T0, status: 2);
        r.Brutto = r.Netto = 0;
        m.Apply(r);
        Assert.True(m.View.StableIndicator);
        Assert.False(m.View.ZeroIndicator);
        r.StatusMask = 1; r.Brutto = r.Netto = 1;
        m.Apply(r);
        Assert.False(m.View.StableIndicator);
        Assert.True(m.View.ZeroIndicator);
    }

    [Fact]
    public void StateEvents_IncludeGrossWeightAtStatusChangesWithoutLoggingEveryPoll()
    {
        var m = Started(latch: false);
        var first = Ok(T0, status: 2);
        first.Brutto = 30.4f; first.Netto = 10; first.Tare = 20.4f; first.Unit = "т";
        var initial = m.Apply(first);
        Assert.Contains(initial, e => e.Text == $"Брутто: {first.FormatWeight(first.Brutto)} т; Стабильно; вес не ниже Min1; ноль: нет.");

        var unchanged = Ok(T0.AddSeconds(1), status: 2);
        unchanged.Brutto = 50;
        Assert.Empty(m.Apply(unchanged));

        var changed = Ok(T0.AddSeconds(2), status: 1);
        changed.Brutto = 60.8f; changed.Unit = "т";
        var transition = m.Apply(changed);
        Assert.Contains(transition, e => e.Text == $"Брутто: {changed.FormatWeight(changed.Brutto)} т; Не стабильно; вес не ниже Min1; ноль: да.");
        Assert.DoesNotContain(transition, e => e.Text.Contains("Нетто") || e.Text.Contains("Тара"));
    }

    [Fact]
    public void StateEvents_DoNotPresentInvalidOrAlarmWeightAsValid()
    {
        var m = Started(latch: false);
        var alarm = Ok(T0, error: 2);
        alarm.Brutto = 123.45f;
        var alarmEvents = m.Apply(alarm);
        Assert.Contains(alarmEvents, e => e.Text.Contains("нагрузка ниже заданного порога"));
        Assert.DoesNotContain(alarmEvents, e => e.Text.Contains("Брутто"));

        var invalid = Ok(T0.AddSeconds(1));
        invalid.Brutto = float.NaN;
        var invalidEvents = m.Apply(invalid);
        Assert.Contains(invalidEvents, e => e.Text.Contains("Некорректное числовое значение"));
        Assert.DoesNotContain(invalidEvents, e => e.Text.Contains("Брутто"));

        var recovered = Ok(T0.AddSeconds(2));
        var recoveredEvents = m.Apply(recovered);
        Assert.Contains(recoveredEvents, e => e.Text.StartsWith($"Брутто: {recovered.FormatWeight(recovered.Brutto)} кг;"));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]
    [InlineData(2, true, false)]
    [InlineData(3, true, true)]
    [InlineData(4, false, false)]
    [InlineData(5, false, true)]
    [InlineData(6, true, false)]
    [InlineData(7, true, true)]
    public void Indicators_DecodeStableAndZeroIndependently(ushort status, bool stable, bool zero)
    {
        var m = Started(latch: false);
        m.Apply(Ok(T0, status: status));
        Assert.True(m.View.HasLiveIndication);
        Assert.Equal(stable, m.View.StableIndicator);
        Assert.Equal(zero, m.View.ZeroIndicator);
    }

    [Fact]
    public void Indicators_OffForStaleDataErrorsAndRechecking()
    {
        var m = Started();
        m.Apply(Ok(T0, status: 3));
        Assert.True(m.View.StableIndicator);
        Assert.True(m.View.ZeroIndicator);
        m.Apply(Fail(T0.AddSeconds(1)));
        Assert.False(m.View.StableIndicator);
        Assert.False(m.View.ZeroIndicator);
        m.Stop(T0.AddSeconds(2), "оператором");
        Assert.False(m.View.HasLiveIndication);
        m.Start(Settings(), true, T0.AddSeconds(3));
        Assert.False(m.View.HasLiveIndication);
        m.Apply(Ok(T0.AddSeconds(4), error: 2, status: 3));
        Assert.False(m.View.HasLiveIndication);
        m.Apply(Ok(T0.AddSeconds(5), status: 3));
        Assert.True(m.View.WeightIsMessage);
        Assert.False(m.View.StableIndicator);
        Assert.False(m.View.ZeroIndicator);
    }

    [Fact]
    public void LatchedError_HidesIndicators()
    {
        var m = Started();
        for (int i = 0; i < 3; i++) m.Apply(Ok(T0.AddSeconds(i), error: 2));
        m.Apply(Ok(T0.AddSeconds(3)));
        Assert.Equal("Фиксация Аварии 0x0002", m.View.WeightText);
        Assert.True(m.View.ShowLatchReset);
        Assert.False(m.View.StableIndicator);
        Assert.False(m.View.ZeroIndicator);
    }

    [Fact]
    public void Weight_HandlesLargeFiniteWeight()
    {
        var m = Started(latch: false);
        var r = Ok(T0);
        r.Brutto = float.MaxValue;
        m.Apply(r);
        Assert.False(m.View.WeightIsMessage);
        Assert.EndsWith(" кг", m.View.WeightText);
    }

    [Fact]
    public void InvalidWeight_IsNotDisplayedAsValid()
    {
        var m = Started(latch: false);
        var r = Ok(T0);
        r.Brutto = float.NaN;
        m.Apply(r);
        Assert.Equal("Некорректные данные", m.View.WeightText);
        Assert.Equal(Tone.Bad, m.View.WeightTone);
    }

    [Fact]
    public void Instrument_FormatsAllFields_AndPreservesSnapshot()
    {
        var m = Started(latch: false);
        var r = Ok(T0);
        r.Division = 0.02f; r.Brutto = 10; r.Netto = 9; r.Tare = 1;
        r.Unit = "т"; r.Range = 1; r.ScaleSerialNumber = 45000;
        r.Ranges = new[] { new ScaleRange(1, 0.4f, 60, 0.02f), new ScaleRange(2, 1, 80, 0.05f) };
        m.Apply(r);
        var instrument = m.View.Instrument!;
        Assert.Equal(r.FormatWeight(9), instrument.NettoText);
        Assert.Equal(r.FormatWeight(1), instrument.TareText);
        Assert.Equal(r.FormatWeight(10), instrument.BruttoText);
        Assert.Equal(r.FormatWeight(0.02f), instrument.DivisionText);
        Assert.Equal(r.FormatWeight(60), instrument.Ranges[0].MaximumText);
        Assert.Equal(r.FormatWeight(0.4f), instrument.Ranges[0].MinimumText);
        Assert.Equal(r.FormatWeight(0.05f), instrument.Ranges[1].DivisionText);
        Assert.Equal(45000, instrument.ScaleSerialNumber);
        Assert.True(instrument.IsStable);
        Assert.False(instrument.IsZero);
        m.Apply(Fail(T0.AddSeconds(1)));
        Assert.Equal(instrument, m.View.Instrument);
        Assert.Equal(Tone.Stale, m.View.WeightTone);
        m.Stop(T0.AddSeconds(2), "оператором");
        Assert.Equal(instrument, m.View.Instrument);
        m.Start(Settings(), false, T0.AddSeconds(3));
        Assert.Equal(instrument, m.View.Instrument);
        m.Apply(Ok(T0.AddSeconds(4), error: 2));
        Assert.True(m.View.Instrument!.IsAlarm);
        Assert.Equal("Авария", m.View.Instrument.BruttoText);
    }

    [Fact]
    public void LegacyDisplaySettings_AreIgnored()
    {
        var m = new DeviceCardModel(1);
        var saved = AppSettings.FromJson("""{"First":{"Ip":"10.0.1.18","ReadUnrounded":true,"Digits":8}}""");
        var settings = saved.First;
        m.Start(settings, false, T0);
        var r = Ok(T0);
        m.Apply(r);
        Assert.Equal(Weight(r), m.View.WeightText);
        Assert.DoesNotContain("ReadUnrounded", saved.ToJson());
    }

    [Fact]
    public void PeriodOverrun_Events_OnTransitionsOnly()
    {
        var m = Started();
        M10PollResult Over(int s, bool o) { var r = Ok(T0.AddSeconds(s)); r.PeriodOverrun = o; r.PlannedPeriodMs = 250; return r; }
        m.Apply(Over(0, false));
        var e1 = m.Apply(Over(1, true));
        Assert.Contains(e1, e => e.Text == "Период 250 мс не выдерживается: обмен длится дольше периода." && e.Tone == Tone.Warn);
        Assert.DoesNotContain(m.Apply(Over(2, true)), e => e.Text.StartsWith("Период"));
        Assert.Contains(m.Apply(Over(3, false)), e => e.Text == "Период снова выдерживается.");
    }
}
