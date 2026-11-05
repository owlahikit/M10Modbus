using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace M10Modbus.Tests;

public class M10ModbusClientTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task Poll_DecodesRangesAndGross_InEitherWordOrder(int scale, bool lowWordFirst)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requests = new List<(ushort Address, ushort Count)>();
        Task server = ServeAsync();
        using var client = new M10ModbusClient("127.0.0.1", port, scaleNumber: scale);
        var configTraces = new List<ModbusTrace>();
        var traces = new List<ModbusTrace>();
        var timeline = new List<string>();
        var identifications = new List<CardEvent>();
        client.Identified += e => { identifications.Add(e); timeline.Add("identified"); };
        client.Trace += trace =>
        {
            traces.Add(trace);
            timeline.Add($"{trace.Direction}:{trace.StartAddress}");
            if (trace.Phase == TracePhase.ScaleConfig && trace.Direction == "RX") configTraces.Add(trace);
        };
        M10PollResult first = await client.PollAsync(PollSlot.Immediate, deadline.Token);
        M10PollResult second = await client.PollAsync(PollSlot.Immediate, deadline.Token);
        await client.ConnectAsync(deadline.Token);
        M10PollResult restored = await client.PollAsync(PollSlot.Immediate, deadline.Token);
        await server;

        Assert.True(first.IsConnected, first.ConnectionError);
        Assert.True(second.IsConnected, second.ConnectionError);
        Assert.True(restored.IsConnected, restored.ConnectionError);
        Assert.Equal(new[] { "TX:1000", "RX:1000", "identified", $"TX:{1100 + 33 * (scale - 1)}" }, timeline.Take(4));
        Assert.Equal(2, identifications.Count);
        Assert.Equal(traces[1].Timestamp, identifications[0].Time);
        Assert.Equal(traces[1].ExchangeId, identifications[0].ExchangeId);
        Assert.NotEqual(first.ExchangeId, second.ExchangeId);
        Assert.NotEqual(first.ExchangeId, restored.ExchangeId);
        Assert.All(traces.GroupBy(t => t.ExchangeId), exchange =>
        {
            Assert.NotEqual(Guid.Empty, exchange.Key);
            Assert.Equal(new[] { "TX", "RX" }, exchange.Select(t => t.Direction));
        });
        Assert.Contains(traces, t => t.Direction == "RX" && t.Phase == TracePhase.Poll && t.ExchangeId == first.ExchangeId);
        Assert.Equal(10f, first.Brutto);
        Assert.Equal(10f, second.Brutto);
        Assert.Equal("10.000", first.FormatWeight(first.Brutto, CultureInfo.InvariantCulture));
        Assert.Equal("0.020", first.FormatWeight(first.Division, CultureInfo.InvariantCulture));
        Assert.Equal("т", first.Unit);
        Assert.Equal(45000, first.ScaleSerialNumber);
        Assert.Equal(new[] { new ScaleRange(1, 0.4f, 60, 0.02f), new ScaleRange(2, 1, 80, 0.05f) }, first.Ranges);
        Assert.Equal(first.Ranges, restored.Ranges);
        Assert.True(restored.Reinitialized);
        Assert.Equal(2, configTraces.Count);
        Assert.All(configTraces, trace =>
        {
            Assert.Equal(lowWordFirst, trace.LowWordFirst);
            var decoded = TraceFormatter.FormatInitializationData(trace, 1)!.Value;
            Assert.False(decoded.IsError);
            Assert.Contains("SN весов=45000", decoded.Text);
            Assert.Contains("Max=60", decoded.Text);
        });
        ushort configAddress = (ushort)(1100 + 33 * (scale - 1));
        ushort currentAddress = (ushort)(2000 + 33 * (scale - 1));
        Assert.Equal(new (ushort, ushort)[]
        {
            (1000, 9), (configAddress, 22), (currentAddress, 13), (currentAddress, 13),
            (1000, 9), (configAddress, 22), (currentAddress, 13)
        }, requests);

        async Task ServeAsync()
        {
            for (int connection = 0; connection < 2; connection++)
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
                using NetworkStream stream = peer.GetStream();
                int exchanges = connection == 0 ? 4 : 3;
                for (int exchange = 0; exchange < exchanges; exchange++)
                {
                    var request = new byte[12];
                    await stream.ReadExactlyAsync(request, deadline.Token);
                    ushort address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8));
                    ushort count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10));
                    requests.Add((address, count));
                    var words = new ushort[count];
                    if (address == 1000)
                    {
                        words[0] = 100; words[1] = 1; words[8] = lowWordFirst ? (ushort)1 : (ushort)0;
                        Put32(words, 2, 0x00010001);
                    }
                    else if (address < 2000)
                    {
                        words[0] = 45000; words[1] = 1; words[2] = 2; words[3] = 2;
                        PutFloat(words, 4, 0.4f); PutFloat(words, 6, 60); PutFloat(words, 8, 0.02f);
                        PutFloat(words, 10, 1); PutFloat(words, 12, 80); PutFloat(words, 14, 0.05f);
                    }
                    else
                    {
                        Put32(words, 0, 1730228779);
                        words[3] = 2; words[4] = 1;
                        PutFloat(words, 5, 0.02f); PutFloat(words, 7, 10);
                        PutFloat(words, 9, 10); PutFloat(words, 11, 0);
                    }
                    var response = new byte[9 + 2 * count];
                    request.AsSpan(0, 4).CopyTo(response);
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(3 + 2 * count));
                    response[6] = request[6]; response[7] = 3; response[8] = (byte)(2 * count);
                    for (int i = 0; i < count; i++)
                        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9 + 2 * i), words[i]);
                    await stream.WriteAsync(response, deadline.Token);
                }
            }
        }

        void PutFloat(ushort[] words, int offset, float value) =>
            Put32(words, offset, BitConverter.SingleToUInt32Bits(value));

        void Put32(ushort[] words, int offset, uint value)
        {
            words[offset] = lowWordFirst ? (ushort)value : (ushort)(value >> 16);
            words[offset + 1] = lowWordFirst ? (ushort)(value >> 16) : (ushort)value;
        }
    }
}

public class M10PollingRegressionTests
{
    [Theory]
    [InlineData(100, 0, true)]
    [InlineData(100, 2, true)]
    [InlineData(100, 65535, true)]
    [InlineData(99, 1, false)]
    [InlineData(0, 1, false)]
    public async Task Identification_ChecksM10Type_NotFirmware(ushort type, ushort firmwareMajor, bool accepted)
    {
        await using var server = new MockM10((address, count) =>
        {
            var words = Words(address, count);
            if (address == 1000) { words[0] = type; words[2] = firmwareMajor; }
            return words;
        });
        using var client = new M10ModbusClient("127.0.0.1", server.Port);
        var identified = new List<CardEvent>();
        client.Identified += identified.Add;
        var result = await client.PollAsync();
        Assert.Equal(accepted, result.IsConnected);
        Assert.Equal(accepted ? 1 : 0, identified.Count);
        if (!accepted)
        {
            Assert.True(result.RequiresOperatorAction);
            Assert.Contains("ожидался M10 (100)", result.ConnectionError);
            Assert.Equal(new ushort[] { 1000 }, server.Addresses.ToArray());
            await client.PollAsync();
            Assert.Equal(1, server.Connections);
        }
    }

    [Fact]
    public async Task ZeroScaleTwoConfiguration_PausesWithoutReconnect_WhileOtherDevicePolls()
    {
        await using var missing = new MockM10((address, count) => address == 1133 ? new ushort[count] : Words(address, count));
        await using var healthy = new MockM10(Words);
        using var badClient = new M10ModbusClient("127.0.0.1", missing.Port, scaleNumber: 2);
        using var goodClient = new M10ModbusClient("127.0.0.1", healthy.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var identified = new List<CardEvent>();
        badClient.Identified += identified.Add;

        M10PollResult bad = await badClient.PollAsync(deadline.Token);
        for (int i = 0; i < 10; i++)
        {
            M10PollResult repeated = await badClient.PollAsync(deadline.Token);
            Assert.Equal(PollFailure.Configuration, repeated.Failure);
            Assert.True(repeated.RequiresOperatorAction);
        }
        M10PollResult good = await goodClient.PollAsync(deadline.Token);
        M10PollResult goodAgain = await goodClient.PollAsync(deadline.Token);

        Assert.False(bad.IsConnected);
        Assert.Single(identified); // The first valid response establishes exchange even if configuration fails next.
        Assert.True(bad.Timestamp >= identified[0].Time);
        Assert.Contains($"127.0.0.1:{missing.Port}", bad.ConnectionError);
        Assert.Contains("весы 2", bad.ConnectionError);
        Assert.Contains("1133–1154", bad.ConnectionError);
        Assert.Equal(new ushort[] { 1000, 1133 }, missing.Addresses.ToArray());
        Assert.Equal(1, missing.Connections);
        Assert.True(good.IsConnected, good.ConnectionError);
        Assert.True(goodAgain.IsConnected, goodAgain.ConnectionError);
        Assert.Equal(new ushort[] { 1000, 1100, 2000, 2000 }, healthy.Addresses.ToArray());
        Assert.Equal("10.000", good.FormatWeight(good.Brutto, CultureInfo.InvariantCulture));

        var model = new DeviceCardModel(2);
        model.Start(new DeviceSettings(), false, DateTime.Now);
        model.Apply(bad);
        Assert.Contains("приостановлен", model.View.StatusText);
        Assert.DoesNotContain("переподключение", model.View.StatusText);
        Assert.Contains("весы 2", model.View.StateText);
    }

    [Fact]
    public async Task InvalidOptionalRanges_WarnsButKeepsPolling()
    {
        await using var server = new MockM10((address, count) =>
        {
            ushort[] words = Words(address, count);
            if (address == 1100) words[3] = 0;
            return words;
        });
        using var client = new M10ModbusClient("127.0.0.1", server.Port);
        M10PollResult result = await client.PollAsync();
        M10PollResult next = await client.PollAsync();
        Assert.True(result.IsConnected, result.ConnectionError);
        Assert.Contains("1103", result.ConfigurationWarning);
        Assert.Empty(result.Ranges);
        Assert.True(next.IsConnected);
        Assert.Equal(1, server.Connections);
        var model = new DeviceCardModel(1);
        model.Start(new DeviceSettings(), false, DateTime.Now);
        Assert.Contains(model.Apply(result), e => e.Text.Contains("Таблица диапазонов недоступна"));
        Assert.Equal(CardPhase.Live, model.Phase);
        Assert.DoesNotContain(model.Apply(next), e => e.Text.Contains("Таблица диапазонов недоступна"));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(6, false)]
    public async Task ModbusException_IsDistinguishedFromTransport(byte code, bool permanent)
    {
        await using var server = new MockM10(Words, code);
        using var client = new M10ModbusClient("127.0.0.1", server.Port);
        M10PollResult result = await client.PollAsync();
        Assert.Equal(PollFailure.ModbusException, result.Failure);
        Assert.Equal(permanent, result.RequiresOperatorAction);
        Assert.Contains($"Modbus 0x{code:X2}", result.ConnectionError);
        await client.PollAsync();
        Assert.Equal(permanent ? 1 : 2, server.Connections);
    }

    [Fact]
    public async Task Timeout_IsTransientAndReconnectCanRecover()
    {
        await using var server = new MockM10(Words, timeoutFirst: true);
        using var client = new M10ModbusClient("127.0.0.1", server.Port) { TimeoutMs = 100 };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        M10PollResult failed = await client.PollAsync(deadline.Token);
        Assert.Equal(PollFailure.Transport, failed.Failure);
        Assert.False(failed.RequiresOperatorAction);
        // The first connection remains silent until the client closes it. The listener then
        // accepts the reconnect without relying on a delay relative to the client's timeout.
        client.TimeoutMs = 1500;
        M10PollResult recovered = await client.PollAsync(deadline.Token);
        Assert.True(recovered.IsConnected, recovered.ConnectionError);
        Assert.Equal(2, server.Connections);
    }

    private static ushort[] Words(ushort address, ushort count)
    {
        var words = new ushort[count];
        if (address == 1000) { words[0] = 100; words[2] = 1; }
        else if (address < 2000)
        {
            words[0] = 123; words[1] = 1; words[3] = 1;
            PutFloat(words, 6, 60); PutFloat(words, 8, 0.02f);
        }
        else
        {
            words[3] = 2; words[4] = 1;
            PutFloat(words, 5, 0.02f); PutFloat(words, 7, 10); PutFloat(words, 9, 10);
        }
        return words;
    }

    private static void PutFloat(ushort[] words, int offset, float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        words[offset] = (ushort)(bits >> 16); words[offset + 1] = (ushort)bits;
    }

    private sealed class MockM10 : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        private readonly Func<ushort, ushort, ushort[]> _words;
        private readonly byte _exception;
        private readonly bool _timeoutFirst;
        private int _connections;
        public int Connections => Volatile.Read(ref _connections);
        public ConcurrentQueue<ushort> Addresses { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public MockM10(Func<ushort, ushort, ushort[]> words, byte exception = 0, bool timeoutFirst = false)
        {
            _words = words; _exception = exception; _timeoutFirst = timeoutFirst;
            _listener.Start();
            _server = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using TcpClient peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                    int connection = Interlocked.Increment(ref _connections);
                    using NetworkStream stream = peer.GetStream();
                    while (true)
                    {
                        byte[] request = new byte[12];
                        try { await stream.ReadExactlyAsync(request, _stop.Token); }
                        catch (EndOfStreamException) { break; }
                        ushort address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8));
                        ushort count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10));
                        Addresses.Enqueue(address);
                        if (_timeoutFirst && connection == 1)
                        {
                            await stream.ReadAsync(new byte[1], _stop.Token);
                            break;
                        }
                        byte[] response;
                        if (_exception != 0)
                        {
                            response = new byte[9]; response[5] = 3; response[7] = 0x83; response[8] = _exception;
                        }
                        else
                        {
                            ushort[] words = _words(address, count);
                            response = new byte[9 + 2 * count];
                            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(3 + 2 * count));
                            response[7] = 3; response[8] = (byte)(2 * count);
                            for (int i = 0; i < count; i++)
                                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9 + 2 * i), words[i]);
                        }
                        request.AsSpan(0, 4).CopyTo(response); response[6] = request[6];
                        await stream.WriteAsync(response, _stop.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _server; }
            finally { _listener.Stop(); _stop.Dispose(); }
        }
    }
}
