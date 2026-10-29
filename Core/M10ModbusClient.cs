using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace M10Modbus
{
    public sealed class M10PollResult
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        // True only after a complete, validated Modbus exchange and device identification.
        public bool IsConnected { get; set; }
        public string ConnectionError { get; set; } = "";
        public ushort ErrorCode { get; set; }
        public ushort StatusMask { get; set; }
        public bool HasScaleError => ErrorCode != 0;
        public bool IsStable => (StatusMask & 2) != 0;
        public bool IsZero => (StatusMask & 1) != 0;
        public bool IsBelowMinimum => (StatusMask & 4) != 0;
        public float Brutto { get; set; }
        public float? BruttoUnrounded { get; set; }
        public string UnroundedError { get; set; } = "";
        public float Netto { get; set; }
        public float Tare { get; set; }
        public float Division { get; set; }
        public ushort Range { get; set; }
        public uint MeasurementUnixTime { get; set; }
        public string Unit { get; set; } = "";
        public string DeviceInfo { get; set; } = "";
        public int ScaleNumber { get; set; } = 1;
        /// <summary>Whole PollAsync call, including (re)connection and initialization. Not shown in the UI.</summary>
        public double ElapsedMs { get; set; }
        /// <summary>Transaction number of the current-weight read (0 if it was not sent).</summary>
        public ushort TransactionId { get; set; }
        /// <summary>TX → last byte of the current-weight response, ms.</summary>
        public double ResponseMs { get; set; }
        /// <summary>Stopwatch ticks right before the current-weight request was written; 0 if not sent.</summary>
        public long PollTxTicks { get; set; }
        /// <summary>Connection and/or initialization were performed in this call.</summary>
        public bool Reinitialized { get; set; }
        public bool PeriodOverrun { get; set; }
        public double PlannedPeriodMs { get; set; }
        public bool HasValidWeight => IsConnected && !HasScaleError &&
            float.IsFinite(Brutto) && float.IsFinite(Netto) && float.IsFinite(Tare) &&
            float.IsFinite(Division) && Division > 0;

        public string FormatWeight(float value, IFormatProvider? provider = null)
        {
            if (!float.IsFinite(value) || !float.IsFinite(Division) || Division <= 0) return "—";
            // G7 removes binary float noise; unlike -log10(d), this also handles d = 0.25 or 0.125.
            string[] parts = Division.ToString("G7", CultureInfo.InvariantCulture).Split('E', 'e');
            int dot = parts[0].IndexOf('.');
            int fraction = dot < 0 ? 0 : parts[0].Length - dot - 1;
            int exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
            int decimals = Math.Max(0, fraction - exponent);
            return value.ToString(decimals <= 9 ? "F" + decimals : "G9", provider ?? CultureInfo.CurrentCulture);
        }

        public string GetMetrologicalErrorDescription()
        {
            if (ErrorCode == 0) return "Ошибок нет";
            var parts = new List<string>();
            if ((ErrorCode & 1) != 0) parts.Add("клеймо нарушено");
            if ((ErrorCode & 2) != 0) parts.Add("нагрузка выше заданного порога");
            if ((ErrorCode & 4) != 0) parts.Add("нагрузка ниже заданного порога");
            if ((ErrorCode & 8) != 0) parts.Add("авария платформы");
            if ((ErrorCode & 0xFFF0) != 0) parts.Add($"неописанные биты 0x{ErrorCode & 0xFFF0:X4}");
            return $"0x{ErrorCode:X4}: {string.Join("; ", parts)}";
        }
    }

    public enum TracePhase { Identification, ScaleConfig, Poll }

    /// <summary>Planned period and overrun flag of one current-weight read, from <see cref="PollScheduler"/>.</summary>
    public readonly record struct PollSlot(double PlannedPeriodMs, bool Overrun)
    {
        public static PollSlot Immediate => new(0, false);
    }

    /// <summary>One complete Modbus TCP ADU, or the exact received prefix on a failed read.
    /// This is application traffic (MBAP + PDU), without Ethernet/IP/TCP headers or CRC.</summary>
    public sealed class ModbusTrace
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string Direction { get; init; } = "";
        public string Endpoint { get; init; } = "";
        /// <summary>Transaction number of the request (for RX too).</summary>
        public ushort TransactionId { get; init; }
        public byte UnitId { get; init; }
        public ushort StartAddress { get; init; }
        public ushort RegisterCount { get; init; }
        public byte[] Bytes { get; init; } = Array.Empty<byte>();
        public bool IsComplete { get; init; } = true;
        public TracePhase Phase { get; init; } = TracePhase.Poll;
        public int ScaleNumber { get; init; } = 1;
        /// <summary>RX: from TX (right before WriteAsync) to the last byte, or to the failure.</summary>
        public double? ResponseMs { get; init; }
        /// <summary>Current-weight TX: from the previous current-weight TX of this client; null for the first one after (re)initialization.</summary>
        public double? PeriodMs { get; init; }
        public double PlannedPeriodMs { get; init; }
        public bool Overrun { get; init; }
        /// <summary>Incomplete RX: 6 + MBAP length, when at least 6 bytes were received.</summary>
        public int? ExpectedLength { get; init; }
        public string Note { get; init; } = "";
        public override string ToString() => TraceFormatter.Format(this, 0).Text;
    }

    /// <summary>Read-only M10 monitor, protocol draft dated 2026-10-22.
    /// Connect/initialization/poll exchanges are serialized. Cancellation is propagated.</summary>
    public sealed class M10ModbusClient : IDisposable
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private ushort _transactionId;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _initialized;
        private bool _lowWordFirst;
        private string _unit = "";
        private string _deviceInfo = "";
        private bool _disposed;
        private int _timeoutMs = 1500;
        private long _lastPollTxTicks;
        public string IpAddress { get; }
        public int Port { get; }
        public byte UnitId { get; }
        public int ScaleNumber { get; }
        public bool ReadUnrounded { get; }
        public string Endpoint => FormatEndpoint(IpAddress, Port);
        public static string FormatEndpoint(string ip, int port) => ip.Contains(':') ? $"[{ip}]:{port}" : $"{ip}:{port}";
        public ushort CurrentAddress => (ushort)(2000 + 33 * (ScaleNumber - 1));
        public const ushort CurrentRegisterCount = 13; // DT, errors, status, range, d, gross, net, tare
        public int TimeoutMs
        {
            get => _timeoutMs;
            set => _timeoutMs = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }
        public bool IsConnected => _stream != null && _tcpClient?.Connected == true;
        public event Action<ModbusTrace>? Trace;

        public M10ModbusClient(string ipAddress, int port = 502, byte unitId = 1, int scaleNumber = 1, bool readUnrounded = false)
        {
            if (string.IsNullOrWhiteSpace(ipAddress)) throw new ArgumentException("Не задан адрес прибора.", nameof(ipAddress));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (scaleNumber < 1 || scaleNumber > 3) throw new ArgumentOutOfRangeException(nameof(scaleNumber));
            IpAddress = ipAddress.Trim(); Port = port; UnitId = unitId; ScaleNumber = scaleNumber; ReadUnrounded = readUnrounded;
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { ThrowIfDisposed(); await ConnectCoreAsync(ct).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        private async Task ConnectCoreAsync(CancellationToken ct)
        {
            Disconnect();
            var tcp = new TcpClient { NoDelay = true };
            _tcpClient = tcp;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeoutMs);
            try
            {
                await tcp.ConnectAsync(IpAddress, Port, timeout.Token).ConfigureAwait(false);
                _stream = tcp.GetStream();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Disconnect(); throw new TimeoutException($"Подключение к {IpAddress}:{Port}: таймаут {TimeoutMs} мс.");
            }
            catch { Disconnect(); throw; }
        }

        public Task<M10PollResult> PollAsync(CancellationToken ct = default) => PollAsync(PollSlot.Immediate, ct);

        public async Task<M10PollResult> PollAsync(PollSlot slot, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            var timer = Stopwatch.StartNew();
            var result = new M10PollResult { ScaleNumber = ScaleNumber, PeriodOverrun = slot.Overrun, PlannedPeriodMs = slot.PlannedPeriodMs };
            var exchange = new Exchange();
            try
            {
                ThrowIfDisposed();
                ct.ThrowIfCancellationRequested();
                if (!IsConnected) { result.Reinitialized = true; await ConnectCoreAsync(ct).ConfigureAwait(false); }
                if (!_initialized) { result.Reinitialized = true; await InitializeAsync(ct).ConfigureAwait(false); }
                ushort[] words = await ReadHoldingRegistersAsync(CurrentAddress, ReadUnrounded ? (ushort)15 : CurrentRegisterCount,
                    TracePhase.Poll, slot, exchange, ct).ConfigureAwait(false);
                result.Timestamp = exchange.RxTime;
                result.MeasurementUnixTime = ReadUInt32(words, 0);
                result.ErrorCode = words[2]; result.StatusMask = words[3]; result.Range = words[4];
                result.Division = ReadFloat(words, 5); result.Brutto = ReadFloat(words, 7);
                result.Netto = ReadFloat(words, 9); result.Tare = ReadFloat(words, 11);
                if (ReadUnrounded)
                {
                    float unrounded = ReadFloat(words, 13);
                    if (float.IsFinite(unrounded)) result.BruttoUnrounded = unrounded;
                    else result.UnroundedError = "Брутто без округления: NaN/Infinity";
                }
                result.Unit = _unit; result.DeviceInfo = _deviceInfo; result.IsConnected = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Disconnect(); throw;
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is TimeoutException || ex is InvalidDataException)
            {
                Disconnect(); result.Timestamp = DateTime.Now; result.ConnectionError = ex.Message;
            }
            finally
            {
                result.TransactionId = exchange.TransactionId;
                result.PollTxTicks = exchange.TxTicks;
                if (exchange.RxTicks != 0) result.ResponseMs = TicksToMs(exchange.RxTicks - exchange.TxTicks);
                result.ElapsedMs = timer.Elapsed.TotalMilliseconds; _gate.Release();
            }
            return result;
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            ushort[] info = await ReadHoldingRegistersAsync(1000, 9, TracePhase.Identification, PollSlot.Immediate, null, ct).ConfigureAwait(false);
            if (info[0] != 100) throw new InvalidDataException($"Регистр 1000: тип {info[0]}, ожидался M10 (100).");
            if (info[8] > 1) throw new InvalidDataException($"Регистр Endians (1008): неизвестное значение {info[8]}.");
            _lowWordFirst = info[8] == 1;
            uint firmware = ReadUInt32(info, 2);
            if ((firmware >> 16) != 1)
                throw new InvalidDataException($"Firmware {firmware >> 16}.{firmware & 0xFFFF:D3}: протокол рассчитан на 001.XXX.");
            ushort configAddress = (ushort)(1100 + 33 * (ScaleNumber - 1));
            ushort[] config = await ReadHoldingRegistersAsync(configAddress, 4, TracePhase.ScaleConfig, PollSlot.Immediate, null, ct).ConfigureAwait(false);
            if (config[1] > 1) throw new InvalidDataException($"Регистр {configAddress + 1}: неизвестная единица веса {config[1]}.");
            _unit = config[1] == 0 ? "кг" : "т";
            _deviceInfo = $"M10 SN {info[1]} · FW {firmware >> 16}.{firmware & 0xFFFF:D3} · " +
                $"весы {ScaleNumber} SN {config[0]} · {(_lowWordFirst ? "младшее слово первым" : "старшее слово первым")}";
            _initialized = true;
        }

        private sealed class Exchange
        {
            public ushort TransactionId;
            public long TxTicks;
            public long RxTicks;
            public DateTime RxTime;
        }

        private async Task<ushort[]> ReadHoldingRegistersAsync(ushort start, ushort count, TracePhase phase, PollSlot slot,
            Exchange? exchange, CancellationToken ct)
        {
            if (_stream == null) throw new IOException("TCP-соединение отсутствует.");
            var stream = _stream;
            ushort tid = unchecked(++_transactionId);
            byte[] request = { (byte)(tid >> 8), (byte)tid, 0, 0, 0, 6, UnitId, 3,
                (byte)(start >> 8), (byte)start, (byte)(count >> 8), (byte)count };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeoutMs); // One budget for the entire write + fragmented response.
            var received = new byte[260]; // Maximum Modbus TCP ADU.
            int used = 0;
            bool sent = false;
            bool responseLogged = false;
            bool peerClosed = false;
            long txTicks = 0;
            try
            {
                txTicks = Stopwatch.GetTimestamp();
                DateTime txTime = DateTime.Now;
                await stream.WriteAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
                sent = true;
                double? periodMs = null;
                if (phase == TracePhase.Poll)
                {
                    if (_lastPollTxTicks != 0) periodMs = TicksToMs(txTicks - _lastPollTxTicks);
                    _lastPollTxTicks = txTicks;
                    if (exchange != null) { exchange.TransactionId = tid; exchange.TxTicks = txTicks; }
                }
                Emit("TX", txTime, request, periodMs: periodMs,
                    plannedPeriodMs: phase == TracePhase.Poll ? slot.PlannedPeriodMs : 0, overrun: phase == TracePhase.Poll && slot.Overrun);
                await ReadToAsync(7).ConfigureAwait(false);
                int length = (received[4] << 8) | received[5];
                if (length < 2 || length > 254)
                    throw new InvalidDataException($"Некорректная длина MBAP: {length} (допустимо 2..254).");
                await ReadToAsync(6 + length).ConfigureAwait(false);
                long rxTicks = Stopwatch.GetTimestamp();
                DateTime rxTime = DateTime.Now;
                if (exchange != null) { exchange.RxTicks = rxTicks; exchange.RxTime = rxTime; }
                // Validate before logging so a rejected answer is never shown as a normal (green) RX.
                string? invalid =
                    ((received[0] << 8) | received[1]) != tid ? $"№ ответа {(received[0] << 8) | received[1]} не совпадает с № запроса {tid}."
                    : received[2] != 0 || received[3] != 0 ? "Идентификатор протокола в ответе не равен 0 (Modbus)."
                    : received[6] != UnitId ? $"Id ответа {received[6]}, ожидался Id {UnitId}."
                    : received[7] == 0x83 ? (length != 3 ? "Некорректная длина ответа с исключением Modbus." : null)
                    : received[7] != 3 ? $"Неожиданная функция 0x{received[7]:X2}, ожидалась 0x03."
                    : length != 3 + count * 2 || received[8] != count * 2 ? $"Длина FC03 не соответствует запросу {count} слов."
                    : null;
                Emit("RX", rxTime, received.AsSpan(0, used).ToArray(), responseMs: TicksToMs(rxTicks - txTicks), note: invalid ?? "");
                responseLogged = true;
                if (invalid != null) throw new InvalidDataException(invalid);
                if (received[7] == 0x83)
                    throw new InvalidDataException($"Modbus 0x{received[8]:X2}: {ExceptionDescription(received[8])}.");
                var words = new ushort[count];
                for (int i = 0; i < count; i++) words[i] = (ushort)((received[9 + i * 2] << 8) | received[10 + i * 2]);
                return words;
            }
            catch (Exception ex)
            {
                bool byOperator = ex is OperationCanceledException && ct.IsCancellationRequested;
                // After a sent request the operator must see that no (complete) answer arrived, even with 0 bytes.
                // A stop by the operator is only worth a line when part of the answer was already received.
                if (sent && !responseLogged && (!byOperator || used > 0))
                {
                    long failTicks = Stopwatch.GetTimestamp();
                    string note = byOperator ? "обмен прерван: опрос выключен"
                        : ex is OperationCanceledException ? $"нет полного ответа за {TimeoutMs} мс"
                        : peerClosed ? "прибор закрыл TCP-соединение"
                        : ex.Message;
                    Emit("RX", DateTime.Now, received.AsSpan(0, used).ToArray(), complete: false, responseMs: TicksToMs(failTicks - txTicks),
                        expectedLength: used >= 6 ? 6 + ((received[4] << 8) | received[5]) : null, note: note);
                }
                if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                    throw new TimeoutException($"Нет полного ответа за {TimeoutMs} мс (адрес {start}, слов {count}, получено {used} байт).");
                throw;
            }

            async Task ReadToAsync(int target)
            {
                while (used < target)
                {
                    int n = await stream.ReadAsync(received.AsMemory(used, target - used), timeout.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        peerClosed = true;
                        throw new IOException($"Прибор закрыл TCP-соединение; получено {used} байт ответа.");
                    }
                    used += n;
                }
            }
            void Emit(string direction, DateTime time, byte[] bytes, bool complete = true, double? responseMs = null,
                double? periodMs = null, double plannedPeriodMs = 0, bool overrun = false, int? expectedLength = null, string note = "")
            {
                Trace?.Invoke(new ModbusTrace { Timestamp = time, Direction = direction, Endpoint = Endpoint,
                    TransactionId = tid, UnitId = UnitId, StartAddress = start, RegisterCount = count,
                    Bytes = bytes, IsComplete = complete, Phase = phase, ScaleNumber = ScaleNumber,
                    ResponseMs = responseMs, PeriodMs = periodMs, PlannedPeriodMs = plannedPeriodMs,
                    Overrun = overrun, ExpectedLength = expectedLength, Note = note });
            }
        }

        private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        public static string ExceptionDescription(byte code) => code switch
        {
            1 => "функция не поддерживается", 2 => "недопустимый адрес регистра",
            3 => "недопустимое значение данных", 4 => "внутренняя ошибка устройства",
            6 => "устройство занято", _ => "неизвестное исключение"
        };
        private uint ReadUInt32(ushort[] words, int offset) => _lowWordFirst
            ? ((uint)words[offset + 1] << 16) | words[offset]
            : ((uint)words[offset] << 16) | words[offset + 1];
        private float ReadFloat(ushort[] words, int offset) => BitConverter.Int32BitsToSingle(unchecked((int)ReadUInt32(words, offset)));
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(M10ModbusClient)); }
        public void Disconnect()
        {
            _initialized = false;
            _lastPollTxTicks = 0;
            _stream?.Dispose(); _tcpClient?.Dispose(); _stream = null; _tcpClient = null;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; Disconnect(); }
    }
}
