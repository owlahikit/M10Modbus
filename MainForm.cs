using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace M10Modbus
{
    public sealed class MainForm : Form
    {
        private sealed class DeviceRunner
        {
            public DeviceRunner(ScalePanel panel)
            {
                Panel = panel;
                Model = new DeviceCardModel(panel.DeviceNumber);
            }

            public int Number => Panel.DeviceNumber;
            public ScalePanel Panel { get; }
            public DeviceCardModel Model { get; }
            public CancellationTokenSource? Cts;
            public Task? Worker;
            public IDisposable? TimerRes;
            public Task? Stopping;
            /// <summary>Incremented on every start/stop; results of a finished polling session are dropped.</summary>
            public int Generation;
            public bool Running => Cts != null;
            public bool IsStopping => Stopping is { IsCompleted: false };
        }

        private const int MaxEntries = 5000;
        private static readonly Color TxColor = Color.FromArgb(25, 82, 150);
        private static readonly Color RxColor = Color.FromArgb(25, 115, 78);

        private readonly DeviceRunner[] _runners;
        private readonly NumericUpDown _period = new NumericUpDown { Minimum = 100, Maximum = 10000, Increment = 50, Value = 250, Width = 80 };
        private readonly NumericUpDown _timeout = new NumericUpDown { Minimum = 200, Maximum = 30000, Increment = 100, Value = 1500, Width = 80 };
        private readonly CheckBox _latch = new CheckBox { Text = "Фиксировать ошибки веса", Checked = true, AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
        private readonly Button _stopAll;
        private readonly SplitContainer _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1, Margin = Padding.Empty };
        private readonly RichTextBox _log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true,
            WordWrap = false, DetectUrls = false, BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.Gainsboro, Font = new Font("Consolas", 9F), BorderStyle = BorderStyle.None };
        private readonly ComboBox _view = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        private readonly ComboBox _deviceFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        private readonly CheckBox _hold = new CheckBox { Text = "Hold", Appearance = Appearance.Button, AutoSize = true };
        private readonly CheckBox _scroll = new CheckBox { Text = "Автопрокрутка", Checked = true, AutoSize = true, Margin = new Padding(10, 6, 6, 0) };
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolTip _tips = new ToolTip();
        private readonly Queue<LogEntry> _entries = new Queue<LogEntry>();
        private readonly string _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "M10MbTcp");
        private string SettingsFile => Path.Combine(_dataRoot, "settings.json");
        private SessionJournal? _journal;
        private Task _journalClosed = Task.CompletedTask;
        private string? _journalProblem;
        private string _sessionDirectory = "";
        private WindowSettings? _savedWindow;
        private int _cardsHeight = AppSettings.DefaultCardsHeight;
        private bool _shown;
        private bool _applyingSplitter;
        private bool _userMovingSplitter;
        private bool _suppressToggle;
        private bool _closing;
        private bool _allowClose;
        private int _periodMs = 250;
        private int _timeoutMs = 1500;
        private sealed record LogEntry(int Device, string Kind, string Text, Color Color);

        public MainForm()
        {
            SuspendLayout();
            Text = "M10 · Монитор Modbus TCP";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1200, 850);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SystemColors.Control;
            _runners = new[] { new DeviceRunner(new ScalePanel(1, "10.0.1.18")), new DeviceRunner(new ScalePanel(2, "10.0.1.8")) };
            _stopAll = MakeButton("Остановить всё", async (_, _) => await StopAllAsync());
            BuildLayout();
            bool settingsRead = LoadSettings();
            foreach (DeviceRunner runner in _runners)
            {
                DeviceRunner r = runner;
                r.Panel.PollToggled += (_, _) => OnPollToggled(r);
                r.Panel.ResetRequested += (_, _) => ApplyEvents(r, r.Model.ResetLatch(DateTime.Now));
                r.Panel.DigitsChanged += (_, _) => r.Model.Digits = r.Panel.Digits;
                r.Panel.Render(r.Model.View);
            }
            _period.ValueChanged += (_, _) => Volatile.Write(ref _periodMs, (int)_period.Value);
            _timeout.ValueChanged += (_, _) => Volatile.Write(ref _timeoutMs, (int)_timeout.Value);
            _latch.CheckedChanged += (_, _) =>
            {
                if (_runners.Any(r => r.Running))
                    _status.Text = $"Фиксация ошибок: {(_latch.Checked ? "вкл" : "выкл")} — применится при следующем включении опроса прибора";
            };
            Volatile.Write(ref _periodMs, (int)_period.Value);
            Volatile.Write(ref _timeoutMs, (int)_timeout.Value);
            UpdateRunState();
            if (settingsRead) _status.Text = "Готово · FC03, только чтение · включите «Опрос» у прибора";
            ResumeLayout(true);
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(4) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var toolbar = Flow();
            toolbar.Margin = Padding.Empty; toolbar.Padding = new Padding(0, 2, 0, 2);
            AddField(toolbar, "Период, мс", _period);
            AddField(toolbar, "Таймаут, мс", _timeout);
            toolbar.Controls.Add(_latch);
            _stopAll.Margin = new Padding(12, 3, 3, 3);
            toolbar.Controls.Add(_stopAll);
            _tips.SetToolTip(_period, "Интервал между запросами текущих показаний (чтение 2000…) одного прибора. Инициализация (1000, 1100…) " +
                "выполняется подряд без паузы. Если обмен длится дольше периода, следующий запрос уходит сразу, в журнале — «ПРЕВЫШЕН». Применяется сразу.");
            _tips.SetToolTip(_timeout, "Лимит подключения и каждого обмена целиком (запрос + полный ответ). Применяется сразу, со следующего обмена.");
            _tips.SetToolTip(_latch, "После ошибки: 12 чистых ответов до возврата веса. Три ошибки до восстановления фиксируются до ручного снятия. " +
                "Команды прибору не посылаются. Применяется при следующем включении опроса прибора.");
            _tips.SetToolTip(_stopAll, "Выключить опрос обоих приборов. Последние показания остаются на экране серым.");
            root.Controls.Add(toolbar, 0, 0);

            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cards.Controls.Add(_runners[0].Panel, 0, 0); cards.Controls.Add(_runners[1].Panel, 1, 0);
            _split.Panel1.Controls.Add(cards);

            var journal = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            journal.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            journal.RowStyles.Add(new RowStyle(SizeType.AutoSize)); journal.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var logTools = Flow();
            logTools.Margin = Padding.Empty;
            var title = new Label { Text = "Журнал обмена", AutoSize = true, Margin = new Padding(3, 8, 6, 0) };
            title.Font = new Font(Font, FontStyle.Bold);
            _view.Items.AddRange(new object[] { "Все записи", "HEX: TX / RX", "Расшифровка", "События" }); _view.SelectedIndex = 0;
            _deviceFilter.Items.AddRange(new object[] { "Оба прибора", "Прибор 1", "Прибор 2" }); _deviceFilter.SelectedIndex = 0;
            _view.SelectedIndexChanged += (_, _) => RenderLog(); _deviceFilter.SelectedIndexChanged += (_, _) => RenderLog();
            var clear = MakeButton("Clear · Очистить", (_, _) =>
            {
                _entries.Clear(); _log.Clear();
                foreach (DeviceRunner r in _runners) r.Panel.ClearHistory();
            });
            var copy = MakeButton("Копировать", (_, _) => CopyLog());
            var save = MakeButton("Сохранить…", (_, _) => SaveLog());
            var folder = MakeButton("Папка журналов", (_, _) => OpenLogFolder());
            _tips.SetToolTip(clear, "Очистить экран, буфер отображения и события карточек. Файлы текущей сессии сохраняются без изменений.");
            _tips.SetToolTip(copy, "Копировать выделенный текст; без выделения — весь видимый журнал.");
            _tips.SetToolTip(save, "Экспортировать текущее содержимое экрана. Полные журналы сессий находятся в папке журналов.");
            _hold.CheckedChanged += (_, _) =>
            {
                foreach (DeviceRunner r in _runners) r.Panel.SetHold(_hold.Checked);
                if (!_hold.Checked) RenderLog();
            };
            _tips.SetToolTip(_hold, "Зафиксировать журналы на экране. Опрос и полная запись в файлы продолжаются. При снятии Hold выводятся последние 5000 записей.");
            _tips.SetToolTip(_scroll, "Выключите, чтобы читать предыдущие записи. Запись в файлы и прием пакетов продолжаются.");
            logTools.Controls.AddRange(new Control[] { title, _view, _deviceFilter, clear, copy, save, folder, _hold, _scroll });
            journal.Controls.Add(logTools, 0, 0); journal.Controls.Add(_log, 0, 1);
            _split.Panel2.Controls.Add(journal);
            // Sizes and min sizes of the split are applied only once the window has its real size (ApplySplitter).
            _split.SplitterMoving += (_, _) => _userMovingSplitter = true;
            _split.SplitterMoved += (_, _) =>
            {
                if (!_userMovingSplitter || _applyingSplitter || WindowState == FormWindowState.Minimized) return;
                _userMovingSplitter = false;
                _cardsHeight = (int)Math.Round(_split.SplitterDistance * 96.0 / DeviceDpi);
            };
            _split.SizeChanged += (_, _) =>
            {
                if (_shown && !_applyingSplitter && WindowState != FormWindowState.Minimized) ApplySplitter(_cardsHeight);
            };
            root.Controls.Add(_split, 0, 1);

            var strip = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = true }; strip.Items.Add(_status);
            root.Controls.Add(strip, 0, 2);
            Controls.Add(root);
        }

        internal static FlowLayoutPanel Flow() => new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = true, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(0, 2, 0, 2) };

        /// <summary>A group that is never split when the parent flow wraps.</summary>
        internal static FlowLayoutPanel Group(params Control[] controls)
        {
            var group = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, Margin = new Padding(6, 0, 0, 0) };
            group.Controls.AddRange(controls);
            return group;
        }

        internal static Label FieldLabel(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(4, 6, 4, 0) };

        internal static void AddField(FlowLayoutPanel flow, string label, Control control) => flow.Controls.Add(Group(FieldLabel(label), control));

        internal static Button MakeButton(string text, EventHandler action)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 29), FlatStyle = FlatStyle.System };
            button.Click += action; return button;
        }

        // ───────────── Window: placement, DPI, splitter ─────────────

        protected override void OnLoad(EventArgs e)
        {
            // Bounds are restored here and not in the constructor: they are physical pixels and must not be auto-scaled.
            Rectangle? bounds = RestorableBounds();
            if (bounds.HasValue) StartPosition = FormStartPosition.Manual;
            base.OnLoad(e);
            if (!bounds.HasValue) return;
            Bounds = bounds.Value;
            if (_savedWindow!.Maximized) WindowState = FormWindowState.Maximized;
        }

        private Rectangle? RestorableBounds()
        {
            if (_savedWindow is not { } w) return null;
            Size min = LogicalToDeviceUnits(new Size(720, 480));
            var rect = new Rectangle(w.X, w.Y, Math.Max(w.Width, min.Width), Math.Max(w.Height, min.Height));
            foreach (Screen screen in Screen.AllScreens)
            {
                Rectangle common = Rectangle.Intersect(screen.WorkingArea, rect);
                if (common.Width >= 100 && common.Height >= 50) return rect;
            }
            return null;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitInputWidths(this);
            ApplyMinimumSize();
            if (WindowState == FormWindowState.Normal)
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
            }
            _shown = true;
            ApplySplitter(_cardsHeight);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            FitInputWidths(this);
            ApplyMinimumSize();
            Post(() => ApplySplitter(_cardsHeight));
        }

        private void ApplyMinimumSize()
        {
            Size min = LogicalToDeviceUnits(new Size(720, 480));
            Rectangle area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(min.Width, area.Width), Math.Min(min.Height, area.Height));
        }

        /// <summary>Applies the splitter geometry and the preferred cards height; does nothing when the window is too small.</summary>
        private void ApplySplitter(int cardsHeightLogical)
        {
            if (_applyingSplitter || IsDisposed || !_split.IsHandleCreated) return;
            _applyingSplitter = true;
            try
            {
                int width = LogicalToDeviceUnits(6), min1 = LogicalToDeviceUnits(180), min2 = LogicalToDeviceUnits(120);
                int max = _split.Height - min2 - width;
                if (max < min1) return;
                if (_split.SplitterWidth != width) _split.SplitterWidth = width;
                _split.Panel1MinSize = min1;
                _split.Panel2MinSize = min2;
                int distance = Math.Clamp(LogicalToDeviceUnits(cardsHeightLogical), min1, max);
                if (_split.SplitterDistance != distance) _split.SplitterDistance = distance;
            }
            catch (InvalidOperationException) { }
            catch (ArgumentOutOfRangeException) { }
            finally { _applyingSplitter = false; }
        }

        private static void FitInputWidths(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is Button button)
                {
                    button.AutoSize = false; button.MinimumSize = Size.Empty;
                    button.Size = new Size(TextRenderer.MeasureText(button.Text, button.Font).Width + 24, button.Font.Height + 12);
                    button.TextAlign = ContentAlignment.MiddleCenter;
                    button.Padding = Padding.Empty; button.Margin = new Padding(3);
                }
                if (control is NumericUpDown number)
                    number.Width = TextRenderer.MeasureText(number.Maximum.ToString(CultureInfo.InvariantCulture), number.Font).Width +
                        SystemInformation.VerticalScrollBarWidth + TextRenderer.MeasureText("00", number.Font).Width;
                else if (control is ComboBox combo)
                {
                    int textWidth = 0;
                    foreach (object item in combo.Items) textWidth = Math.Max(textWidth, TextRenderer.MeasureText(item.ToString(), combo.Font).Width);
                    combo.Width = textWidth + SystemInformation.VerticalScrollBarWidth + TextRenderer.MeasureText("00", combo.Font).Width;
                    combo.DropDownWidth = combo.Width;
                }
                else if (control is TextBox text)
                    text.Width = Math.Max(text.Width, TextRenderer.MeasureText("255.255.255.255", text.Font).Width + 12);
                FitInputWidths(control);
                // After the recursion: the latch button height is fitted to the state row.
                if (control is ScalePanel scalePanel) scalePanel.CompactRows();
            }
        }

        // ───────────── Polling lifecycle ─────────────

        private void OnPollToggled(DeviceRunner r)
        {
            if (_suppressToggle || _closing) return;
            if (r.Panel.PollChecked) StartDevice(r);
            else _ = StopFromUiAsync(r, "оператором");
        }

        private void SetChecked(DeviceRunner r, bool value)
        {
            _suppressToggle = true;
            try { r.Panel.PollChecked = value; }
            finally { _suppressToggle = false; }
        }

        private void StartDevice(DeviceRunner r)
        {
            if (r.Running) return;
            ScalePanel panel = r.Panel;
            if (!panel.ValidateSettings()) { SetChecked(r, false); return; }
            DeviceRunner other = _runners[2 - r.Number];
            if (other.Model.Phase != CardPhase.Off && other.Panel.ConnectionKey == panel.ConnectionKey)
            {
                MessageBox.Show(this, $"Прибор {other.Number} уже опрашивает {M10ModbusClient.FormatEndpoint(panel.IpAddress, panel.Port)}, " +
                    $"Id {panel.UnitId}, весы {panel.ScaleNumber}. Измените IP, порт, Id или номер весов.",
                    "Одинаковые подключения", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetChecked(r, false);
                return;
            }
            SaveSettings();
            EnsureJournal();
            DeviceSettings settings = panel.GetSettings();
            panel.LockSettings(true);
            r.Generation++;
            ApplyEvents(r, r.Model.Start(settings, _latch.Checked, DateTime.Now, (int)_period.Value, (int)_timeout.Value));
            r.TimerRes = TimerResolution.Acquire();
            r.Cts = new CancellationTokenSource();
            int device = r.Number, generation = r.Generation;
            CancellationToken token = r.Cts.Token;
            r.Worker = Task.Run(() => RunDeviceAsync(device, settings, generation, token));
            _ = ObserveWorkerAsync(r, r.Worker);
            UpdateRunState();
        }

        private async Task StopFromUiAsync(DeviceRunner r, string reason)
        {
            try { await StopDeviceAsync(r, reason); }
            catch (Exception ex) { _status.Text = $"Ошибка остановки прибора {r.Number}: {ex.Message}"; }
        }

        /// <summary>Idempotent: a repeated call while stopping returns the same task.</summary>
        private Task StopDeviceAsync(DeviceRunner r, string reason)
        {
            if (r.IsStopping) return r.Stopping!;
            if (!r.Running) return Task.CompletedTask;
            r.Stopping = StopCoreAsync(r, reason);
            return r.Stopping;
        }

        private async Task StopCoreAsync(DeviceRunner r, string reason)
        {
            r.Panel.SetToggleEnabled(false);
            CancellationTokenSource cts = r.Cts!;
            Task? worker = r.Worker;
            UpdateRunState();
            // CancelAsync: cancellation callbacks of the worker must not run synchronously on the UI thread.
            await cts.CancelAsync();
            if (worker != null)
            {
                try { await worker; }
                catch (Exception) { /* A worker failure is reported by ObserveWorkerAsync; stopping continues. */ }
            }
            cts.Dispose();
            r.TimerRes?.Dispose();
            r.Cts = null; r.Worker = null; r.TimerRes = null;
            r.Generation++;
            ApplyEvents(r, r.Model.Stop(DateTime.Now, reason));
            r.Panel.LockSettings(false);
            r.Panel.SetToggleEnabled(!_closing);
            UpdateRunState();
            if (!_runners.Any(x => x.Running))
            {
                Task closing = CloseJournalAsync();
                _journalClosed = _journalClosed.IsCompleted ? closing : Task.WhenAll(_journalClosed, closing);
            }
        }

        private async Task StopAllAsync()
        {
            DeviceRunner[] running = _runners.Where(r => r.Running).ToArray();
            foreach (DeviceRunner r in running) SetChecked(r, false);
            await Task.WhenAll(running.Select(r => StopFromUiAsync(r, "оператором")));
        }

        private async Task ObserveWorkerAsync(DeviceRunner r, Task worker)
        {
            try { await worker; }
            catch (Exception ex)
            {
                if (r.Worker != worker || r.IsStopping || _closing) return;
                SetChecked(r, false);
                await StopFromUiAsync(r, "из-за ошибки программы: " + ex.Message);
                await _journalClosed;
                _status.Text = $"Ошибка опроса прибора {r.Number}: {ex.Message}";
            }
        }

        /// <summary>Runs on the thread pool; touches the UI only through <see cref="Post"/>.</summary>
        private async Task RunDeviceAsync(int device, DeviceSettings s, int generation, CancellationToken ct)
        {
            using var client = new M10ModbusClient(s.Ip.Trim(), s.Port, (byte)s.UnitId, s.Scale, s.ReadUnrounded);
            client.Trace += trace => Post(() => AddTrace(device, trace));
            var scheduler = new PollScheduler(Stopwatch.GetTimestamp, Stopwatch.Frequency, Task.Delay);
            int failures = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    client.TimeoutMs = Volatile.Read(ref _timeoutMs);
                    int period = Volatile.Read(ref _periodMs);
                    scheduler.Period = TimeSpan.FromMilliseconds(period);
                    PollSlotPlan plan = scheduler.NextSlot();
                    await scheduler.WaitAsync(plan, ct);
                    M10PollResult result = await client.PollAsync(new PollSlot(period, plan.Overrun), ct);
                    ct.ThrowIfCancellationRequested();
                    Post(() => ApplyResult(device, generation, result));
                    if (result.IsConnected)
                    {
                        failures = 0;
                        scheduler.Commit(plan, result.PollTxTicks, result.Reinitialized);
                    }
                    else
                    {
                        failures++;
                        scheduler.Reset();
                        // Bounded reconnect backoff, independently for each instrument.
                        await Task.Delay(Math.Max(period, Math.Min(5000, 500 * Math.Min(failures, 10))), ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        private void ApplyResult(int device, int generation, M10PollResult result)
        {
            DeviceRunner r = _runners[device - 1];
            if (generation != r.Generation || r.Model.Phase == CardPhase.Off) return;
            (string text, bool isError) = TraceFormatter.FormatData(result, device);
            AddLog(device, "DATA", text, isError ? Color.Firebrick : Color.DimGray);
            ApplyEvents(r, r.Model.Apply(result));
        }

        /// <summary>Writes model events to the card history and the journal, then renders the card.</summary>
        private void ApplyEvents(DeviceRunner r, IReadOnlyList<CardEvent> events)
        {
            foreach (CardEvent e in events)
            {
                r.Panel.AddEvent(e);
                AddLog(r.Number, "EVENT", TraceFormatter.FormatEvent(e.Time, r.Number, r.Model.Endpoint, e.Text), ToneColor(e.Tone));
            }
            r.Panel.Render(r.Model.View);
        }

        private void AddTrace(int device, ModbusTrace trace)
        {
            (string text, TraceTone tone) = TraceFormatter.Format(trace, device);
            Color color = tone switch
            {
                TraceTone.Tx => TxColor,
                TraceTone.Rx => RxColor,
                TraceTone.Warning => Color.DarkOrange,
                _ => Color.Firebrick
            };
            AddLog(device, "HEX", text, color);
        }

        internal static Color ToneColor(Tone tone) => tone switch
        {
            Tone.Good => Color.SeaGreen,
            Tone.Bad => Color.Firebrick,
            Tone.Warn => Color.DarkOrange,
            Tone.Info => Color.SteelBlue,
            Tone.Stale => Color.DimGray,
            _ => Color.SlateGray
        };

        private void UpdateRunState()
        {
            _stopAll.Enabled = !_closing && _runners.Any(r => r.Running && !r.IsStopping);
            DeviceRunner[] running = _runners.Where(r => r.Running).ToArray();
            if (running.Length == 0) return;
            string devices = "Опрос: " + string.Join(", ", running.Select(r => "прибор " + r.Number));
            _status.Text = _journal?.Error != null ? devices + " · ошибка записи журнала: " + _journal.Error
                : _journal != null ? devices + " · журнал: " + _sessionDirectory
                : devices + " · журнал не ведётся: " + _journalProblem;
        }

        private void Post(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        // ───────────── Session journal files ─────────────

        private void EnsureJournal()
        {
            if (_journal != null) return;
            DateTime now = DateTime.Now;
            string directory = Path.Combine(_dataRoot, "logs", now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _journalProblem = ex.Message;
                AddLog(0, "EVENT", TraceFormatter.FormatSystem(now, "Не удалось создать папку журнала: " + ex.Message), Color.Firebrick);
                return;
            }
            _journalProblem = null;
            var journal = new SessionJournal();
            _journal = journal;
            _sessionDirectory = directory;
            journal.Start(directory, error =>
            {
                DateTime at = DateTime.Now;
                Post(() =>
                {
                    // A late error of an already closed session must not be attributed to the current one.
                    if (!ReferenceEquals(_journal, journal)) return;
                    _status.Text = "Не удалось записать журнал: " + error;
                    AddLog(0, "EVENT", TraceFormatter.FormatSystem(at, "Ошибка записи журнала: " + error), Color.Firebrick);
                });
            });
            AddLog(0, "EVENT", TraceFormatter.SessionOpened(now, directory), Color.SlateGray);
            AddLog(0, "EVENT", TraceFormatter.LegendLine(now), Color.SlateGray);
        }

        private async Task CloseJournalAsync()
        {
            SessionJournal? journal = _journal;
            if (journal == null) { _status.Text = "Остановлено"; return; }
            AddLog(0, "EVENT", TraceFormatter.SessionClosed(DateTime.Now), Color.SlateGray);
            _journal = null;
            string directory = _sessionDirectory;
            string text;
            try
            {
                await journal.DisposeAsync();
                text = journal.Error == null ? "Остановлено · журнал: " + directory : "Ошибка записи журнала: " + journal.Error;
            }
            catch (Exception ex) { text = "Ошибка закрытия журнала: " + ex.Message; }
            if (!_runners.Any(r => r.Running)) _status.Text = text;
        }

        // ───────────── Journal view ─────────────

        private void AddLog(int device, string kind, string text, Color color)
        {
            var entry = new LogEntry(device, kind, text, color);
            _journal?.Write("traffic.txt", text);
            if (device > 0 && kind == "EVENT") _journal?.Write($"info_device{device}.txt", text);
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries) _entries.Dequeue();
            if (!_hold.Checked && Matches(entry)) AppendEntry(entry);
        }

        private static Color LogInk(Color color) => color == Color.Firebrick ? Color.Salmon
            : color == Color.DarkOrange ? Color.Orange
            : color == Color.SlateGray || color == Color.DimGray ? Color.Gainsboro
            : Color.FromArgb(Math.Max(110, (int)color.R), Math.Max(150, (int)color.G), Math.Max(110, (int)color.B));

        private bool Matches(LogEntry entry) => (_deviceFilter.SelectedIndex == 0 || entry.Device == _deviceFilter.SelectedIndex || entry.Device == 0) &&
            (_view.SelectedIndex == 0 || entry.Kind == (_view.SelectedIndex == 1 ? "HEX" : _view.SelectedIndex == 2 ? "DATA" : "EVENT"));

        private void AppendEntry(LogEntry entry)
        {
            // Preserve selection/scroll while inspecting history. ScrollToCaret only on explicit autoscroll.
            int firstLine = (int)SendMessage(_log.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero);
            int selection = _log.SelectionStart, length = _log.SelectionLength;
            _log.Select(_log.TextLength, 0); _log.SelectionColor = LogInk(entry.Color);
            _log.AppendText(entry.Text + Environment.NewLine);
            if (_log.TextLength > 400000)
            {
                string visible = _log.Text;
                int cut = visible.IndexOf('\n', 100000) + 1;
                int removedLines = visible.Take(cut).Count(c => c == '\n');
                _log.Select(0, cut); _log.SelectedText = "";
                selection = Math.Max(0, selection - cut);
                length = Math.Min(length, _log.TextLength - selection);
                firstLine = Math.Max(0, firstLine - removedLines);
            }
            if (_scroll.Checked) NativeLogScroll.ToBottom(_log);
            else
            {
                _log.Select(selection, length);
                int now = (int)SendMessage(_log.Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero);
                SendMessage(_log.Handle, 0x00B6, IntPtr.Zero, (IntPtr)(firstLine - now));
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);

        private void RenderLog()
        {
            if (_hold.Checked) return;
            _log.Clear();
            // Add one block per entry to keep packet pairs readable.
            foreach (var entry in _entries.Where(Matches))
            {
                _log.Select(_log.TextLength, 0); _log.SelectionColor = LogInk(entry.Color); _log.AppendText(entry.Text + Environment.NewLine);
            }
            if (_scroll.Checked) NativeLogScroll.ToBottom(_log);
        }

        private void CopyLog()
        {
            try { string text = _log.SelectionLength > 0 ? _log.SelectedText : _log.Text; if (text.Length > 0) Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "Буфер обмена занят. Повторите копирование."; }
        }

        private void SaveLog()
        {
            using var dialog = new SaveFileDialog { Filter = "Текстовый журнал (*.txt)|*.txt", FileName = $"M10_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dialog.FileName, _log.Text, new UTF8Encoding(false)); _status.Text = "Экспорт сохранен: " + dialog.FileName; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _status.Text = "Не удалось сохранить экспорт: " + ex.Message; }
        }

        private void OpenLogFolder()
        {
            try
            {
                string folder = _sessionDirectory.Length > 0 ? _sessionDirectory : Path.Combine(_dataRoot, "logs");
                Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) { _status.Text = "Не удалось открыть папку: " + ex.Message; }
        }

        // ───────────── Settings ─────────────

        /// <returns>false when the file exists but could not be read (the warning is shown in the status bar).</returns>
        private bool LoadSettings()
        {
            AppSettings? settings;
            try { settings = AppSettings.Load(SettingsFile); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                _status.Text = "Настройки не прочитаны; использованы значения по умолчанию: " + ex.Message;
                return false;
            }
            if (settings == null) return true;
            // SecondEnabled is intentionally ignored: polling never starts without the operator.
            _runners[0].Panel.ApplySettings(settings.First); _runners[1].Panel.ApplySettings(settings.Second);
            _latch.Checked = settings.Latch;
            _period.Value = Math.Clamp(settings.PeriodMs, (int)_period.Minimum, (int)_period.Maximum);
            _timeout.Value = Math.Clamp(settings.TimeoutMs, (int)_timeout.Minimum, (int)_timeout.Maximum);
            _cardsHeight = settings.CardsHeight > 0 ? settings.CardsHeight : AppSettings.DefaultCardsHeight;
            _view.SelectedIndex = Math.Clamp(settings.LogView, 0, _view.Items.Count - 1);
            _deviceFilter.SelectedIndex = Math.Clamp(settings.LogDevice, 0, _deviceFilter.Items.Count - 1);
            _savedWindow = settings.Window is { Width: > 0, Height: > 0 } ? settings.Window : null;
            return true;
        }

        private void SaveSettings(bool? secondEnabled = null)
        {
            var settings = new AppSettings
            {
                First = _runners[0].Panel.GetSettings(),
                Second = _runners[1].Panel.GetSettings(),
                SecondEnabled = secondEnabled ?? _runners[1].Panel.PollChecked,
                Latch = _latch.Checked,
                PeriodMs = (int)_period.Value,
                TimeoutMs = (int)_timeout.Value,
                Window = CaptureWindow(),
                CardsHeight = _cardsHeight,
                LogView = _view.SelectedIndex,
                LogDevice = _deviceFilter.SelectedIndex
            };
            try { settings.Save(SettingsFile); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _status.Text = "Настройки не сохранены: " + ex.Message; }
        }

        private WindowSettings? CaptureWindow()
        {
            if (!IsHandleCreated) return _savedWindow;
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            return new WindowSettings { X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, Maximized = WindowState == FormWindowState.Maximized };
        }

        // ───────────── Closing ─────────────

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            bool secondEnabled = _runners[1].Panel.PollChecked;
            _closing = true;
            _stopAll.Enabled = false;
            foreach (DeviceRunner r in _runners) r.Panel.SetToggleEnabled(false);
            try
            {
                await Task.WhenAll(_runners.Select(r => StopDeviceAsync(r, "при закрытии программы")));
                await _journalClosed;
            }
            catch (Exception) { /* Stop errors must not block closing. */ }
            SaveSettings(secondEnabled);
            // Leave the FormClosing handler before closing again.
            await Task.Yield();
            _allowClose = true;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (DeviceRunner r in _runners)
                {
                    try { r.Cts?.Cancel(); } catch (ObjectDisposedException) { }
                    r.TimerRes?.Dispose(); r.TimerRes = null;
                }
                _tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public sealed class ScalePanel : GroupBox
    {
        private readonly CheckBox _poll = new CheckBox { Text = "Опрос", AutoSize = true, Margin = new Padding(3, 5, 6, 0) };
        private readonly TextBox _ip;
        private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 502, Width = 72 };
        private readonly NumericUpDown _uid = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 1, Width = 55 };
        private readonly ComboBox _scale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 56 };
        private readonly CheckBox _raw = new CheckBox { Text = "Без округления", AutoSize = true, Margin = new Padding(12, 5, 4, 0) };
        private readonly ComboBox _digits = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64 };
        private readonly Label _status = Caption();
        private readonly Label _weight = new Label { Text = "—", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true, BorderStyle = BorderStyle.Fixed3D, Margin = new Padding(0, 2, 0, 2) };
        private readonly Label _state = Caption();
        private readonly Button _reset;
        private readonly RichTextBox _history = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(250, 250, 245), DetectUrls = false, Font = new Font("Segoe UI", 9F), MinimumSize = Size.Empty };
        private readonly TableLayoutPanel _layout;
        private readonly TableLayoutPanel _stateRow;
        private readonly Font _weightFont = new Font("Consolas", 34F, FontStyle.Bold);
        private readonly Font _messageFont = new Font("Segoe UI", 21F, FontStyle.Bold);
        private readonly ToolTip _tips = new ToolTip();
        private readonly Queue<CardEvent> _eventBuffer = new Queue<CardEvent>();
        private bool _historyHold;

        public event EventHandler? PollToggled;
        public event EventHandler? ResetRequested;
        public event EventHandler? DigitsChanged;

        public int DeviceNumber { get; }
        public string IpAddress => _ip.Text.Trim();
        public int Port => (int)_port.Value;
        public byte UnitId => (byte)_uid.Value;
        public int ScaleNumber => _scale.SelectedIndex + 1;
        /// <summary>Digits after the decimal point; −1 = auto.</summary>
        public int Digits => _digits.SelectedIndex - 1;
        public string ConnectionKey => $"{IpAddress}:{Port}/{UnitId}/{ScaleNumber}";
        public bool PollChecked { get => _poll.Checked; set => _poll.Checked = value; }

        private static Label Caption() => new Label { Dock = DockStyle.Fill, AutoEllipsis = true, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 4, 0), Margin = Padding.Empty };

        public ScalePanel(int number, string ip)
        {
            DeviceNumber = number;
            Text = "Прибор " + number;
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Control; Padding = new Padding(6); Margin = new Padding(3);
            _ip = new TextBox { Text = ip, Width = 120 };
            _scale.Items.AddRange(new object[] { "1", "2", "3" }); _scale.SelectedIndex = 0;
            _digits.Items.Add("Авто"); for (int i = 0; i <= 8; i++) _digits.Items.Add(i.ToString(CultureInfo.InvariantCulture)); _digits.SelectedIndex = 0;

            _poll.Font = new Font(Font, FontStyle.Bold);
            var settings = MainForm.Flow();
            settings.Margin = Padding.Empty; settings.Padding = Padding.Empty;
            var colon = new Label { Text = ":", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            var uidLabel = MainForm.FieldLabel("Id");
            var scaleLabel = MainForm.FieldLabel("Весы");
            var digitsLabel = MainForm.FieldLabel("Знаков");
            settings.Controls.Add(_poll);
            settings.Controls.Add(MainForm.Group(MainForm.FieldLabel("IP"), _ip, colon, _port));
            settings.Controls.Add(MainForm.Group(uidLabel, _uid));
            settings.Controls.Add(MainForm.Group(scaleLabel, _scale));
            settings.Controls.Add(_raw);
            settings.Controls.Add(MainForm.Group(digitsLabel, _digits));

            _tips.SetToolTip(_poll, "Включить/выключить опрос этого прибора. Второй прибор не затрагивается. " +
                "Пока опрос включён, IP, порт, Id, весы и «Без округления» не редактируются.");
            string uidTip = "Unit Identifier — адрес устройства в запросе Modbus TCP (7-й байт MBAP), та же роль, что адрес в Modbus RTU. " +
                "Должен совпадать с настройкой прибора.";
            _tips.SetToolTip(uidLabel, uidTip); _tips.SetToolTip(_uid, uidTip);
            string scaleTip = "Номер весов 1–3: текущие показания читаются с адреса 2000+33·(n−1), конфигурация — 1100+33·(n−1).";
            _tips.SetToolTip(scaleLabel, scaleTip); _tips.SetToolTip(_scale, scaleTip);
            _tips.SetToolTip(_raw, "Читать 15 регистров вместо 13: дополнительно брутто без округления (2013+33·(n−1)).");
            string digitsTip = "Число знаков после запятой на экране. Применяется сразу.";
            _tips.SetToolTip(digitsLabel, digitsTip); _tips.SetToolTip(_digits, digitsTip);

            _poll.CheckedChanged += (_, _) => PollToggled?.Invoke(this, EventArgs.Empty);
            _raw.CheckedChanged += (_, _) => _digits.Enabled = !_raw.Checked;
            _digits.SelectedIndexChanged += (_, _) => DigitsChanged?.Invoke(this, EventArgs.Empty);

            _status.Font = new Font(Font, FontStyle.Bold);
            _status.TextAlign = ContentAlignment.MiddleCenter;
            _reset = MainForm.MakeButton("Снять фиксацию ошибки", (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty));
            _reset.Visible = false;
            _tips.SetToolTip(_reset, "Только в программе: снимает фиксацию ошибки веса и запускает проверку — вес будет показан после 12 ответов " +
                "подряд без ошибок. Прибору ничего не отправляется.");
            _stateRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            _stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _stateRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _stateRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _stateRow.Controls.Add(_state, 0, 0); _stateRow.Controls.Add(_reset, 1, 0);

            _weight.Font = _weightFont;
            _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height + 8));
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, _weightFont.Height + 16));
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height + 10));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _layout.Controls.Add(settings, 0, 0);
            _layout.Controls.Add(_status, 0, 1);
            _layout.Controls.Add(_weight, 0, 2);
            _layout.Controls.Add(_stateRow, 0, 3);
            _layout.Controls.Add(_history, 0, 4);
            Controls.Add(_layout);
        }

        internal void CompactRows()
        {
            int stateRow = Font.Height + 10;
            _layout.RowStyles[1].Height = Font.Height + 8;
            _layout.RowStyles[2].Height = _weightFont.Height + 16;
            _layout.RowStyles[3].Height = stateRow;
            _reset.AutoSize = false;
            _reset.MinimumSize = Size.Empty;
            _reset.Margin = new Padding(3, 1, 0, 1);
            _reset.Size = new Size(TextRenderer.MeasureText(_reset.Text, _reset.Font).Width + 16, stateRow - 2);
        }

        public bool ValidateSettings()
        {
            if (IPAddress.TryParse(IpAddress, out _)) return true;
            MessageBox.Show(FindForm(), $"Прибор {DeviceNumber}: введите корректный IP-адрес.", "Настройки подключения", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _ip.Focus(); _ip.SelectAll(); return false;
        }

        /// <summary>Locks the connection settings; «Опрос» and «Знаков» stay available.</summary>
        public void LockSettings(bool locked)
        {
            _ip.Enabled = _port.Enabled = _uid.Enabled = _scale.Enabled = _raw.Enabled = !locked;
        }

        public void SetToggleEnabled(bool enabled) => _poll.Enabled = enabled;

        public void Render(CardView v)
        {
            (_status.BackColor, _status.ForeColor) = v.StatusTone switch
            {
                Tone.Info => (Color.LightSteelBlue, Color.Black),
                Tone.Good => (Color.ForestGreen, Color.White),
                Tone.Bad => (Color.Crimson, Color.White),
                Tone.Warn => (Color.Orange, Color.Black),
                _ => (Color.LightGray, Color.Black)
            };
            _status.Text = v.StatusText;
            _tips.SetToolTip(_status, v.StatusTip.Length > 0 ? v.StatusText + Environment.NewLine + v.StatusTip : v.StatusText);

            Font weightFont = v.WeightIsMessage ? _messageFont : _weightFont;
            if (_weight.Font != weightFont) _weight.Font = weightFont;
            (_weight.BackColor, _weight.ForeColor) = v.WeightTone switch
            {
                Tone.Stale => (Color.FromArgb(48, 48, 48), Color.FromArgb(150, 150, 150)),
                Tone.Good => (Color.Black, Color.Lime),
                Tone.Warn => (Color.Black, v.WeightIsMessage ? Color.DarkOrange : Color.Yellow),
                Tone.Bad => (Color.Black, Color.OrangeRed),
                _ => (Color.Black, Color.Gainsboro)
            };
            _weight.Text = v.WeightText;

            (_state.BackColor, _state.ForeColor) = v.StateTone switch
            {
                Tone.Good => (Color.PaleGreen, Color.Black),
                Tone.Warn => (Color.LightGoldenrodYellow, Color.Black),
                Tone.Bad => (Color.MistyRose, Color.Firebrick),
                _ => (SystemColors.Control, Color.DimGray)
            };
            _state.Text = v.StateText;
            _tips.SetToolTip(_state, v.StateText);
            _reset.Visible = v.ShowLatchReset;
        }

        public void AddEvent(CardEvent e)
        {
            _eventBuffer.Enqueue(e); while (_eventBuffer.Count > 200) _eventBuffer.Dequeue();
            if (!_historyHold) AppendHistory(e);
        }

        private void AppendHistory(CardEvent e)
        {
            if (_history.TextLength > 20000) { _history.Select(0, 5000); _history.SelectedText = ""; }
            _history.Select(_history.TextLength, 0); _history.SelectionColor = MainForm.ToneColor(e.Tone);
            _history.AppendText(e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + e.Text + Environment.NewLine);
            NativeLogScroll.ToBottom(_history);
        }

        public void ClearHistory() { _eventBuffer.Clear(); _history.Clear(); }

        public void SetHold(bool hold)
        {
            _historyHold = hold;
            if (hold) return;
            _history.Clear(); foreach (CardEvent e in _eventBuffer) AppendHistory(e);
        }

        public DeviceSettings GetSettings() => new DeviceSettings { Ip = IpAddress, Port = Port, UnitId = UnitId, Scale = ScaleNumber,
            ReadUnrounded = _raw.Checked, Digits = _digits.SelectedIndex == 0 ? null : _digits.SelectedIndex - 1,
            UnroundedAddress = (2013 + (ScaleNumber - 1) * 33).ToString(CultureInfo.InvariantCulture) };

        public void ApplySettings(DeviceSettings? settings)
        {
            if (settings == null) return;
            _ip.Text = settings.Ip ?? ""; _port.Value = Math.Clamp(settings.Port, 1, 65535);
            _uid.Value = Math.Clamp(settings.UnitId, 0, 255); _scale.SelectedIndex = Math.Clamp(settings.Scale, 1, 3) - 1;
            _raw.Checked = settings.ReadUnrounded; _digits.SelectedIndex = settings.Digits.HasValue ? Math.Clamp(settings.Digits.Value, 0, 8) + 1 : 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tips.Dispose(); _weightFont.Dispose(); _messageFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static class NativeLogScroll
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);

        public static void ToBottom(RichTextBox box)
        {
            if (!box.IsHandleCreated || box.IsDisposed) return;
            SendMessage(box.Handle, 0x0115, (IntPtr)7, IntPtr.Zero); // WM_VSCROLL, SB_BOTTOM; no caret/focus change.
        }
    }
}
