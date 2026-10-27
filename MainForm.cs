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
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace M10Modbus
{
    public sealed class MainForm : Form
    {
        private readonly ScalePanel _panel1;
        private readonly ScalePanel _panel2;
        private readonly Button _start = new Button { Text = "Начать опрос", AutoSize = true, Height = 34 };
        private readonly CheckBox _second = new CheckBox { Text = "Второй прибор", AutoSize = true, Margin = new Padding(18, 8, 8, 0) };
        private readonly NumericUpDown _interval = new NumericUpDown { Minimum = 100, Maximum = 10000, Increment = 50, Value = 250, Width = 80 };
        private readonly NumericUpDown _timeout = new NumericUpDown { Minimum = 200, Maximum = 30000, Increment = 100, Value = 1500, Width = 80 };
        private readonly CheckBox _latch = new CheckBox { Text = "Фиксировать ошибки веса", Checked = true, AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
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
        private CancellationTokenSource? _cts;
        private Task? _sessionTask;
        private SessionJournal? _journal;
        private string _sessionDirectory = "";
        private bool _closing;
        private bool _allowClose;
        private bool _settingsWarning;
        private int _intervalMs = 250;
        private const int MaxEntries = 5000;
        private sealed record LogEntry(int Device, string Kind, string Text, Color Color);

        public MainForm()
        {
            SuspendLayout();
            Text = "M10 · Монитор Modbus TCP";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1200, 850);
            MinimumSize = new Size(1050, 800);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SystemColors.Control;
            _panel1 = new ScalePanel(1, "10.0.1.18", AddLog);
            _panel2 = new ScalePanel(2, "10.0.1.8", AddLog);
            BuildLayout();
            LoadSettings();
            UpdateSecond();
            _start.Click += StartStop_Click;
            _second.CheckedChanged += (_, _) => UpdateSecond();
            _interval.ValueChanged += (_, _) => Volatile.Write(ref _intervalMs, (int)_interval.Value);
            Volatile.Write(ref _intervalMs, (int)_interval.Value);
            if (!_settingsWarning) _status.Text = "Готово · FC03, только чтение · настройки сохраняются автоматически";
            ResumeLayout(true);
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var toolbar = Flow();
            _start.BackColor = Color.LightGreen; _start.ForeColor = Color.Black; _start.FlatStyle = FlatStyle.Standard;
            _start.Font = new Font(Font, FontStyle.Bold);
            toolbar.Controls.Add(_start);
            AddField(toolbar, "Период, мс", _interval);
            AddField(toolbar, "Таймаут, мс", _timeout);
            toolbar.Controls.Add(_second); toolbar.Controls.Add(_latch);
            _tips.SetToolTip(_interval, "Минимальный период между началами успешных циклов. При медленном ответе запросы не накладываются.");
            _tips.SetToolTip(_timeout, "Лимит подключения и каждого обмена целиком, включая запись запроса и чтение ответа.");
            _tips.SetToolTip(_latch, "После ошибки: 12 чистых ответов до возврата веса. Три ошибки до восстановления фиксируются до ручного снятия. Команды прибору не посылаются.");
            root.Controls.Add(toolbar, 0, 0);
            var scales = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            scales.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); scales.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            scales.Controls.Add(_panel1, 0, 0); scales.Controls.Add(_panel2, 1, 0);
            root.Controls.Add(scales, 0, 1);
            var journalBox = new GroupBox { Text = "Журнал обмена", Dock = DockStyle.Fill, Padding = new Padding(10) };
            var journalLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            journalLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); journalLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var logTools = Flow();
            _view.Items.AddRange(new object[] { "Все записи", "HEX: TX / RX", "Расшифровка", "События" }); _view.SelectedIndex = 0;
            _deviceFilter.Items.AddRange(new object[] { "Оба прибора", "Прибор 1", "Прибор 2" }); _deviceFilter.SelectedIndex = 0;
            _view.SelectedIndexChanged += (_, _) => RenderLog(); _deviceFilter.SelectedIndexChanged += (_, _) => RenderLog();
            var clear = MakeButton("Clear · Очистить", (_, _) => { _entries.Clear(); _log.Clear(); _panel1.ClearHistory(); _panel2.ClearHistory(); });
            var copy = MakeButton("Копировать", (_, _) => CopyLog());
            var save = MakeButton("Сохранить…", (_, _) => SaveLog());
            var folder = MakeButton("Папка журналов", (_, _) => OpenLogFolder());
            _tips.SetToolTip(clear, "Очистить экран и буфер отображения. Файлы текущей сессии сохраняются без изменений.");
            _tips.SetToolTip(copy, "Копировать выделенный текст; без выделения — весь видимый журнал.");
            _tips.SetToolTip(save, "Экспортировать текущее содержимое экрана. Полные журналы сессий находятся в папке журналов.");
            _hold.CheckedChanged += (_, _) =>
            {
                _panel1.SetHold(_hold.Checked); _panel2.SetHold(_hold.Checked);
                if (!_hold.Checked) RenderLog();
            };
            _tips.SetToolTip(_hold, "Зафиксировать журналы на экране. Опрос и полная запись в файлы продолжаются. При снятии Hold выводятся последние 5000 записей.");
            _tips.SetToolTip(_scroll, "Выключите, чтобы читать предыдущие записи. Запись в файлы и прием пакетов продолжаются.");
            logTools.Controls.AddRange(new Control[] { _view, _deviceFilter, clear, copy, save, folder, _hold, _scroll });
            journalLayout.Controls.Add(logTools, 0, 0); journalLayout.Controls.Add(_log, 0, 1);
            journalBox.Controls.Add(journalLayout); root.Controls.Add(journalBox, 0, 2);
            var strip = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false }; strip.Items.Add(_status);
            root.Controls.Add(strip, 0, 3); Controls.Add(root);
            AcceptButton = _start;
        }
        internal static FlowLayoutPanel Flow() => new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = true, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(0, 3, 0, 3) };
        internal static void AddField(FlowLayoutPanel flow, string label, Control control)
        {
            // Keep caption and input together when the toolbar wraps.
            var field = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, Margin = new Padding(6, 0, 0, 0) };
            field.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(4, 6, 4, 0) });
            field.Controls.Add(control);
            flow.Controls.Add(field);
        }
        internal static Button MakeButton(string text, EventHandler action)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 29), FlatStyle = FlatStyle.System };
            button.Click += action; return button;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitInputWidths(this);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            FitInputWidths(this);
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
                if (control is ScalePanel scalePanel) scalePanel.CompactRows();
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
            }
        }
        private void UpdateSecond() { _panel2.Enabled = _second.Checked; _panel2.SetIdle(_second.Checked ? "Опрос остановлен" : "Прибор не выбран"); }
        private async void StartStop_Click(object? sender, EventArgs e)
        {
            if (_cts != null)
            {
                _start.Enabled = false; _start.Text = "Остановка…"; _cts.Cancel(); return;
            }
            if (!_panel1.ValidateSettings() || (_second.Checked && !_panel2.ValidateSettings())) return;
            if (_second.Checked && _panel1.ConnectionKey == _panel2.ConnectionKey)
            {
                MessageBox.Show(this, "Приборы имеют одинаковые IP, порт, Unit ID и номер весов. Измените настройки второго прибора.",
                    "Одинаковые подключения", MessageBoxButtons.OK, MessageBoxIcon.Information); return;
            }
            SaveSettings();
            _cts = new CancellationTokenSource();
            SetRunning(true);
            _sessionTask = RunSessionAsync(_cts.Token);
            try { await _sessionTask; }
            catch (Exception ex)
            {
                _status.Text = "Ошибка опроса: " + ex.Message;
                AddLog(0, "EVENT", "Опрос остановлен: " + ex.Message, Color.Firebrick);
            }
            finally
            {
                _cts.Dispose(); _cts = null; _sessionTask = null;
                SetRunning(false);
                if (!_closing) _start.Focus();
            }
        }
        private void SetRunning(bool running)
        {
            _start.Text = running ? "Остановить" : "Начать опрос"; _start.Enabled = true;
            _start.BackColor = running ? Color.LightCoral : Color.LightGreen;
            _second.Enabled = !running; _timeout.Enabled = !running; _latch.Enabled = !running;
            _panel1.LockSettings(running); _panel2.LockSettings(running);
        }
        private async Task RunSessionAsync(CancellationToken ct)
        {
            bool useSecond = _second.Checked;
            var journal = new SessionJournal();
            _journal = journal;
            try
            {
                _sessionDirectory = Path.Combine(_dataRoot, "logs", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Directory.CreateDirectory(_sessionDirectory);
                journal.Start(_sessionDirectory, error => Post(() => {
                    _status.Text = "Не удалось записать журнал: " + error;
                    AddLog(0, "EVENT", "Ошибка записи журнала: " + error, Color.Firebrick);
                }));
                _status.Text = "Опрос · полный журнал: " + _sessionDirectory;
                AddLog(0, "EVENT", "Начало сессии. FC03; адреса из документа, без смещения −1. RX показывает адрес/число слов соответствующего TX.", Color.SlateGray);
                _panel1.StartSession(_latch.Checked);
                if (useSecond) _panel2.StartSession(_latch.Checked);
                await Task.WhenAll(RunPollLoopAsync(_panel1, ct), useSecond ? RunPollLoopAsync(_panel2, ct) : Task.CompletedTask);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally
            {
                _panel1.SetIdle("Опрос остановлен"); if (useSecond) _panel2.SetIdle("Опрос остановлен");
                AddLog(0, "EVENT", "Опрос остановлен.", Color.SlateGray);
                await journal.DisposeAsync(); _journal = null;
                _status.Text = journal.Error == null ? "Остановлено · журнал: " + _sessionDirectory : "Ошибка записи журнала: " + journal.Error;
            }
        }
        private async Task RunPollLoopAsync(ScalePanel panel, CancellationToken ct)
        {
            using var client = new M10ModbusClient(panel.IpAddress, panel.Port, panel.UnitId, panel.ScaleNumber, panel.ReadUnrounded) { TimeoutMs = (int)_timeout.Value };
            client.Trace += frame => Post(() => AddLog(panel.DeviceNumber, "HEX", frame.ToString(),
                !frame.IsComplete ? Color.Firebrick : frame.Direction == "TX" ? Color.FromArgb(25, 82, 150) : Color.FromArgb(25, 115, 78), timestamp: false));
            int failures = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var timer = Stopwatch.StartNew();
                    M10PollResult result = await client.PollAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    panel.ProcessPollResult(result);
                    failures = result.IsConnected ? 0 : failures + 1;
                    int period = Volatile.Read(ref _intervalMs);
                    // Bounded reconnect backoff, independently for each instrument.
                    int wait = result.IsConnected ? Math.Max(0, period - (int)timer.ElapsedMilliseconds)
                        : Math.Max(period, Math.Min(5000, 500 * Math.Min(failures, 10)));
                    await Task.Delay(wait, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch { _cts?.Cancel(); throw; } // A failed loop must also release Task.WhenAll for the other device.
        }
        private void Post(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
        }
        private void AddLog(int device, string kind, string text, Color color, bool timestamp = true)
        {
            if (timestamp) text = $"[{DateTime.Now:HH:mm:ss.fff}] [{(device == 0 ? "Система" : "Прибор " + device)}] {text}";
            var entry = new LogEntry(device, kind, text, color);
            _journal?.Write("traffic.txt", text);
            if (device > 0 && kind == "EVENT") _journal?.Write($"info_device{device}.txt", text);
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries) _entries.Dequeue();
            if (!_hold.Checked && Matches(entry)) AppendEntry(entry);
        }
        private static Color LogInk(Color color) => color == Color.Firebrick ? Color.Salmon
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
        private void LoadSettings()
        {
            string file = Path.Combine(_dataRoot, "settings.json");
            try
            {
                if (!File.Exists(file)) return;
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file));
                if (settings == null) return;
                _panel1.ApplySettings(settings.First); _panel2.ApplySettings(settings.Second);
                _second.Checked = settings.UseSecond; _latch.Checked = settings.Latch;
                _interval.Value = Math.Clamp(settings.Interval, (int)_interval.Minimum, (int)_interval.Maximum);
                _timeout.Value = Math.Clamp(settings.Timeout, (int)_timeout.Minimum, (int)_timeout.Maximum);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            { _settingsWarning = true; _status.Text = "Настройки не прочитаны; использованы значения по умолчанию: " + ex.Message; }
        }
        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(_dataRoot);
                var settings = new AppSettings { First = _panel1.GetSettings(), Second = _panel2.GetSettings(),
                    UseSecond = _second.Checked, Latch = _latch.Checked, Interval = (int)_interval.Value, Timeout = (int)_timeout.Value };
                string file = Path.Combine(_dataRoot, "settings.json");
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(file + ".tmp", file, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _status.Text = "Настройки не сохранены: " + ex.Message; }
        }
        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true; _start.Enabled = false; _cts?.Cancel();
            if (_sessionTask != null)
            {
                try { await _sessionTask; } catch (Exception) { /* Session errors are shown by the start handler. */ }
            }
            SaveSettings(); _allowClose = true; Close();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tips.Dispose(); _cts?.Cancel(); }
            base.Dispose(disposing);
        }
    }

    public sealed class ScalePanel : GroupBox
    {
        private readonly Action<int, string, string, Color, bool> _logger;
        private readonly TextBox _ip;
        private readonly CheckBox _raw = new CheckBox { Text = "Без округления", AutoSize = true, Margin = new Padding(12, 6, 4, 0) };
        private readonly ComboBox _digits = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64 };
        private readonly Queue<(string Text, Color Color)> _eventBuffer = new Queue<(string, Color)>();
        private bool _historyHold;
        private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 502, Width = 72 };
        private readonly NumericUpDown _uid = new NumericUpDown { Minimum = 0, Maximum = 255, Value = 1, Width = 55 };
        private readonly ComboBox _scale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 56 };
        private readonly Label _connection = Caption("Опрос остановлен");
        private readonly Label _weight = new Label { Text = "—", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Color.FromArgb(30, 70, 100), Font = new Font("Consolas", 34F, FontStyle.Bold), AutoEllipsis = true };
        private readonly Label _state = Caption("Ожидание данных");
        private readonly Label _detail = Caption("");
        private readonly Label _info = Caption("");
        private readonly Label _last = Caption("");
        private readonly Button _reset;
        private readonly RichTextBox _history = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = Color.White, DetectUrls = false, Font = new Font("Segoe UI", 9F) };
        private readonly FlowLayoutPanel _settings;
        private readonly Font _weightFont = new Font("Consolas", 34F, FontStyle.Bold);
        private readonly Font _messageFont = new Font("Segoe UI", 21F, FontStyle.Bold);
        private bool _latchEnabled;
        private bool _latched;
        private bool _rechecking;
        private int _faults, _clean, _failures;
        private long _ok, _errors;
        private ushort _heldMask;
        private string _lastEvent = "";
        private DateTime? _lastSuccess;
        public int DeviceNumber { get; }
        public string IpAddress => _ip.Text.Trim();
        public int Port => (int)_port.Value;
        public byte UnitId => (byte)_uid.Value;
        public bool ReadUnrounded => _raw.Checked;
        public int ScaleNumber => _scale.SelectedIndex + 1;
        public string ConnectionKey => $"{IpAddress}:{Port}/{UnitId}/{ScaleNumber}";
        private static Label Caption(string text) => new Label { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 4, 0) };

        public ScalePanel(int number, string ip, Action<int, string, string, Color, bool> logger)
        {
            DeviceNumber = number; _logger = logger; Text = "Весовой прибор M10 №" + number;
            Dock = DockStyle.Fill;
            BackColor = SystemColors.Control; Padding = new Padding(10); Margin = new Padding(4);
            _ip = new TextBox { Text = ip, Width = 120 };
            _scale.Items.AddRange(new object[] { "1", "2", "3" }); _scale.SelectedIndex = 0;
            _settings = MainForm.Flow();
            MainForm.AddField(_settings, "IP", _ip); MainForm.AddField(_settings, "Порт", _port);
            MainForm.AddField(_settings, "UID", _uid); MainForm.AddField(_settings, "Весы", _scale);
            _digits.Items.Add("Авто"); for (int i = 0; i <= 8; i++) _digits.Items.Add(i.ToString()); _digits.SelectedIndex = 0;
            _settings.Controls.Add(_raw); MainForm.AddField(_settings, "Знаков", _digits);
            _raw.CheckedChanged += (_, _) => _digits.Enabled = !_raw.Checked;
            var layout = new TableLayoutPanel { Name = "ScaleLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int i = 0; i < 7; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _weight.MinimumSize = new Size(0, _weightFont.Height + 16);
            foreach (Label caption in new[] { _connection, _state, _detail, _info, _last })
            {
                caption.AutoSize = false;
                caption.Height = Font.Height + 8;
                caption.MinimumSize = new Size(0, Font.Height + 8);
            }
            _history.MinimumSize = Size.Empty;
            _history.BackColor = Color.FromArgb(250, 250, 245);
            _history.BorderStyle = BorderStyle.FixedSingle;
            _weight.BackColor = Color.Black; _weight.BorderStyle = BorderStyle.Fixed3D;
            _connection.TextAlign = _state.TextAlign = ContentAlignment.MiddleCenter;
            _connection.Font = _state.Font = new Font(Font, FontStyle.Bold);
            _reset = MainForm.MakeButton("Снять фиксацию", (_, _) => ResetLatch()); _reset.Enabled = false;
            var actions = MainForm.Flow(); actions.Margin = Padding.Empty; actions.Padding = Padding.Empty; actions.Controls.Add(_reset);
            var clearHistory = MainForm.MakeButton("Очистить события", (_, _) => ClearHistory()); actions.Controls.Add(clearHistory);
            layout.Controls.Add(_settings, 0, 0); layout.Controls.Add(_connection, 0, 1); layout.Controls.Add(_weight, 0, 2);
            layout.Controls.Add(_state, 0, 3); layout.Controls.Add(_detail, 0, 4); layout.Controls.Add(_info, 0, 5);
            layout.Controls.Add(_last, 0, 6); layout.Controls.Add(actions, 0, 7); layout.Controls.Add(_history, 0, 8);
            Controls.Add(layout);
        }
        internal void CompactRows()
        {
            if (Controls[0] is not TableLayoutPanel layout) return;
            int line = Font.Height + 8;
            _weight.MinimumSize = Size.Empty;
            foreach (Label label in new[] { _connection, _state, _detail, _info, _last }) label.MinimumSize = Size.Empty;
            layout.RowStyles[1].Height = line;
            layout.RowStyles[2].Height = _weightFont.Height + 16;
            layout.RowStyles[3].Height = line;
            layout.RowStyles[4].Height = line;
            layout.RowStyles[5].Height = 0; // Identification remains available as a tooltip on connection status.
            _info.Visible = false;
            layout.RowStyles[6].Height = line;
            layout.RowStyles[7].Height = Font.Height + 20;
        }
        private readonly ToolTip _deviceTip = new ToolTip();
        public bool ValidateSettings()
        {
            if (IPAddress.TryParse(IpAddress, out _)) return true;
            MessageBox.Show(FindForm(), $"Прибор {DeviceNumber}: введите корректный IP-адрес.", "Настройки подключения", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _ip.Focus(); _ip.SelectAll(); return false;
        }
        public void LockSettings(bool locked) => _settings.Enabled = !locked;
        public void StartSession(bool latch)
        {
            _latchEnabled = latch; _latched = false; _rechecking = false; _faults = _clean = _failures = 0; _heldMask = 0;
            _ok = _errors = 0; _lastSuccess = null; _lastEvent = ""; _reset.Enabled = false;
            _weight.Font = _weightFont; _weight.Text = "—"; _detail.Text = ""; _info.Text = "";
            _connection.Text = $"Подключение к {IpAddress}:{Port} · UID {UnitId}";
            _connection.BackColor = Color.LightSteelBlue; _connection.ForeColor = Color.Black; _state.Text = "Идентификация и чтение конфигурации…"; _last.Text = "Ответов еще нет";
            Event($"Начало опроса {IpAddress}:{Port}, UID {UnitId}, весы {ScaleNumber}.", Color.SlateGray);
        }
        public void ProcessPollResult(M10PollResult raw)
        {
            if (!raw.IsConnected)
            {
                _errors++; _failures++; _clean = 0;
                _connection.Text = $"Нет ответа · {IpAddress}:{Port} · повторное подключение";
                _connection.BackColor = Color.Crimson; _connection.ForeColor = Color.White; _state.Text = "Ошибка обмена: " + raw.ConnectionError; _state.ForeColor = Color.Firebrick;
                ShowMessage("Нет данных", Color.Firebrick); _detail.Text = "Показания недоступны";
                _last.Text = Counters();
                _logger(DeviceNumber, "DATA", $"{IpAddress}:{Port} | Ошибка обмена: {raw.ConnectionError}", Color.Firebrick, true);
                if (_failures == 1 || _failures % 10 == 0) Event($"Ошибка обмена #{_failures}: {raw.ConnectionError}", Color.Firebrick);
                return;
            }
            if (_failures > 0 || _lastSuccess == null) Event(_failures > 0 ? "Обмен восстановлен; конфигурация прочитана заново." : "Обмен установлен; прибор идентифицирован.", Color.SeaGreen);
            _failures = 0; _ok++; _lastSuccess = raw.Timestamp;
            _connection.Text = $"Подключено · {IpAddress}:{Port} · UID {UnitId}"; _connection.BackColor = Color.ForestGreen; _connection.ForeColor = Color.White;
            _info.Text = raw.DeviceInfo; _deviceTip.SetToolTip(_connection, raw.DeviceInfo);
            _last.Text = $"Ответ {raw.Timestamp:HH:mm:ss.fff} · {raw.ElapsedMs:F0} мс · {Counters()}";
            string decoded = $"{IpAddress}:{Port} | Весы {ScaleNumber} | Errors=0x{raw.ErrorCode:X4}: {raw.GetMetrologicalErrorDescription()} | " +
                $"Status=0x{raw.StatusMask:X4} | Stable={(raw.IsStable ? 1 : 0)} Zero={(raw.IsZero ? 1 : 0)} BelowMin={(raw.IsBelowMinimum ? 1 : 0)} | " +
                $"Брутто={raw.Brutto.ToString("G9", CultureInfo.InvariantCulture)} Нетто={raw.Netto.ToString("G9", CultureInfo.InvariantCulture)} " +
                $"Тара={raw.Tare.ToString("G9", CultureInfo.InvariantCulture)} {raw.Unit} | d={raw.Division.ToString("G9", CultureInfo.InvariantCulture)} | " +
                $"Диапазон={raw.Range} DT_LastWeight={raw.MeasurementUnixTime}" +
                (raw.BruttoUnrounded.HasValue ? $" | Брутто без округления={raw.BruttoUnrounded.Value.ToString("G9", CultureInfo.InvariantCulture)} {raw.Unit}" : "") +
                (raw.UnroundedError.Length > 0 ? " | " + raw.UnroundedError : "");
            _logger(DeviceNumber, "DATA", decoded, raw.HasScaleError || !raw.HasValidWeight || raw.UnroundedError.Length > 0 ? Color.Firebrick : Color.DimGray, true);
            _state.ForeColor = Color.FromArgb(55, 65, 80);
            string eventKey = $"{raw.ErrorCode}/{raw.StatusMask}/{raw.HasValidWeight}/{raw.UnroundedError}";
            if (_lastEvent != eventKey)
            {
                _lastEvent = eventKey;
                Event(raw.HasScaleError ? raw.GetMetrologicalErrorDescription() : raw.UnroundedError.Length > 0 ? raw.UnroundedError : !raw.HasValidWeight ? "Некорректное числовое значение или дискретность d." :
                    $"{(raw.IsStable ? "Стабильно" : "Не стабильно")}; {(raw.IsBelowMinimum ? "вес ниже Min1" : "вес не ниже Min1")}; ноль: {(raw.IsZero ? "да" : "нет")}.",
                    raw.HasScaleError || !raw.HasValidWeight || raw.UnroundedError.Length > 0 ? Color.Firebrick : Color.SlateGray);
            }
            if (raw.HasScaleError)
            {
                _clean = 0;
                if (_latchEnabled) { _faults++; _heldMask |= raw.ErrorCode; if (_faults >= 3) _latched = true; }
                _reset.Enabled = _latchEnabled && _faults > 0;
                ShowMessage($"Ошибка 0x{raw.ErrorCode:X4}", Color.Firebrick);
                _state.Text = raw.GetMetrologicalErrorDescription();
                _detail.Text = _latched ? "Ошибка зафиксирована · снимите фиксацию после проверки" : "Вес недействителен по флагу Errors прибора";
                return;
            }
            if (!raw.HasValidWeight)
            {
                _clean = 0; ShowMessage("Некорректные данные", Color.Firebrick);
                _state.Text = "Числовые значения или дискретность d недопустимы"; _detail.Text = "См. HEX и расшифровку в журнале"; return;
            }
            if (_raw.Checked && !raw.BruttoUnrounded.HasValue)
            {
                _clean = 0; ShowMessage("Некорректные данные", Color.Firebrick);
                _state.Text = raw.UnroundedError; _detail.Text = "Округленное брутто доступно в расшифровке журнала"; return;
            }
            if (_latched)
            {
                ShowMessage($"Фиксация 0x{_heldMask:X4}", Color.DarkOrange);
                _state.Text = "Сейчас ошибок нет · предыдущая ошибка зафиксирована";
                _detail.Text = "«Снять фиксацию» запускает повторную проверку показаний"; return;
            }
            if (_latchEnabled && (_faults > 0 || _rechecking))
            {
                _clean++;
                if (_clean < 12)
                {
                    ShowMessage($"Проверка {_clean}/12", Color.DarkOrange);
                    _state.Text = "Ожидание 12 ответов без ошибок"; _detail.Text = "Показания временно скрыты после ошибки"; return;
                }
                _faults = _clean = 0; _heldMask = 0; _rechecking = false; _reset.Enabled = false;
                Event("Проверка завершена: 12 последовательных корректных ответов.", Color.SeaGreen);
            }
            _weight.Font = _weightFont; _weight.ForeColor = raw.IsStable && !raw.IsBelowMinimum ? Color.Lime : Color.Yellow;
            string weight = _raw.Checked ? raw.BruttoUnrounded!.Value.ToString("G9", CultureInfo.CurrentCulture)
                : _digits.SelectedIndex == 0 ? raw.FormatWeight(raw.Brutto) : raw.Brutto.ToString("F" + (_digits.SelectedIndex - 1), CultureInfo.CurrentCulture);
            _weight.Text = weight + " " + raw.Unit;
            _state.BackColor = raw.IsStable && !raw.IsBelowMinimum ? Color.PaleGreen : Color.LightGoldenrodYellow;
            _state.Text = raw.IsBelowMinimum ? "Ниже Min1 · не подлежит учету" : raw.IsStable ? "Стабильно" : "Не стабильно · не подлежит учету";
            _detail.Text = $"Нетто {raw.FormatWeight(raw.Netto)} · Тара {raw.FormatWeight(raw.Tare)} {raw.Unit} · Ноль: {(raw.IsZero ? "да" : "нет")}";
        }
        private string Counters() => $"Ответов {_ok} · сбоев {_errors}" + (_lastSuccess.HasValue && _failures > 0 ? $" · последний {_lastSuccess:HH:mm:ss}" : "");
        private void ShowMessage(string text, Color color) { _weight.Font = _messageFont; _weight.ForeColor = color == Color.Firebrick ? Color.OrangeRed : color; _weight.Text = text;
            _state.BackColor = Color.MistyRose; }
        private void ResetLatch()
        {
            _latched = false; _rechecking = true; _faults = 0; _clean = 0; _heldMask = 0; _reset.Enabled = false;
            ShowMessage("Повторная проверка", Color.DarkOrange);
            _state.Text = "Ожидание 12 корректных ответов";
            Event("Локальная фиксация снята. Проверка 12 ответов; команда сброса прибору не отправлялась.", Color.SlateGray);
        }
        private void Event(string message, Color color)
        {
            string line = $"{DateTime.Now:HH:mm:ss}  {message}";
            _eventBuffer.Enqueue((line, color)); while (_eventBuffer.Count > 200) _eventBuffer.Dequeue();
            if (!_historyHold) AppendHistory(line, color);
            _logger(DeviceNumber, "EVENT", $"{IpAddress}:{Port} | {message}", color, true);
        }
        private void AppendHistory(string line, Color color)
        {
            if (_history.TextLength > 20000) { _history.Select(0, 5000); _history.SelectedText = ""; }
            _history.Select(_history.TextLength, 0); _history.SelectionColor = color;
            _history.AppendText(line + Environment.NewLine); NativeLogScroll.ToBottom(_history);
        }
        public void ClearHistory() { _eventBuffer.Clear(); _history.Clear(); }
        public void SetHold(bool hold)
        {
            _historyHold = hold;
            if (hold) return;
            _history.Clear(); foreach (var item in _eventBuffer) AppendHistory(item.Text, item.Color);
        }
        public void SetIdle(string text)
        {
            _connection.Text = text; _connection.BackColor = Color.LightGray; _connection.ForeColor = Color.Black;
            _state.BackColor = SystemColors.Control; _state.ForeColor = Color.Black;
            _weight.Font = _weightFont; _weight.Text = "—"; _weight.ForeColor = Color.SlateGray;
            _state.Text = "Нет текущих показаний"; _detail.Text = ""; _reset.Enabled = false;
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
        { if (disposing) { _deviceTip.Dispose(); _weightFont.Dispose(); _messageFont.Dispose(); } base.Dispose(disposing); }
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

    internal sealed class AppSettings
    {
        public DeviceSettings First { get; set; } = new DeviceSettings { Ip = "10.0.1.18" };
        public DeviceSettings Second { get; set; } = new DeviceSettings { Ip = "10.0.1.8" };
        [JsonPropertyName("SecondEnabled")]
        public bool UseSecond { get; set; }
        public bool Latch { get; set; } = true;
        [JsonPropertyName("PeriodMs")]
        public int Interval { get; set; } = 250;
        [JsonPropertyName("TimeoutMs")]
        public int Timeout { get; set; } = 1500;
    }
    public sealed class DeviceSettings
    {
        public string Ip { get; set; } = "";
        public int Port { get; set; } = 502;
        public int UnitId { get; set; } = 1;
        public int Scale { get; set; } = 1;
        public bool ReadUnrounded { get; set; }
        public int? Digits { get; set; }
        public string UnroundedAddress { get; set; } = "";
    }

    // File I/O runs separately from the UI. An unbounded queue avoids silently losing HEX frames.
    // At <= 10 polls/s per device this stays small on a healthy local disk.
    internal sealed class SessionJournal : IAsyncDisposable
    {
        private readonly Channel<(string File, string Text)> _queue = Channel.CreateUnbounded<(string, string)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private Task _writer = Task.CompletedTask;
        public string? Error { get; private set; }
        public void Start(string directory, Action<string> failed)
        {
            _writer = Task.Run(async () =>
            {
                var files = new Dictionary<string, StreamWriter>();
                try
                {
                    await foreach (var entry in _queue.Reader.ReadAllAsync())
                    {
                        if (!files.TryGetValue(entry.File, out var writer))
                        {
                            writer = new StreamWriter(Path.Combine(directory, entry.File), false, new UTF8Encoding(false)) { AutoFlush = true };
                            files.Add(entry.File, writer);
                        }
                        await writer.WriteLineAsync(entry.Text);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { Error = ex.Message; _queue.Writer.TryComplete(); failed(ex.Message); }
                finally
                {
                    foreach (var writer in files.Values)
                    {
                        try { await writer.DisposeAsync(); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        { if (Error == null) { Error = ex.Message; failed(ex.Message); } }
                    }
                }
            });
        }
        public void Write(string file, string text) { if (Error == null) _queue.Writer.TryWrite((file, text)); }
        public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); await _writer; }
    }
}
