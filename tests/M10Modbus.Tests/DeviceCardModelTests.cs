using System.Globalization;

namespace M10Modbus.Tests;

/// <summary>CardView texts/tones of DeviceCardModel (design.md 2.3–2.4).</summary>
public class DeviceCardModelTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 21, 47, 35, 703);

    private static DeviceSettings Settings(bool unrounded = false) =>
        new() { Ip = "10.0.1.18", Port = 502, UnitId = 1, Scale = 1, ReadUnrounded = unrounded };

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
        Assert.Equal("Опрос включён: 10.0.1.18:502, Id 1, весы 1, без округления: нет; период 250 мс, таймаут 1500 мс, фиксация ошибок: да.", ev.Single().Text);
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
        Assert.Equal(("Ошибка 0x0002", true, Tone.Bad), (m.View.WeightText, m.View.WeightIsMessage, m.View.WeightTone));
        Assert.Equal("0x0002: нагрузка выше заданного порога · вес недействителен по флагу Errors", m.View.StateText);
        Assert.True(m.View.ShowLatchReset);
        m.Apply(Ok(T0.AddSeconds(2), error: 4));
        m.Apply(Ok(T0.AddSeconds(3), error: 4));
        Assert.Equal("0x0004: нагрузка ниже заданного порога · ошибка зафиксирована — снимите фиксацию после проверки", m.View.StateText);

        for (int i = 0; i < 20; i++) m.Apply(Ok(T0.AddSeconds(4 + i)));
        Assert.Equal(("Фиксация 0x0006", Tone.Warn), (m.View.WeightText, m.View.WeightTone));
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
    public void Off_HidesLatchButton_SnapshotKeepsMessage()
    {
        var m = Started();
        m.Apply(Ok(T0, error: 2));
        Assert.True(m.View.ShowLatchReset);
        m.Stop(T0.AddSeconds(1), "оператором");
        Assert.False(m.View.ShowLatchReset);
        Assert.Equal(("Ошибка 0x0002", true, Tone.Stale), (m.View.WeightText, m.View.WeightIsMessage, m.View.WeightTone));
        Assert.Equal(Tone.Stale, m.View.StateTone);
        Assert.Contains("последние данные на 21:47:35.703", m.View.StatusText);
    }

    [Fact]
    public void InvalidData_And_Unrounded_And_Digits()
    {
        var m = Started();
        var bad = Ok(T0); bad.Division = 0;
        m.Apply(bad);
        Assert.Equal(("Некорректные данные", Tone.Bad), (m.View.WeightText, m.View.WeightTone));
        Assert.Equal("Числовые значения или дискретность d недопустимы — см. HEX/расшифровку", m.View.StateText);

        m.Digits = 2;
        var r = Ok(T0.AddSeconds(1));
        r.BruttoUnrounded = 1234.5f;
        m.Apply(r);
        Assert.Equal(1234.5f.ToString("F2", CultureInfo.CurrentCulture) + " кг", m.View.WeightText);

        var u = new DeviceCardModel(1);
        u.Start(Settings(unrounded: true), true, T0);
        var nan = Ok(T0); nan.UnroundedError = "Брутто без округления: NaN/Infinity";
        u.Apply(nan);
        Assert.Equal("Брутто без округления: NaN/Infinity · округлённое брутто — в расшифровке журнала", u.View.StateText);
        var good = Ok(T0.AddSeconds(1)); good.BruttoUnrounded = 1234.56f;
        u.Apply(good);
        Assert.Equal(1234.56f.ToString("G9", CultureInfo.CurrentCulture) + " кг", u.View.WeightText);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "0.5")]
    [InlineData(2, "0.48")]
    [InlineData(3, "0.475")]
    [InlineData(4, "0.4750")]
    [InlineData(5, "0.47500")]
    [InlineData(6, "0.475002")]
    [InlineData(7, "0.4750015")]
    [InlineData(8, "0.47500150")]
    public void ManualDigits_RoundUnroundedWeight(int digits, string expected)
    {
        var m = Started(latch: false);
        m.Digits = digits;
        var r = Ok(T0);
        r.Brutto = 0.48f;
        r.BruttoUnrounded = 0.4750015f;
        r.Unit = "т";
        m.Apply(r);
        Assert.True(m.NeedsUnrounded);
        Assert.Equal(expected.Replace(".", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator) + " т", m.View.WeightText);

        if (digits >= 4)
        {
            r.BruttoUnrounded = 0.4740019f;
            m.Apply(r);
            Assert.NotEqual(expected.Replace(".", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator) + " т", m.View.WeightText);
        }
    }

    [Theory]
    [InlineData(2.5f, "3")]
    [InlineData(-2.5f, "-3")]
    public void ManualDigits_MidpointsRoundAwayFromZero(float raw, string expected)
    {
        var m = Started(latch: false);
        m.Digits = 0;
        var r = Ok(T0);
        r.BruttoUnrounded = raw;
        m.Apply(r);
        Assert.Equal(expected + " кг", m.View.WeightText);
    }

    [Fact]
    public void DisplayModeChanges_ApplyToFollowingResponses_AndPreserveLastSnapshot()
    {
        var m = Started(latch: false);
        var r = Ok(T0);
        r.Brutto = 0.48f;
        r.Unit = "т";
        Assert.False(m.NeedsUnrounded);
        m.Apply(r);
        string original = m.View.WeightText;

        m.Digits = 7;
        Assert.True(m.NeedsUnrounded);
        m.Apply(r); // An already in-flight 13-register response.
        Assert.Equal("Ожидание точных данных", m.View.WeightText);
        m.Stop(T0.AddSeconds(1), "оператором");
        Assert.Equal(original, m.View.WeightText);
        Assert.Equal(Tone.Stale, m.View.WeightTone);

        m.Start(Settings(), false, T0.AddSeconds(2));
        m.Digits = 7;
        r.BruttoUnrounded = 0.4750015f;
        m.Apply(r);
        string rounded = "0.4750015".Replace(".", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator) + " т";
        Assert.Equal(rounded, m.View.WeightText);

        m.DisplayUnrounded = true;
        m.Apply(r);
        Assert.Equal(r.BruttoUnrounded.Value.ToString("G9", CultureInfo.CurrentCulture) + " т", m.View.WeightText);
        m.DisplayUnrounded = false;
        m.Apply(r);
        Assert.Equal(rounded, m.View.WeightText);
        m.Digits = -1;
        Assert.False(m.NeedsUnrounded);
        r.BruttoUnrounded = null;
        m.Apply(r);
        Assert.Equal(original, m.View.WeightText);

        m.DisplayUnrounded = true;
        Assert.True(m.NeedsUnrounded);
        m.Apply(r);
        Assert.Equal("Ожидание точных данных", m.View.WeightText);
        m.DisplayUnrounded = false;
        Assert.False(m.NeedsUnrounded);
        m.Apply(r);
        Assert.Equal(original, m.View.WeightText);
    }

    [Fact]
    public void DisplayModeChange_DoesNotHideLatchedError()
    {
        var m = Started();
        for (int i = 0; i < 3; i++) m.Apply(Ok(T0.AddSeconds(i), error: 2));
        m.DisplayUnrounded = true;
        m.Apply(Ok(T0.AddSeconds(3)));
        Assert.Equal("Фиксация 0x0002", m.View.WeightText);
        Assert.True(m.View.ShowLatchReset);
    }

    [Fact]
    public void ManualDigits_HandlesLargeFiniteWeight()
    {
        var m = Started(latch: false);
        m.Digits = 8;
        var r = Ok(T0);
        r.BruttoUnrounded = float.MaxValue;
        m.Apply(r);
        Assert.False(m.View.WeightIsMessage);
        Assert.EndsWith(" кг", m.View.WeightText);
    }

    [Fact]
    public void InvalidUnrounded_IsNotReplacedByRoundedWeight()
    {
        var m = Started(latch: false);
        m.Digits = 5;
        var r = Ok(T0);
        r.UnroundedError = "Брутто без округления: NaN/Infinity";
        m.Apply(r);
        Assert.Equal("Некорректные данные", m.View.WeightText);
        Assert.Equal(Tone.Bad, m.View.WeightTone);
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
