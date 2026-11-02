namespace M10Modbus.Tests;

public class AlarmIndicationTests
{
    private static readonly DateTime StartTime = new(2026, 11, 2, 11, 0, 0);

    private static DeviceCardModel Start(bool latch)
    {
        var model = new DeviceCardModel(1);
        model.Start(new DeviceSettings { Ip = "10.0.1.18", Scale = 1 }, latch, StartTime);
        return model;
    }

    private static M10PollResult Reading(float weight, ushort status, ushort range = 1, ushort errors = 0) => new()
    {
        Timestamp = StartTime, IsConnected = true, Brutto = weight, Netto = weight, Tare = 0,
        Division = range == 1 ? 0.02f : 0.05f, Range = range, StatusMask = status, ErrorCode = errors, Unit = "т",
        ScaleSerialNumber = 45000,
        Ranges = new[] { new ScaleRange(1, 0.4f, 60, 0.02f), new ScaleRange(2, 1, 80, 0.05f) }
    };

    [Theory]
    [InlineData(0, 7, 1, IndicatorState.Red, IndicatorState.Off, IndicatorState.Off)]
    [InlineData(0.399f, 2, 1, IndicatorState.Red, IndicatorState.Off, IndicatorState.Off)]
    [InlineData(0.4f, 2, 1, IndicatorState.Green, IndicatorState.Off, IndicatorState.Off)]
    [InlineData(30.4f, 2, 1, IndicatorState.Green, IndicatorState.Off, IndicatorState.Off)]
    [InlineData(60f, 2, 1, IndicatorState.Green, IndicatorState.Off, IndicatorState.Off)]
    [InlineData(60.8f, 0, 2, IndicatorState.Off, IndicatorState.Green, IndicatorState.Off)]
    [InlineData(60.95f, 0, 2, IndicatorState.Off, IndicatorState.Green, IndicatorState.Off)]
    [InlineData(80f, 2, 2, IndicatorState.Off, IndicatorState.Green, IndicatorState.Off)]
    [InlineData(80.001f, 2, 2, IndicatorState.Off, IndicatorState.Off, IndicatorState.Green)]
    [InlineData(80.001f, 2, 1, IndicatorState.Off, IndicatorState.Off, IndicatorState.Green)]
    [InlineData(80.4f, 0, 2, IndicatorState.Off, IndicatorState.Off, IndicatorState.Green)]
    [InlineData(80.45f, 2, 2, IndicatorState.Off, IndicatorState.Off, IndicatorState.Green)]
    [InlineData(80.5f, 2, 2, IndicatorState.Off, IndicatorState.Off, IndicatorState.Green)]
    public void RangeLights_DistinguishMinAndLastMax_WithoutRequiringStability(
        float weight, ushort status, ushort range, IndicatorState first, IndicatorState second, IndicatorState aboveMaximum)
    {
        var model = Start(latch: false);
        model.Apply(Reading(weight, status, range));
        Assert.Equal(first, model.View.Instrument!.Ranges[0].Indicator);
        Assert.Equal(second, model.View.Instrument.Ranges[1].Indicator);
        Assert.Equal(aboveMaximum, model.View.Instrument.AboveMaximumIndicator);
        Assert.False(model.View.Instrument.IsAlarm);
        Assert.Equal((status & 2) != 0, model.View.StableIndicator);
        Assert.Equal((status & 1) != 0, model.View.ZeroIndicator);
        if (weight > 80) Assert.StartsWith("Выше Max", model.View.StateText);
    }

    [Fact]
    public void NegativeSensorSignal_ShowsAlarmInGrid_AndRecoversImmediatelyWithoutLatch()
    {
        var model = Start(latch: false);
        model.Apply(Reading(0, 7));
        var fault = Reading(float.NaN, 4, errors: 2);
        model.Apply(fault);
        Assert.Equal("Авария", model.View.WeightText);
        Assert.True(model.View.Instrument!.IsAlarm);
        Assert.Equal("Авария", model.View.Instrument.NettoText);
        Assert.Equal("Авария", model.View.Instrument.BruttoText);
        Assert.True(model.View.Instrument.TareIsLastKnown);
        Assert.Equal(fault.FormatWeight(0), model.View.Instrument.TareText);
        Assert.Contains("нагрузка ниже заданного порога", model.View.StateText);
        Assert.DoesNotContain("выше", model.View.StateText);
        Assert.Equal(IndicatorState.Red, model.View.Instrument.Ranges[0].Indicator);
        Assert.Equal(IndicatorState.Off, model.View.Instrument.Ranges[1].Indicator);
        Assert.False(model.View.StableIndicator);
        Assert.False(model.View.ZeroIndicator);
        Assert.False(model.View.ShowLatchReset);
        var log = TraceFormatter.FormatData(fault, 1);
        Assert.Contains("0x0002: нагрузка ниже заданного порога", log.Text);
        Assert.True(log.IsError);
        model.Apply(Reading(30.4f, 2));
        Assert.False(model.View.WeightIsMessage);
        Assert.False(model.View.Instrument!.IsAlarm);
        Assert.True(model.View.StableIndicator);
    }

    [Fact]
    public void UpperAlarm_UsesErrorBitAndLightsAboveRange_NotInvalidWeight()
    {
        var model = Start(latch: false);
        model.Apply(Reading(float.NaN, 0, range: 1, errors: 4));
        Assert.Equal("Авария", model.View.WeightText);
        Assert.Contains("выше заданного порога", model.View.StateText);
        Assert.Equal(IndicatorState.Off, model.View.Instrument!.Ranges[0].Indicator);
        Assert.Equal(IndicatorState.Off, model.View.Instrument.Ranges[1].Indicator);
        Assert.Equal(IndicatorState.Green, model.View.Instrument.AboveMaximumIndicator);
        Assert.Equal("—", model.View.Instrument.TareText);
        var combined = Reading(0, 0, errors: 6).GetMetrologicalErrorDescription();
        Assert.Contains("ниже заданного порога", combined);
        Assert.Contains("выше заданного порога", combined);
    }

    [Fact]
    public void AboveMaximumIndicator_TransitionsFromNormalWeightThroughAlarmAndRecovery()
    {
        var model = Start(latch: false);
        model.Apply(Reading(80, 2, 2));
        Assert.Equal(IndicatorState.Off, model.View.Instrument!.AboveMaximumIndicator);
        model.Apply(Reading(80.4f, 0, 2));
        Assert.Equal(IndicatorState.Green, model.View.Instrument!.AboveMaximumIndicator);
        Assert.False(model.View.WeightIsMessage);
        model.Apply(Reading(float.NaN, 0, 2, errors: 4));
        Assert.Equal(IndicatorState.Green, model.View.Instrument!.AboveMaximumIndicator);
        Assert.Equal("Авария", model.View.WeightText);
        model.Apply(Reading(80, 2, 2));
        Assert.Equal(IndicatorState.Off, model.View.Instrument!.AboveMaximumIndicator);
        Assert.Equal(IndicatorState.Green, model.View.Instrument.Ranges[1].Indicator);
        Assert.False(model.View.WeightIsMessage);
    }

    [Fact]
    public void Limits_UseGrossAndMin1_NotNettoOrMinOfSecondRange()
    {
        var model = Start(latch: false);
        var withTare = Reading(30.4f, 2);
        withTare.Netto = 0; withTare.Tare = 30.4f;
        model.Apply(withTare);
        Assert.Equal(IndicatorState.Green, model.View.Instrument!.Ranges[0].Indicator);
        model.Apply(Reading(0.8f, 2, range: 2));
        Assert.Equal(IndicatorState.Green, model.View.Instrument!.Ranges[1].Indicator);
        var above = Reading(80.05f, 2, range: 2);
        above.Netto = 0;
        model.Apply(above);
        Assert.Equal(IndicatorState.Off, model.View.Instrument!.Ranges[1].Indicator);
        Assert.Equal(IndicatorState.Green, model.View.Instrument.AboveMaximumIndicator);
    }

    [Fact]
    public void OtherAlarm_DoesNotMarkRangeRed()
    {
        var model = Start(latch: false);
        model.Apply(Reading(float.NaN, 0, range: 2, errors: 8));
        Assert.True(model.View.Instrument!.IsAlarm);
        Assert.All(model.View.Instrument.Ranges, range => Assert.Equal(IndicatorState.Off, range.Indicator));
        Assert.Equal(IndicatorState.Off, model.View.Instrument.AboveMaximumIndicator);
    }

    [Fact]
    public void AlarmSnapshot_IsNotLiveAfterCommunicationFailureOrStop()
    {
        var model = Start(latch: false);
        model.Apply(Reading(-1, 4, errors: 2));
        Assert.True(model.View.HasFreshInstrument);
        var snapshot = model.View.Instrument;
        model.Apply(new M10PollResult { ConnectionError = "нет ответа" });
        Assert.False(model.View.HasFreshInstrument);
        Assert.Equal(snapshot, model.View.Instrument);
        Assert.False(model.View.StableIndicator);
        Assert.False(model.View.ZeroIndicator);
        model.Stop(StartTime.AddSeconds(1), "оператором");
        Assert.False(model.View.HasFreshInstrument);
        Assert.Equal(snapshot, model.View.Instrument);
    }

    [Fact]
    public void LatchAppearsAfterAlarmEnds_AndCannotShowGreenUntilReleased()
    {
        var model = Start(latch: true);
        model.Apply(Reading(30.4f, 2));
        for (int i = 0; i < 10; i++)
        {
            model.Apply(Reading(-1, 4, errors: 2));
            Assert.Equal("Авария", model.View.WeightText);
            Assert.Equal(IndicatorState.Red, model.View.Instrument!.Ranges[0].Indicator);
        }
        model.Apply(Reading(30.4f, 2));
        Assert.Equal("Фиксация Аварии 0x0002", model.View.WeightText);
        Assert.True(model.View.Instrument!.IsAlarm);
        Assert.Contains("0x0002", model.View.Instrument.NettoText);
        Assert.All(model.View.Instrument.Ranges, range => Assert.Equal(IndicatorState.Off, range.Indicator));
        Assert.Equal(IndicatorState.Off, model.View.Instrument.AboveMaximumIndicator);
        Assert.False(model.View.StableIndicator);
        model.ResetLatch(StartTime.AddSeconds(1));
        for (int i = 0; i < 11; i++)
        {
            model.Apply(Reading(30.4f, 2));
            Assert.True(model.View.WeightIsMessage);
        }
        model.Apply(Reading(30.4f, 2));
        Assert.False(model.View.WeightIsMessage);
        Assert.True(model.View.StableIndicator);
    }

    [Fact]
    public void SimulatorCycle_KeepsJoltsUnstable_AndRepeatsWithoutLatch()
    {
        var model = Start(latch: false);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            Apply(Reading(0, 7), 10);
            for (int second = 1; second <= 6; second++) Apply(Reading(30.4f * second / 6, 0), 1);
            Apply(Reading(30.4f, 2), 15);
            for (int second = 5; second >= 0; second--) Apply(Reading(30.4f * second / 6, 0), 1);
            Apply(Reading(0, 7), 10);
            for (int second = 1; second <= 4; second++)
                Apply(Reading(60.8f * second / 4, 0, (ushort)(second == 4 ? 2 : 1)), 1);
            Apply(Reading(60.8f, 0, 2), 2);
            Apply(Reading(60.95f, 0, 2), 2);
            Apply(Reading(60.8f, 2, 2), 15);
            for (int second = 4; second >= 0; second--) Apply(Reading(60.8f * second / 5, 0), 1);
            Apply(Reading(0, 7), 10);
            Apply(Reading(-1, 4, errors: 2), 10);
            Apply(Reading(0, 7), 1);
            Assert.False(model.View.WeightIsMessage);
        }

        void Apply(M10PollResult reading, int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                model.Apply(reading);
                Assert.Equal(reading.ErrorCode == 0 && reading.IsStable, model.View.StableIndicator);
                Assert.Equal(reading.ErrorCode != 0, model.View.Instrument!.IsAlarm);
                Assert.False(model.View.ShowLatchReset);
            }
        }
    }
}
