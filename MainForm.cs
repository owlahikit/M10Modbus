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
        internal static readonly Color PanelGrey = Color.FromArgb(0xAC, 0xAC, 0xAC);
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
        private sealed record LogEntry(int Device, LogKind Kind, string Text, Color Color);

        public MainForm()
        {
            SuspendLayout();
            Text = "M10 · Монитор Modbus TCP v4.5";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1200, 850);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = PanelGrey;
            _runners = new[] { new DeviceRunner(new ScalePanel(1, "10.0.1.18")), new DeviceRunner(new ScalePanel(2, "10.0.1.8")) };
            _stopAll = MakeButton("Остановить всё", async (_, _) => await StopAllAsync());
            BuildLayout();
            bool settingsRead = LoadSettings();
            foreach (DeviceRunner runner in _runners)
            {
                DeviceRunner r = runner;
                r.Panel.PollToggled += (_, _) => OnPollToggled(r);
                r.Panel.ResetRequested += (_, _) => ApplyEvents(r, r.Model.ResetLatch(DateTime.Now));
                r.Panel.Render(r.Model.View);
            }
            _period.ValueChanged += (_, _) => Volatile.Write(ref _periodMs, (int)_period.Value);
            _timeout.ValueChanged += (_, _) => Volatile.Write(ref _timeoutMs, (int)_timeout.Value);
            _latch.CheckedChanged += (_, _) =>
            {
                foreach (DeviceRunner r in _runners)
                    ApplyEvents(r, r.Model.SetLatchEnabled(_latch.Checked, DateTime.Now));
            };
            Volatile.Write(ref _periodMs, (int)_period.Value);
            Volatile.Write(ref _timeoutMs, (int)_timeout.Value);
            UpdateRunState();
            if (settingsRead) _status.Text = "Готово · FC03, только чтение · включите «Опрос» у прибора";
            ResumeLayout(true);
        }

        private void BuildLayout()
        {
            _split.BackColor = Color.White;
            _split.Panel1.BackColor = _split.Panel2.BackColor = PanelGrey;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(4), BackColor = PanelGrey };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var toolbar = Flow();
            toolbar.Margin = Padding.Empty; toolbar.Padding = new Padding(0, 2, 0, 2);
            AddField(toolbar, "Период, мс", _period);
            AddField(toolbar, "Таймаут, мс", _timeout);
            toolbar.Controls.Add(Group(_latch));
            _stopAll.Margin = new Padding(12, 3, 3, 3);
            toolbar.Controls.Add(Group(_stopAll));
            _tips.SetToolTip(_period, "Интервал между запросами текущих показаний (чтение 2000…) одного прибора. Инициализация (1000, 1100…) " +
                "выполняется подряд без паузы. Если обмен длится дольше периода, следующий запрос уходит сразу, в журнале — «ПРЕВЫШЕН». Применяется сразу.");
            _tips.SetToolTip(_timeout, "Лимит подключения и каждого обмена целиком (запрос + полный ответ). Применяется сразу, со следующего обмена.");
            _tips.SetToolTip(_latch, "После ошибки: 12 чистых ответов до возврата веса. Три ошибки до восстановления фиксируются до ручного снятия. " +
                "При выключении фиксация снимается сразу. Команды прибору не посылаются.");
            _tips.SetToolTip(_stopAll, "Выключить опрос обоих приборов. Последние показания остаются на экране серым.");
            root.Controls.Add(toolbar, 0, 0);

            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = PanelGrey };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cards.Controls.Add(_runners[0].Panel, 0, 0); cards.Controls.Add(_runners[1].Panel, 1, 0);
            _split.Panel1.Controls.Add(cards);

            var journal = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty, BackColor = PanelGrey };
            journal.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            journal.RowStyles.Add(new RowStyle(SizeType.AutoSize)); journal.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var logTools = Flow();
            logTools.Margin = Padding.Empty;
            var title = new Label { Text = "Журнал обмена", AutoSize = true, Margin = new Padding(3, 8, 6, 0) };
            title.Font = new Font(Font, FontStyle.Bold);
            _view.Items.AddRange(new object[] { "Все записи", "HEX: TX / RX", "Расшифровка", "События", "Показания", "Инициализация" }); _view.SelectedIndex = 0;
            _tips.SetToolTip(_view, "Расшифровка — показания и настройки; Показания — только числовые ответы весов; " +
                "Инициализация — запросы, ответы HEX и расшифровка идентификации/конфигурации.");
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
            _scroll.CheckedChanged += (_, _) =>
            {
                if (_scroll.Checked) NativeLogScroll.ToBottom(_log);
            };
            _tips.SetToolTip(_scroll, "Выключите, чтобы читать предыдущие записи. Опрос и запись в файлы продолжаются.");
            foreach (Control control in new Control[] { title, _view, _deviceFilter, clear, copy, save, folder, _scroll })
                logTools.Controls.Add(Group(control));
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

            var strip = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = true, BackColor = PanelGrey }; strip.Items.Add(_status);
            root.Controls.Add(strip, 0, 2);
            Controls.Add(root);
        }

        internal static FlowLayoutPanel Flow() => new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = true, Margin = new Padding(0, 0, 0, 4), Padding = new Padding(0, 2, 0, 2), BackColor = PanelGrey };

        /// <summary>A group that is never split when the parent flow wraps.</summary>
        internal static TableLayoutPanel Group(params Control[] controls)
        {
            var group = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = controls.Length, RowCount = 1, MinimumSize = new Size(0, 35),
                Margin = new Padding(6, 0, 0, 0), BackColor = PanelGrey };
            group.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < controls.Length; i++)
            {
                group.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                controls[i].Anchor = AnchorStyles.Left;
                controls[i].Margin = new Padding(4, 3, 4, 3);
                group.Controls.Add(controls[i], i, 0);
            }
            return group;
        }

        internal static Label FieldLabel(string text) => new Label { Text = text, AutoSize = true, Margin = new Padding(4, 3, 4, 3) };

        internal static void AddField(FlowLayoutPanel flow, string label, Control control) => flow.Controls.Add(Group(FieldLabel(label), control));

        internal static Button MakeButton(string text, EventHandler action)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 29), FlatStyle = FlatStyle.System };
            button.Click += action; return button;
        }

        internal static void StyleToggleButton(CheckBox button)
        {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.White;
            button.ForeColor = Color.Black;
            button.FlatAppearance.CheckedBackColor = Color.White;
            button.FlatAppearance.MouseOverBackColor = Color.White;
            button.FlatAppearance.MouseDownBackColor = Color.White;
            button.FlatAppearance.BorderColor = button.Checked ? Color.RoyalBlue : Color.Silver;
            button.CheckedChanged += (_, _) =>
                button.FlatAppearance.BorderColor = button.Checked ? Color.RoyalBlue : Color.Silver;
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
                int width = Math.Max(1, (int)Math.Round(3.2 * DeviceDpi / 96.0));
                int min1 = LogicalToDeviceUnits(180), min2 = LogicalToDeviceUnits(120);
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
            int generation = r.Generation;
            CancellationToken token = r.Cts.Token;
            r.Worker = Task.Run(() => RunDeviceAsync(r, settings, generation, token));
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
        private async Task RunDeviceAsync(DeviceRunner runner, DeviceSettings s, int generation, CancellationToken ct)
        {
            int device = runner.Number;
            using var client = new M10ModbusClient(s.Ip.Trim(), s.Port, (byte)s.UnitId, s.Scale);
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
                    if (result.RequiresOperatorAction) break;
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
            AddLog(device, result.TransactionId == 0 ? LogKind.Initialization : LogKind.Readings,
                text, isError ? Color.Firebrick : Color.DimGray);
            ApplyEvents(r, r.Model.Apply(result));
        }

        /// <summary>Writes model events to the card history and the journal, then renders the card.</summary>
        private void ApplyEvents(DeviceRunner r, IReadOnlyList<CardEvent> events)
        {
            foreach (CardEvent e in events)
            {
                r.Panel.AddEvent(e);
                AddLog(r.Number, LogKind.Event, TraceFormatter.FormatEvent(e.Time, r.Number, r.Model.Endpoint, e.Text), ToneColor(e.Tone));
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
            AddLog(device, trace.Phase == TracePhase.Poll ? LogKind.Hex : LogKind.InitializationHex, text, color);
            if (TraceFormatter.FormatInitializationData(trace, device) is { } decoded)
                AddLog(device, LogKind.Initialization, decoded.Text, decoded.IsError ? Color.Firebrick : Color.DimGray);
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
                AddLog(0, LogKind.Event, TraceFormatter.FormatSystem(now, "Не удалось создать папку журнала: " + ex.Message), Color.Firebrick);
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
                    AddLog(0, LogKind.Event, TraceFormatter.FormatSystem(at, "Ошибка записи журнала: " + error), Color.Firebrick);
                });
            });
            AddLog(0, LogKind.Event, TraceFormatter.SessionOpened(now, directory), Color.SlateGray);
            AddLog(0, LogKind.Event, TraceFormatter.LegendLine(now), Color.SlateGray);
        }

        private async Task CloseJournalAsync()
        {
            SessionJournal? journal = _journal;
            if (journal == null) { _status.Text = "Остановлено"; return; }
            AddLog(0, LogKind.Event, TraceFormatter.SessionClosed(DateTime.Now), Color.SlateGray);
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

        private void AddLog(int device, LogKind kind, string text, Color color)
        {
            var entry = new LogEntry(device, kind, text, color);
            _journal?.Write("traffic.txt", text);
            if (device > 0 && kind == LogKind.Event) _journal?.Write($"info_device{device}.txt", text);
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries) _entries.Dequeue();
            if (Matches(entry)) AppendEntry(entry);
        }

        private static Color LogInk(Color color) => color == Color.Firebrick ? Color.Salmon
            : color == Color.DarkOrange ? Color.Orange
            : color == Color.SlateGray || color == Color.DimGray ? Color.Gainsboro
            : Color.FromArgb(Math.Max(110, (int)color.R), Math.Max(150, (int)color.G), Math.Max(110, (int)color.B));

        private bool Matches(LogEntry entry) => (_deviceFilter.SelectedIndex == 0 || entry.Device == _deviceFilter.SelectedIndex || entry.Device == 0) &&
            LogViewFilter.Includes((LogView)_view.SelectedIndex, entry.Kind);

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
                int lineEnd = visible.IndexOf('\n', 100000);
                int cut = lineEnd >= 0 ? lineEnd + 1 : 100000;
                int removedLines = visible.Take(cut).Count(c => c == '\n');
                LogTextBox.RemovePrefix(_log, cut);
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
        private static readonly Color CardGrey = MainForm.PanelGrey;
        private static readonly Color CardGreyDark = Color.FromArgb(0x97, 0x97, 0x97);
        private static readonly Color GridWhite = Color.White;
        private static readonly Color VividBlue = Color.FromArgb(0, 0, 220);
        private static readonly Color DimInk = Color.FromArgb(120, 120, 120);
        private static readonly Color LedGreen = Color.FromArgb(185, 235, 25);
        private static readonly Color LedRed = Color.Red;
        private static readonly Color LedOff = Color.FromArgb(128, 128, 128);

        private sealed class Led : Control
        {
            public Led()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                Size = new Size(11, 11); Margin = new Padding(0, 1, 0, 0);
            }

            private IndicatorState _state;

            public void SetOn(bool on) => SetState(on ? IndicatorState.Green : IndicatorState.Off);

            public void SetState(IndicatorState state) { if (_state != state) { _state = state; Invalidate(); } }

            protected override void OnPaint(PaintEventArgs e)
            {
                using var back = new SolidBrush(BackColor); e.Graphics.FillRectangle(back, ClientRectangle);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using var fill = new SolidBrush(_state switch
                {
                    IndicatorState.Green => LedGreen,
                    IndicatorState.Red => LedRed,
                    _ => LedOff
                });
                using var pen = new Pen(Color.FromArgb(70, 70, 70));
                e.Graphics.FillEllipse(fill, rect); e.Graphics.DrawEllipse(pen, rect);
            }
        }

        private readonly CheckBox _poll = new CheckBox { Text = "Начать опрос", Appearance = Appearance.Button, AutoSize = true,
            MinimumSize = new Size(0, 27), Margin = new Padding(3, 3, 6, 3) };
        private readonly TextBox _ip;
        private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 502, Width = 72 };
        private readonly NumericUpDown _uid = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 1, Width = 55 };
        private readonly ComboBox _scale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 56 };
        private readonly Label _serial = new Label { AutoSize = true, Visible = false, ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 7.5F), Margin = new Padding(12, 8, 4, 0) };
        private readonly Label _status = Caption();
        private readonly Panel _instrumentHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = CardGrey };
        private readonly Label _message = new Label { Text = "—", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true, Margin = Padding.Empty };
        private readonly TableLayoutPanel _readings;
        private readonly Panel _rangesHost = new Panel { Dock = DockStyle.Fill, BackColor = GridWhite, Margin = new Padding(1, 0, 1, 0) };
        private readonly List<(Led Active, Label Number, Label Maximum, Label Minimum, Label Division)> _rangeRows = new();
        private Led? _aboveMaximumLed;
        private readonly Label _netto = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true, BackColor = CardGrey, Margin = new Padding(1, 1, 1, 1), Padding = new Padding(0, 0, 6, 0) };
        private readonly Label _tareValue = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
            BackColor = CardGrey, ForeColor = VividBlue, Margin = Padding.Empty };
        private readonly Label _bruttoValue = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
            BackColor = CardGrey, ForeColor = VividBlue, Margin = Padding.Empty };
        private readonly Label _unitCaption = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true, BackColor = CardGrey, ForeColor = VividBlue, Margin = new Padding(1),
            Padding = new Padding(4, 0, 4, 0) };
        private readonly Label _tareCaption = Caption();
        private readonly Label _bruttoCaption = Caption();
        private readonly Led _stableLed = new Led { BackColor = CardGrey };
        private readonly Led _zeroLed = new Led { BackColor = CardGrey };
        private readonly Label _state = Caption();
        private readonly Button _reset;
        private readonly RichTextBox _history = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(250, 250, 245), DetectUrls = false, Font = new Font("Segoe UI", 9F), MinimumSize = Size.Empty };
        private readonly TableLayoutPanel _layout;
        private readonly TableLayoutPanel _stateRow;
        private readonly Font _weightFont = new Font("Segoe UI", 32F, FontStyle.Regular);
        private readonly Font _messageFont = new Font("Segoe UI", 19F, FontStyle.Regular);
        private readonly Font _subValueFont = new Font("Segoe UI", 20F, FontStyle.Regular);
        private readonly Font _captionFont = new Font("Segoe UI", 9F, FontStyle.Regular);
        private readonly Font _rangeFont = new Font("Segoe UI", 9F, FontStyle.Regular);
        private readonly ToolTip _tips = new ToolTip();

        public event EventHandler? PollToggled;
        public event EventHandler? ResetRequested;

        public int DeviceNumber { get; }
        public string IpAddress => _ip.Text.Trim();
        public int Port => (int)_port.Value;
        public byte UnitId => (byte)_uid.Value;
        public int ScaleNumber => _scale.SelectedIndex + 1;
        public string ConnectionKey => $"{IpAddress}:{Port}/{UnitId}/{ScaleNumber}";
        public bool PollChecked { get => _poll.Checked; set => _poll.Checked = value; }

        private static Label Caption() => new Label { Dock = DockStyle.Fill, AutoEllipsis = true, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 4, 0), Margin = Padding.Empty };

        public ScalePanel(int number, string ip)
        {
            DeviceNumber = number;
            MainForm.StyleToggleButton(_poll);
            Text = "Прибор " + number;
            Dock = DockStyle.Fill;
            BackColor = CardGrey; Padding = new Padding(6); Margin = new Padding(3);
            _ip = new TextBox { Text = ip, Width = 120 };
            _scale.Items.AddRange(new object[] { "1", "2", "3" }); _scale.SelectedIndex = 0;

            var settings = MainForm.Flow();
            settings.Margin = Padding.Empty; settings.Padding = Padding.Empty; settings.BackColor = CardGrey;
            var colon = new Label { Text = ":", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            var uidLabel = MainForm.FieldLabel("Id");
            var scaleLabel = MainForm.FieldLabel("Канал весов");
            settings.Controls.Add(_poll);
            settings.Controls.Add(MainForm.Group(MainForm.FieldLabel("IP"), _ip, colon, _port));
            settings.Controls.Add(MainForm.Group(uidLabel, _uid));
            settings.Controls.Add(MainForm.Group(scaleLabel, _scale));

            _tips.SetToolTip(_poll, "Начать/остановить опрос этого прибора. Второй прибор не затрагивается. " +
                "Пока опрос включён, IP, порт, Id и весы не редактируются.");
            string uidTip = "Unit Identifier — адрес устройства в запросе Modbus TCP (7-й байт MBAP), та же роль, что адрес в Modbus RTU. " +
                "Должен совпадать с настройкой прибора.";
            _tips.SetToolTip(uidLabel, uidTip); _tips.SetToolTip(_uid, uidTip);
            string scaleTip = "Канал весов внутри выбранного M10, а не номер карточки программы. " +
                "«Прибор 2» не означает канал «Весы 2»: если в этом M10 одни весы, выбирайте канал 1. " +
                "Каналы 2 и 3 выбирайте только при их наличии в приборе. " +
                "Текущие показания: 2000+33·(n−1), конфигурация: 1100+33·(n−1).";
            _tips.SetToolTip(scaleLabel, scaleTip); _tips.SetToolTip(_scale, scaleTip);

            _poll.CheckedChanged += (_, _) =>
            {
                _poll.Text = _poll.Checked ? "Остановить опрос" : "Начать опрос";
                PollToggled?.Invoke(this, EventArgs.Empty);
            };

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

            _message.Font = _messageFont;
            _netto.Font = _weightFont; _netto.ForeColor = VividBlue;
            _tareValue.Font = _subValueFont; _bruttoValue.Font = _subValueFont;
            _unitCaption.Font = _captionFont;
            _unitCaption.TextAlign = ContentAlignment.MiddleLeft;
            _stableLed.Dock = _zeroLed.Dock = DockStyle.None;

            var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = GridWhite, Margin = new Padding(0, 1, 1, 1) };
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 34)); side.RowStyles.Add(new RowStyle(SizeType.Percent, 32)); side.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
            side.Controls.Add(LedCell("стаб.", _stableLed), 0, 0);
            side.Controls.Add(_unitCaption, 0, 1);
            side.Controls.Add(LedCell("нуль", _zeroLed), 0, 2);

            _readings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, BackColor = GridWhite, Margin = Padding.Empty };
            _readings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            _readings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            _readings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            _readings.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _readings.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            _readings.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
            _readings.Controls.Add(_rangesHost, 0, 0); _readings.SetRowSpan(_rangesHost, 3);
            _readings.Controls.Add(_netto, 1, 0);
            _readings.Controls.Add(_tareValue, 1, 1);
            _readings.Controls.Add(_bruttoValue, 1, 2);
            _readings.Controls.Add(side, 2, 0);
            _readings.Controls.Add(_tareCaption, 2, 1);
            _readings.Controls.Add(_bruttoCaption, 2, 2);
            foreach (Label label in new[] { _tareCaption, _bruttoCaption })
            {
                label.BackColor = CardGrey; label.ForeColor = VividBlue;
                label.Font = _captionFont; label.Margin = new Padding(1);
            }
            _tareValue.Margin = _bruttoValue.Margin = new Padding(1);
            _tareValue.Padding = _bruttoValue.Padding = new Padding(0, 0, 6, 0);

            _instrumentHost.Controls.Add(_readings);
            _instrumentHost.Controls.Add(_message);
            _message.BringToFront();

            _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
            _layout.AutoScroll = true;
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height + 8));
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, _weightFont.Height + 16 + 2 * (_subValueFont.Height + 4)));
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Font.Height + 10));
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _layout.Controls.Add(settings, 0, 0);
            _layout.Controls.Add(_status, 0, 1);
            _layout.Controls.Add(_instrumentHost, 0, 2);
            _layout.Controls.Add(_stateRow, 0, 3);
            _layout.Controls.Add(_history, 0, 4);
            Controls.Add(_layout);
        }

        private Panel LedCell(string caption, Led led)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = CardGrey, Margin = new Padding(1) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 15));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var label = Caption();
            label.Text = caption;
            label.ForeColor = VividBlue;
            label.Font = _captionFont;
            led.Anchor = AnchorStyles.None; led.Margin = Padding.Empty;
            panel.Controls.Add(label, 0, 0); panel.Controls.Add(led, 1, 0);
            return panel;
        }

        internal void CompactRows()
        {
            int stateRow = Font.Height + 10;
            _layout.RowStyles[1].Height = Font.Height + 8;
            _layout.RowStyles[2].Height = _weightFont.Height + 16 + 2 * (_subValueFont.Height + 4);
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

        /// <summary>Locks connection settings while display settings remain available.</summary>
        public void LockSettings(bool locked)
        {
            _ip.Enabled = _port.Enabled = _uid.Enabled = _scale.Enabled = !locked;
        }

        public void SetToggleEnabled(bool enabled) => _poll.Enabled = enabled;

        public void Render(CardView v)
        {
            (_status.BackColor, _status.ForeColor) = v.StatusTone switch
            {
                Tone.Info => (CardGrey, Color.Navy),
                Tone.Good => (CardGrey, VividBlue),
                Tone.Bad => (CardGrey, Color.Firebrick),
                Tone.Warn => (CardGrey, Color.FromArgb(110, 70, 0)),
                _ => (Color.LightGray, Color.Black)
            };
            _status.Text = v.StatusText;
            _tips.SetToolTip(_status, v.StatusTip.Length > 0 ? v.StatusText + Environment.NewLine + v.StatusTip : v.StatusText);

            bool numeric = v.Instrument != null;
            bool fresh = v.HasFreshInstrument;
            _stableLed.SetOn(v.StableIndicator);
            _zeroLed.SetOn(v.ZeroIndicator);
            _message.Visible = !numeric;
            _readings.Visible = numeric;

            if (!numeric)
            {
                var (back, fore) = ReadingColors(v.WeightTone);
                _message.BackColor = back; _message.ForeColor = fore; _message.Text = v.WeightText;
                _tips.SetToolTip(_message, v.WeightText);
                _serial.Visible = false;
            }
            else
            {
                InstrumentView inst = v.Instrument!;
                var (back, fore) = ReadingColors(v.WeightTone);
                _netto.Font = inst.IsAlarm ? _messageFont : _weightFont;
                _netto.BackColor = back; _netto.ForeColor = fore; _netto.Text = inst.NettoText;
                _tareValue.BackColor = back; _tareValue.ForeColor = fore; _tareValue.Text = inst.TareText;
                _bruttoValue.BackColor = back; _bruttoValue.ForeColor = fore; _bruttoValue.Text = inst.BruttoText;
                _unitCaption.Text = "нетто, " + inst.Unit;
                _tareCaption.Text = "тара, " + inst.Unit;
                _bruttoCaption.Text = "брутто, " + inst.Unit;
                int offset = 33 * (inst.ScaleNumber - 1);
                _tips.SetToolTip(_netto, inst.IsAlarm ? v.WeightText + Environment.NewLine + v.StateText
                    : $"WS_Netto_d · {2009 + offset} · {inst.NettoText} {inst.Unit}");
                _tips.SetToolTip(_tareValue, inst.TareIsLastKnown
                    ? "Последняя корректная тара до аварии; текущие весовые значения недействительны"
                    : $"WS_Tare_d · {2011 + offset} · {inst.TareText} {inst.Unit}");
                _tareValue.ForeColor = inst.TareIsLastKnown ? DimInk : fore;
                _tips.SetToolTip(_bruttoValue, inst.IsAlarm ? v.WeightText + Environment.NewLine + v.StateText
                    : $"WS_Brutto_d · {2007 + offset} · {inst.BruttoText} {inst.Unit}");
                _tips.SetToolTip(_unitCaption, $"WS_d_current · {2005 + offset} · d={inst.DivisionText} {inst.Unit}");
                BuildRangesTable(inst.Ranges, inst.AboveMaximumIndicator, fresh);
                _serial.Visible = inst.ScaleSerialNumber != 0;
                _serial.ForeColor = fresh ? VividBlue : DimInk;
                if (_serial.Visible) _serial.Text = $"Весы {inst.ScaleNumber} · № {inst.ScaleSerialNumber}";
            }

            (_state.BackColor, _state.ForeColor) = v.StateTone switch
            {
                Tone.Good => (CardGrey, VividBlue),
                Tone.Warn => (CardGrey, Color.FromArgb(110, 70, 0)),
                Tone.Bad => (CardGrey, Color.Firebrick),
                _ => (CardGrey, Color.DimGray)
            };
            _state.Text = v.StateText;
            _tips.SetToolTip(_state, v.StateText);
            _reset.Visible = v.ShowLatchReset;
        }

        private static (Color Back, Color Fore) ReadingColors(Tone tone) => tone switch
        {
            Tone.Stale => (CardGreyDark, DimInk),
            _ => (CardGrey, VividBlue)
        };

        private void BuildRangesTable(IReadOnlyList<InstrumentRangeView> ranges, IndicatorState aboveMaximum, bool fresh)
        {
            if (_aboveMaximumLed == null || _rangeRows.Count != ranges.Count)
            {
                _serial.Parent?.Controls.Remove(_serial);
                foreach (Control child in _rangesHost.Controls.Cast<Control>().ToArray()) child.Dispose();
                _rangeRows.Clear();
                var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = ranges.Count + 3,
                    BackColor = GridWhite, Margin = Padding.Empty, Padding = new Padding(1, 0, 1, 0) };
                foreach (float width in new[] { 8f, 27f, 27f, 28f })
                    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, _stableLed.Width + 6));
                for (int i = 0; i < ranges.Count + 2; i++)
                    grid.RowStyles.Add(new RowStyle(SizeType.Absolute, _rangeFont.Height + 7));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                string[] headers = { "", "Max", "Min", "e=d", "" };
                for (int i = 0; i < headers.Length; i++) grid.Controls.Add(RangeCell(headers[i]), i, 0);
                var emptyCell = RangeCell("");
                grid.Controls.Add(emptyCell, 0, 1);
                grid.SetColumnSpan(emptyCell, 4);
                var aboveMaximumLed = new Led { BackColor = CardGrey, Size = _stableLed.Size, Margin = Padding.Empty };
                _aboveMaximumLed = aboveMaximumLed;
                var aboveMaximumCell = new Panel { Dock = DockStyle.Fill, BackColor = CardGrey, Margin = new Padding(0, 1, 0, 1) };
                aboveMaximumCell.Controls.Add(aboveMaximumLed);
                aboveMaximumCell.Resize += (_, _) => aboveMaximumLed.Location = new Point(
                    Math.Max(0, (aboveMaximumCell.Width - aboveMaximumLed.Width) / 2),
                    Math.Max(0, (aboveMaximumCell.Height - aboveMaximumLed.Height) / 2));
                _tips.SetToolTip(aboveMaximumLed, "Вес выше Max последнего диапазона");
                grid.Controls.Add(aboveMaximumCell, 4, 1);
                for (int i = 0; i < ranges.Count; i++)
                {
                    var led = new Led { BackColor = CardGrey, Anchor = AnchorStyles.Top | AnchorStyles.Left,
                        Size = _stableLed.Size, Margin = Padding.Empty };
                    var number = RangeCell("");
                    var maximum = RangeCell("");
                    var minimum = RangeCell("");
                    var division = RangeCell("");
                    int row = ranges.Count - i + 1;
                    grid.Controls.Add(number, 0, row);
                    grid.Controls.Add(maximum, 1, row);
                    grid.Controls.Add(minimum, 2, row);
                    grid.Controls.Add(division, 3, row);
                    var ledCell = new Panel { Dock = DockStyle.Fill, BackColor = CardGrey, Margin = new Padding(0, 1, 0, 1) };
                    ledCell.Controls.Add(led);
                    ledCell.Resize += (_, _) => led.Location = new Point(Math.Max(0, (ledCell.Width - led.Width) / 2),
                        Math.Max(0, (ledCell.Height - led.Height) / 2));
                    grid.Controls.Add(ledCell, 4, row);
                    _rangeRows.Add((led, number, maximum, minimum, division));
                }
                _serial.BackColor = CardGrey;
                _serial.Dock = DockStyle.Fill;
                _serial.Margin = Padding.Empty;
                grid.Controls.Add(_serial, 0, ranges.Count + 2);
                grid.SetColumnSpan(_serial, 5);
                _rangesHost.Controls.Add(grid);
            }
            _aboveMaximumLed.Visible = fresh && aboveMaximum != IndicatorState.Off;
            _aboveMaximumLed.SetState(fresh ? aboveMaximum : IndicatorState.Off);
            for (int i = 0; i < ranges.Count; i++)
            {
                InstrumentRangeView r = ranges[i];
                var row = _rangeRows[i];
                row.Number.Text = r.Number.ToString(CultureInfo.InvariantCulture) + ":";
                row.Maximum.Text = r.MaximumText;
                row.Minimum.Text = r.MinimumText;
                row.Division.Text = r.DivisionText;
                foreach (Label label in new[] { row.Number, row.Maximum, row.Minimum, row.Division })
                {
                    label.ForeColor = fresh ? VividBlue : DimInk;
                    _tips.SetToolTip(label, $"Диапазон {r.Number}: Max={r.MaximumText}, Min={r.MinimumText}, e=d={r.DivisionText}");
                }
                row.Active.Size = _stableLed.Size;
                if (row.Active.Parent is { } ledCell)
                    row.Active.Location = new Point(Math.Max(0, (ledCell.Width - row.Active.Width) / 2),
                        Math.Max(0, (ledCell.Height - row.Active.Height) / 2));
                row.Active.SetState(fresh ? r.Indicator : IndicatorState.Off);
            }
        }

        private Label RangeCell(string text) => new Label
        {
            Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, Font = _rangeFont,
            BackColor = CardGrey, ForeColor = VividBlue, TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 1, 0, 1)
        };

        public void AddEvent(CardEvent e)
        {
            AppendHistory(e);
        }

        private void AppendHistory(CardEvent e)
        {
            if (_history.TextLength > 20000) LogTextBox.RemovePrefix(_history, 5000);
            _history.Select(_history.TextLength, 0); _history.SelectionColor = MainForm.ToneColor(e.Tone);
            _history.AppendText(e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + e.Text + Environment.NewLine);
            NativeLogScroll.ToBottom(_history);
        }

        public void ClearHistory() => _history.Clear();

        public DeviceSettings GetSettings() => new DeviceSettings { Ip = IpAddress, Port = Port, UnitId = UnitId, Scale = ScaleNumber,
            Digits = null };

        public void ApplySettings(DeviceSettings? settings)
        {
            if (settings == null) return;
            _ip.Text = settings.Ip ?? ""; _port.Value = Math.Clamp(settings.Port, 1, 65535);
            _uid.Value = Math.Clamp(settings.UnitId, 0, 255); _scale.SelectedIndex = Math.Clamp(settings.Scale, 1, 3) - 1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tips.Dispose(); _weightFont.Dispose(); _messageFont.Dispose();
                _subValueFont.Dispose(); _captionFont.Dispose(); _rangeFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class LogTextBox
    {
        public static void RemovePrefix(RichTextBox box, int length)
        {
            bool readOnly = box.ReadOnly;
            try
            {
                box.ReadOnly = false;
                box.Select(0, length);
                box.SelectedText = "";
            }
            finally
            {
                box.ReadOnly = readOnly;
            }
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
