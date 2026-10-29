using System.Globalization;

namespace M10Modbus
{
    public enum CardPhase { Off, Connecting, Live, CommError }

    /// <summary>Semantic tone; the UI maps it to colors.</summary>
    public enum Tone { Neutral, Stale, Info, Good, Warn, Bad }

    public sealed record CardView(
        string StatusText, Tone StatusTone, string StatusTip,
        string WeightText, bool WeightIsMessage, Tone WeightTone,
        string StateText, Tone StateTone, bool ShowLatchReset);

    /// <summary>Card history line and EVENT journal line. Time is the moment of the event, not of rendering.</summary>
    public sealed record CardEvent(DateTime Time, string Text, Tone Tone);

    /// <summary>State of one device card: error latching (D2), last-data snapshot («odometer») and connection phases.
    /// Not thread-safe: called on the UI thread only.</summary>
    public sealed class DeviceCardModel
    {
        private const int CleanResponsesRequired = 12;
        private const int FaultsToLatch = 3;

        private sealed record Snapshot(string WeightText, bool WeightIsMessage, string StateText, DateTime Time);

        private DeviceSettings _settings = new();
        private Snapshot? _snapshot;
        private bool _latchEnabled;
        private bool _latched;
        private bool _rechecking;
        private int _faults, _clean, _failures;
        private long _ok, _errors;
        private ushort _heldMask;
        private string _lastEventKey = "";
        private DateTime? _lastSuccess;
        private bool _overrun;
        private DateTime _responseTime;
        private double _responseMs;
        private string _deviceInfo = "";
        private string _connectionError = "";
        private string _weightText = "—";
        private bool _weightIsMessage;
        private Tone _weightTone = Tone.Stale;
        private string _stateText = "";
        private Tone _stateTone = Tone.Neutral;

        public DeviceCardModel(int deviceNumber)
        {
            DeviceNumber = deviceNumber;
            View = BuildView();
        }

        public int DeviceNumber { get; }

        /// <summary>Digits after the decimal point; −1 = auto (by d). Applied from the next response.</summary>
        public int Digits { get; set; } = -1;

        public CardPhase Phase { get; private set; } = CardPhase.Off;

        public CardView View { get; private set; }

        /// <summary>"ip:port" of the current/last polling session; prefix of EVENT journal lines.</summary>
        public string Endpoint { get; private set; } = "";

        public long Ok => _ok;

        public long Errors => _errors;

        public bool HasSnapshot => _snapshot != null;

        public IReadOnlyList<CardEvent> Start(DeviceSettings s, bool latchEnabled, DateTime now, int? periodMs = null, int? timeoutMs = null)
        {
            _settings = s;
            Endpoint = M10ModbusClient.FormatEndpoint(s.Ip.Trim(), s.Port);
            Digits = s.Digits ?? -1;
            _latchEnabled = latchEnabled; _latched = false; _rechecking = false;
            _faults = _clean = _failures = 0; _heldMask = 0;
            _ok = _errors = 0; _lastSuccess = null; _lastEventKey = ""; _overrun = false;
            _deviceInfo = ""; _connectionError = "";
            Phase = CardPhase.Connecting;
            string timing = periodMs.HasValue && timeoutMs.HasValue ? $"; период {periodMs} мс, таймаут {timeoutMs} мс" : "";
            var events = new List<CardEvent>
            {
                new(now, $"Опрос включён: {Endpoint}, Id {s.UnitId}, весы {s.Scale}, без округления: {YesNo(s.ReadUnrounded)}" +
                    $"{timing}, фиксация ошибок: {YesNo(latchEnabled)}.", Tone.Neutral)
            };
            View = BuildView();
            return events;
        }

        public IReadOnlyList<CardEvent> Stop(DateTime now, string reason)
        {
            if (Phase == CardPhase.Off) return Array.Empty<CardEvent>();
            Phase = CardPhase.Off;
            View = BuildView();
            return new[] { new CardEvent(now, $"Опрос выключен {reason}. Ответов {_ok}, сбоев {_errors}.", Tone.Neutral) };
        }

        /// <summary>Applies one poll result (D2 rules, unchanged). Event time = <see cref="M10PollResult.Timestamp"/>.</summary>
        public IReadOnlyList<CardEvent> Apply(M10PollResult r)
        {
            if (Phase == CardPhase.Off) return Array.Empty<CardEvent>();
            var events = new List<CardEvent>();
            void Event(string text, Tone tone) => events.Add(new CardEvent(r.Timestamp, text, tone));

            if (!r.IsConnected)
            {
                _errors++; _failures++; _clean = 0;
                _connectionError = r.ConnectionError;
                Phase = CardPhase.CommError;
                if (_failures == 1 || _failures % 10 == 0) Event($"Ошибка обмена #{_failures}: {r.ConnectionError}", Tone.Bad);
                View = BuildView();
                return events;
            }

            if (_failures > 0 || _lastSuccess == null)
                Event(_failures > 0 ? "Обмен восстановлен; конфигурация прочитана заново." : $"Обмен установлен; {r.DeviceInfo}.", Tone.Good);
            _failures = 0; _ok++; _lastSuccess = r.Timestamp;
            Phase = CardPhase.Live;
            _responseTime = r.Timestamp; _responseMs = r.ResponseMs; _deviceInfo = r.DeviceInfo;

            if (r.PeriodOverrun != _overrun)
            {
                _overrun = r.PeriodOverrun;
                if (_overrun)
                    Event($"Период {r.PlannedPeriodMs.ToString("0.#", CultureInfo.InvariantCulture)} мс не выдерживается: обмен длится дольше периода.", Tone.Warn);
                else
                    Event("Период снова выдерживается.", Tone.Neutral);
            }

            bool invalid = r.HasScaleError || !r.HasValidWeight || r.UnroundedError.Length > 0;
            string eventKey = $"{r.ErrorCode}/{r.StatusMask}/{r.HasValidWeight}/{r.UnroundedError}";
            if (_lastEventKey != eventKey)
            {
                _lastEventKey = eventKey;
                Event(r.HasScaleError ? r.GetMetrologicalErrorDescription()
                    : r.UnroundedError.Length > 0 ? r.UnroundedError
                    : !r.HasValidWeight ? "Некорректное числовое значение или дискретность d."
                    : $"{(r.IsStable ? "Стабильно" : "Не стабильно")}; {(r.IsBelowMinimum ? "вес ниже Min1" : "вес не ниже Min1")}; ноль: {YesNo(r.IsZero)}.",
                    invalid ? Tone.Bad : Tone.Neutral);
            }

            ApplyWeight(r, Event);
            _snapshot = new Snapshot(_weightText, _weightIsMessage, _stateText, r.Timestamp);
            View = BuildView();
            return events;
        }

        private void ApplyWeight(M10PollResult r, Action<string, Tone> Event)
        {
            if (r.HasScaleError)
            {
                _clean = 0;
                if (_latchEnabled) { _faults++; _heldMask |= r.ErrorCode; if (_faults >= FaultsToLatch) _latched = true; }
                ShowMessage($"Ошибка 0x{r.ErrorCode:X4}", Tone.Bad,
                    $"{r.GetMetrologicalErrorDescription()} · {(_latched ? "ошибка зафиксирована — снимите фиксацию после проверки" : "вес недействителен по флагу Errors")}");
                return;
            }
            if (!r.HasValidWeight)
            {
                _clean = 0;
                ShowMessage("Некорректные данные", Tone.Bad, "Числовые значения или дискретность d недопустимы — см. HEX/расшифровку");
                return;
            }
            if (_settings.ReadUnrounded && !r.BruttoUnrounded.HasValue)
            {
                _clean = 0;
                ShowMessage("Некорректные данные", Tone.Bad, $"{r.UnroundedError} · округлённое брутто — в расшифровке журнала");
                return;
            }
            if (_latched)
            {
                ShowMessage($"Фиксация 0x{_heldMask:X4}", Tone.Warn, "Сейчас ошибок нет · предыдущая ошибка зафиксирована");
                return;
            }
            if (_latchEnabled && (_faults > 0 || _rechecking))
            {
                _clean++;
                if (_clean < CleanResponsesRequired)
                {
                    ShowMessage($"Проверка {_clean}/{CleanResponsesRequired}", Tone.Warn, "Показания скрыты после ошибки · нужно 12 ответов без ошибок");
                    return;
                }
                _faults = _clean = 0; _heldMask = 0; _rechecking = false;
                Event("Проверка завершена: 12 последовательных корректных ответов.", Tone.Good);
            }

            string weight = _settings.ReadUnrounded ? r.BruttoUnrounded!.Value.ToString("G9", CultureInfo.CurrentCulture)
                : Digits < 0 ? r.FormatWeight(r.Brutto)
                : r.Brutto.ToString("F" + Digits.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
            bool accountable = r.IsStable && !r.IsBelowMinimum;
            Tone tone = accountable ? Tone.Good : Tone.Warn;
            string state = r.IsBelowMinimum ? "Ниже Min1 · не подлежит учёту" : r.IsStable ? "Стабильно" : "Не стабильно · не подлежит учёту";
            _weightText = weight + " " + r.Unit; _weightIsMessage = false; _weightTone = tone;
            _stateText = $"{state} · Нетто {r.FormatWeight(r.Netto)} · Тара {r.FormatWeight(r.Tare)} {r.Unit} · Ноль: {YesNo(r.IsZero)}";
            _stateTone = tone;
        }

        /// <summary>Local only: clears the latch and starts the 12-response recheck. Nothing is sent to the device.</summary>
        public IReadOnlyList<CardEvent> ResetLatch(DateTime now)
        {
            if (Phase == CardPhase.Off) return Array.Empty<CardEvent>();
            _latched = false; _rechecking = true; _faults = 0; _clean = 0; _heldMask = 0;
            ShowMessage("Повторная проверка", Tone.Warn, "Ожидание 12 корректных ответов");
            View = BuildView();
            return new[] { new CardEvent(now, "Локальная фиксация снята. Проверка 12 ответов; прибору ничего не отправлялось.", Tone.Neutral) };
        }

        private void ShowMessage(string weight, Tone tone, string state)
        {
            _weightText = weight; _weightIsMessage = true; _weightTone = tone;
            _stateText = state; _stateTone = tone;
        }

        private CardView BuildView()
        {
            bool showReset = Phase != CardPhase.Off && _latchEnabled && _faults > 0;
            string snapshotWeight = _snapshot?.WeightText ?? "—";
            bool snapshotIsMessage = _snapshot?.WeightIsMessage ?? false;
            string lastData = _snapshot != null ? $" · последние данные на {Time(_snapshot.Time)}" : "";
            switch (Phase)
            {
                case CardPhase.Off when _snapshot == null:
                    return new CardView("Опрос выключен", Tone.Stale, "", "—", false, Tone.Stale,
                        "Нет данных · включите «Опрос»", Tone.Neutral, false);
                case CardPhase.Off:
                    string stopped = $"Опрос остановлен{lastData} · ответов {_ok} · сбоев {_errors}";
                    return new CardView(stopped, Tone.Stale, _deviceInfo, snapshotWeight, snapshotIsMessage, Tone.Stale,
                        _snapshot!.StateText, Tone.Stale, false);
                case CardPhase.Connecting:
                    return new CardView($"Подключение к {Endpoint} · Id {_settings.UnitId} · инициализация…", Tone.Info, "",
                        snapshotWeight, snapshotIsMessage, Tone.Stale,
                        _snapshot?.StateText ?? "Ожидание первого ответа", _snapshot != null ? Tone.Stale : Tone.Neutral, showReset);
                case CardPhase.CommError:
                    return new CardView($"Нет связи · переподключение{lastData} · сбоев {_errors}", Tone.Bad, _connectionError,
                        snapshotWeight, snapshotIsMessage, Tone.Stale,
                        "Ошибка обмена: " + _connectionError, Tone.Bad, showReset);
                default:
                    string ms = _responseMs.ToString("F0", CultureInfo.InvariantCulture);
                    return new CardView($"Подключено · ответ {Time(_responseTime)} за {ms} мс · ответов {_ok} · сбоев {_errors}", Tone.Good, _deviceInfo,
                        _weightText, _weightIsMessage, _weightTone, _stateText, _stateTone, showReset);
            }
        }

        private static string Time(DateTime time) => time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        private static string YesNo(bool value) => value ? "да" : "нет";
    }
}
