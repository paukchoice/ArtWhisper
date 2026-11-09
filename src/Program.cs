using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Whisper.net;

namespace ArtWhisper;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Один экземпляр: если программа уже запущена — молча выходим
        using var singleInstance = new Mutex(true, "ArtWhisper_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Log.Info("Программа уже запущена — повторный запуск проигнорирован");
            return;
        }

        try
        {
            Log.Info("=== запуск Art Whisper ===");
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => FatalReporter.Report(e.Exception, "интерфейс");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    FatalReporter.Report(ex, "фоновый поток");
            };
            Application.Run(new VoiceApp());
            Log.Info("=== выход ===");
        }
        catch (Exception ex)
        {
            FatalReporter.Report(ex, "запуск");
        }
    }
}

// ---------------------------------------------------------------------------
// Журнал — чтобы при проблеме было, что прислать
// ---------------------------------------------------------------------------

internal static class Log
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtWhisper");

    public static string FilePath => Path.Combine(Dir, "art-whisper.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
        }
        catch { /* журнал не должен ронять программу */ }
    }
}

// ---------------------------------------------------------------------------
// Критические ошибки — окно + запись в журнал
// ---------------------------------------------------------------------------

internal static class FatalReporter
{
    public static void Report(Exception ex, string where)
    {
        Log.Error($"Необработанная ошибка ({where})", ex);
        try
        {
            MessageBox.Show(
                $"Произошла ошибка ({where}):\n\n{ex.Message}\n\nПодробности записаны в файл:\n{Log.FilePath}\n\n" +
                "Если обратитесь за помощью — пришлите этот файл.",
                "Art Whisper — ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}

// ---------------------------------------------------------------------------
// Настройки
// ---------------------------------------------------------------------------

internal sealed class Config
{
    public string Model { get; set; } = "small";   // tiny / base / small / medium
    public bool AutoPaste { get; set; } = true;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtWhisper");

    public static string FilePath => Path.Combine(Dir, "config.json");

    public static Config Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(System.IO.File.ReadAllText(FilePath)) ?? new Config();
        }
        catch { /* повреждённый config — берём умолчания */ }
        return new Config();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

// ---------------------------------------------------------------------------
// Запись с микрофона через winmm (waveIn) — без внешних зависимостей
// ---------------------------------------------------------------------------

internal sealed class MicRecorder
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int BufferBytes = 8192;        // 4096 сэмплов по 2 байта
    private const int BufferCount = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public int dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr hWaveIn, int uDeviceID, ref WaveFormatEx lpFormat,
        WaveInProc dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hWaveIn);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hWaveIn);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr hWaveIn);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hWaveIn);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr pwh, int cbwh);

    private const uint CALLBACK_FUNCTION = 0x00030000;
    private const int WIM_DATA = 0x03C0;

    private IntPtr _handle;
    private WaveInProc? _callback;                  // держим ссылку — иначе GC соберёт делегат
    private readonly WaveHdr[] _headers = new WaveHdr[BufferCount];
    private GCHandle _headersHandle;                // закреплённый массив заголовков
    private GCHandle[] _dataHandles = Array.Empty<GCHandle>();
    private readonly ConcurrentQueue<byte[]> _chunks = new();
    private volatile bool _recording;

    public void Start()
    {
        var fmt = new WaveFormatEx
        {
            wFormatTag = 1,                         // PCM
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = (short)(BitsPerSample / 8),
            nAvgBytesPerSec = SampleRate * BitsPerSample / 8,
            cbSize = 0,
        };

        _callback = OnWaveInMessage;
        int rc = waveInOpen(out _handle, -1, ref fmt, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
        if (rc != 0)
            throw new InvalidOperationException($"waveInOpen вернул код {rc} (микрофон недоступен?)");

        _chunks.Clear();
        _recording = true;

        _dataHandles = new GCHandle[BufferCount];
        for (int i = 0; i < BufferCount; i++)
        {
            _dataHandles[i] = GCHandle.Alloc(new byte[BufferBytes], GCHandleType.Pinned);
            _headers[i] = new WaveHdr
            {
                lpData = _dataHandles[i].AddrOfPinnedObject(),
                dwBufferLength = BufferBytes,
            };
        }
        _headersHandle = GCHandle.Alloc(_headers, GCHandleType.Pinned);
        int hdrSize = Marshal.SizeOf<WaveHdr>();
        IntPtr basePtr = _headersHandle.AddrOfPinnedObject();
        for (int i = 0; i < BufferCount; i++)
        {
            IntPtr p = basePtr + i * hdrSize;
            waveInPrepareHeader(_handle, p, hdrSize);
            waveInAddBuffer(_handle, p, hdrSize);
        }
        waveInStart(_handle);
        Log.Info("Микрофон: запись начата");
    }

    private void OnWaveInMessage(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg != WIM_DATA || !_recording) return;
        var hdr = Marshal.PtrToStructure<WaveHdr>(dwParam1);
        if (hdr.dwBytesRecorded > 0)
        {
            var data = new byte[hdr.dwBytesRecorded];
            Marshal.Copy(hdr.lpData, data, 0, hdr.dwBytesRecorded);
            _chunks.Enqueue(data);
        }
        waveInAddBuffer(_handle, dwParam1, Marshal.SizeOf<WaveHdr>());   // вернуть буфер драйверу
    }

    /// <summary>Останавливает запись и возвращает сэмплы float32 [-1..1] с частотой 16 кГц.</summary>
    public float[] Stop()
    {
        _recording = false;
        if (_handle == IntPtr.Zero) return Array.Empty<float>();

        waveInStop(_handle);
        waveInReset(_handle);
        int hdrSize = Marshal.SizeOf<WaveHdr>();
        IntPtr basePtr = _headersHandle.AddrOfPinnedObject();
        for (int i = 0; i < BufferCount; i++)
            waveInUnprepareHeader(_handle, basePtr + i * hdrSize, hdrSize);
        waveInClose(_handle);
        _handle = IntPtr.Zero;

        _headersHandle.Free();
        foreach (var h in _dataHandles) h.Free();
        _dataHandles = Array.Empty<GCHandle>();
        _callback = null;

        var bytes = _chunks.SelectMany(c => c).ToArray();
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        var floats = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            floats[i] = samples[i] / 32768f;
        return floats;
    }
}

// ---------------------------------------------------------------------------
// Скачивание ggml-моделей Whisper
// ---------------------------------------------------------------------------

internal static class ModelManager
{
    private static readonly string[] Hosts =
    {
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main",
        "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main",   // запасное зеркало
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static string ModelsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtWhisper", "models");

    public static string ModelPath(string model) => Path.Combine(ModelsDir, $"ggml-{model}.bin");

    public static async Task EnsureModelAsync(string model, IProgress<string>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelsDir);
        string dest = ModelPath(model);
        if (System.IO.File.Exists(dest)) return;

        string tmp = dest + ".download";
        Exception? last = null;
        foreach (var host in Hosts)
        {
            try
            {
                progress?.Report($"Скачивание модели {model}…");
                using var response = await Http.GetAsync($"{host}/ggml-{model}.bin", HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                Log.Info($"Скачивание модели {model} с {host} ({total / 1048576} МБ)");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                var buf = new byte[81920];
                long done = 0; int read; var sw = Stopwatch.StartNew();
                while ((read = await input.ReadAsync(buf, ct)) > 0)
                {
                    await output.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0 && sw.ElapsedMilliseconds > 500)
                    {
                        progress?.Report($"Скачивание модели {model}: {done / 1048576} из {total / 1048576} МБ");
                        sw.Restart();
                    }
                }
                System.IO.File.Move(tmp, dest);
                Log.Info($"Модель {model} скачана");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                Log.Error($"Сервер {host} недоступен", ex);
                progress?.Report("Основной сервер недоступен, пробуем зеркало…");
            }
        }
        throw new IOException($"Не удалось скачать модель {model}: {last?.Message}");
    }
}

// ---------------------------------------------------------------------------
// Распознавание (Whisper.net — нативный whisper.cpp)
// ---------------------------------------------------------------------------

internal sealed class WhisperService : IDisposable
{
    private readonly Config _cfg;
    private WhisperFactory? _factory;
    private string? _loadedModel;

    public WhisperService(Config cfg) => _cfg = cfg;

    public async Task<string> TranscribeAsync(float[] samples, IProgress<string>? progress, CancellationToken ct)
    {
        await ModelManager.EnsureModelAsync(_cfg.Model, progress, ct);
        if (_factory is null || _loadedModel != _cfg.Model)
        {
            progress?.Report($"Загрузка модели {_cfg.Model}…");
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(ModelManager.ModelPath(_cfg.Model));
            _loadedModel = _cfg.Model;
            Log.Info($"Модель {_cfg.Model} загружена в память");
        }

        using var processor = _factory.CreateBuilder()
            .WithLanguage("ru")
            .WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(5))
            .Build();

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
            sb.Append(segment.Text);
        var result = sb.ToString().Trim();
        Log.Info($"Распознано {samples.Length / 16000.0:F1} c -> {result.Length} символов");
        return result;
    }

    public void Preload(IProgress<string>? progress)
    {
        if (_factory is not null) return;
        progress?.Report($"Загрузка модели {_cfg.Model}…");
        _factory = WhisperFactory.FromPath(ModelManager.ModelPath(_cfg.Model));
        _loadedModel = _cfg.Model;
        Log.Info($"Модель {_cfg.Model} предзагружена");
    }

    public void Dispose() => _factory?.Dispose();
}

// ---------------------------------------------------------------------------
// Окно запуска — видно, что программа работает
// ---------------------------------------------------------------------------

internal sealed class SplashForm : Form
{
    private readonly TextBox _logBox;
    private readonly ProgressBar _progress;
    private readonly Button _hideButton;
    private readonly Button _micButton;
    private bool _ready;
    private bool _micTestRunning;

    public event Action? HideToTrayRequested;
    public event Func<string>? MicTestRequested;   // возвращает текст результата

    public SplashForm(Icon icon)
    {
        Text = "Art Whisper — голосовой ввод";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 560;
        Height = 400;

        var title = new Label
        {
            Text = "Art Whisper",
            Font = new Font("Segoe UI", 18f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 16),
        };
        var subtitle = new Label
        {
            Text = "Голосовой ввод текста — работает полностью на вашем ПК",
            Font = new Font("Segoe UI", 9.5f),
            AutoSize = true,
            Location = new Point(22, 52),
            ForeColor = Color.DimGray,
        };

        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Font = new Font("Consolas", 9.5f),
            Location = new Point(20, 84),
            Width = 504,
            Height = 210,
            Text = "",
        };

        _progress = new ProgressBar
        {
            Location = new Point(20, 304),
            Width = 504,
            Height = 14,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25,
            Visible = false,
        };

        _hideButton = new Button
        {
            Text = "Свернуть в трей",
            Location = new Point(20, 326),
            Width = 140,
            Height = 30,
            Enabled = false,
        };
        _hideButton.Click += (_, _) => HideToTrayRequested?.Invoke();

        _micButton = new Button
        {
            Text = "Проверить микрофон",
            Location = new Point(172, 326),
            Width = 170,
            Height = 30,
            Enabled = false,
        };
        _micButton.Click += async (_, _) =>
        {
            if (_micTestRunning) return;
            _micTestRunning = true;
            _micButton.Enabled = false;
            LogLine("Проверка микрофона: говорите что-нибудь 3 секунды…");
            SetBusy(true);
            try
            {
                string result = await Task.Run(() => MicTestRequested?.Invoke() ?? "тест недоступен");
                LogLine(result);
            }
            finally
            {
                SetBusy(false);
                _micButton.Enabled = true;
                _micTestRunning = false;
            }
        };

        Controls.AddRange(new Control[] { title, subtitle, _logBox, _progress, _hideButton, _micButton });

        // закрытие крестиком — сворачиваем в трей, а не выходим
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTrayRequested?.Invoke();
            }
        };
    }

    public void SetBusy(bool busy) => _progress.Visible = busy;

    public void MarkReady()
    {
        _ready = true;
        _hideButton.Enabled = true;
        _micButton.Enabled = true;
        SetBusy(false);
        LogLine("Готово! Зажмите ПРАВЫЙ Ctrl, говорите, отпустите — текст появится под курсором.");
        LogLine("Это окно можно закрыть — программа останется в трее (рядом с часами).");
    }

    public void LogLine(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => LogLine(message)); return; }
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}

// ---------------------------------------------------------------------------
// Оверлей-индикатор: цветной кружок, следующий за курсором мыши
// красный — запись, синий — распознавание, зелёный — готово (вспышка)
// ---------------------------------------------------------------------------

internal sealed class OverlayForm : Form
{
    private readonly System.Windows.Forms.Timer _follow = new() { Interval = 40 };
    private Color _dotColor = Color.FromArgb(0x30, 0x30, 0x30);
    private bool _followMouse;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Opacity = 0.9;
        Width = 22;
        Height = 22;
        DoubleBuffered = true;
        _follow.Tick += (_, _) =>
        {
            if (_followMouse && !IsDisposed)
                Location = Cursor.Position + new Size(20, 20);
        };
        _ = Handle;   // создать handle сразу — BeginInvoke из других потоков
    }

    public void ShowDot(Color color, bool followMouse)
    {
        if (IsDisposed) return;
        _dotColor = color;
        _followMouse = followMouse;
        if (followMouse)
            Location = Cursor.Position + new Size(20, 20);
        else
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(area.Right - 40, area.Top + 20);
        }
        if (!Visible) Show();
        _follow.Start();
        Invalidate();
    }

    public void HideState()
    {
        _follow.Stop();
        if (!IsDisposed) Hide();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = new GraphicsPath();
        path.AddEllipse(1, 1, Width - 3, Height - 3);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(_dotColor);
        e.Graphics.FillEllipse(brush, 1, 1, Width - 3, Height - 3);
        using var pen = new Pen(Color.White, 1.5f);
        e.Graphics.DrawEllipse(pen, 1.75f, 1.75f, Width - 4.5f, Height - 4.5f);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }
}

// ---------------------------------------------------------------------------
// Горячая клавиша: опрос правого Ctrl
// ---------------------------------------------------------------------------

internal sealed class PttMonitor
{
    public event Action? Pressed;
    public event Action? Released;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_RCONTROL = 0xA3;

    private volatile bool _running = true;
    private bool _down;

    public void Start()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "PTT" };
        t.Start();
    }

    public void Stop() => _running = false;

    private void Loop()
    {
        while (_running)
        {
            bool down = (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
            if (down && !_down) { _down = true; Pressed?.Invoke(); }
            else if (!down && _down) { _down = false; Released?.Invoke(); }
            Thread.Sleep(15);
        }
    }
}

// ---------------------------------------------------------------------------
// Приложение: окно запуска + трей + конечный автомат
// ---------------------------------------------------------------------------

internal sealed class VoiceApp : ApplicationContext
{
    private const int MinSamples = 16000 / 3;   // меньше 0.3 c — отмена

    private readonly Config _cfg = Config.Load();
    private readonly NotifyIcon _tray;
    private readonly OverlayForm _overlay = new();
    private readonly SplashForm _splash;
    private readonly MicRecorder _recorder = new();
    private readonly WhisperService _whisper;
    private readonly PttMonitor _ptt = new();
    private readonly Icon _icon;
    private readonly System.Windows.Forms.Timer _doneFlash = new() { Interval = 1200 };

    private string _state = "idle";             // idle | listening | transcribing
    private CancellationTokenSource? _transcribeCts;

    public VoiceApp()
    {
        Log.Info("Создание главного окна");
        _icon = LoadIcon();
        _whisper = new WhisperService(_cfg);
        _splash = new SplashForm(_icon);
        _splash.HideToTrayRequested += HideSplashToTray;
        _splash.MicTestRequested += RunMicTest;

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = "Art Whisper — готов (правая клавиша Ctrl)",
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseDoubleClick += (_, _) => ShowSplash();
        Log.Info("Иконка в трее создана");

        _ptt.Pressed += OnPttPressed;
        _ptt.Released += OnPttReleased;
        _ptt.Start();

        _doneFlash.Tick += (_, _) =>
        {
            _doneFlash.Stop();
            if (_state == "idle") _overlay.HideState();
        };

        _splash.LogLine($"Программа запущена. Модель распознавания: «{_cfg.Model}».");
        _splash.SetBusy(true);
        _splash.Show();

        // Предзагрузка (и при необходимости скачивание) модели в фоне
        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<string>(m =>
                {
                    _splash.LogLine(m);
                    Ui(() => _tray.Text = $"Art Whisper — {m}");
                });
                await ModelManager.EnsureModelAsync(_cfg.Model, progress);
                _whisper.Preload(progress);
                Ui(() =>
                {
                    _splash.MarkReady();
                    _tray.Text = "Art Whisper — готов (правая клавиша Ctrl)";
                });
                Log.Info("Готов к работе");
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка подготовки модели", ex);
                Ui(() =>
                {
                    _splash.SetBusy(false);
                    _splash.LogLine($"ОШИБКА: {ex.Message}");
                    _splash.LogLine("Попробуйте перезапустить программу. Подробности — в журнале.");
                    _tray.Text = "Art Whisper — ошибка загрузки модели";
                    Balloon("Ошибка загрузки модели", ex.Message, ToolTipIcon.Error);
                });
            }
        });
    }

    private static Icon LoadIcon()
    {
        var asm = typeof(VoiceApp).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("icon.ico"));
        if (name != null)
        {
            using var s = asm.GetManifestResourceStream(name)!;
            return new Icon(s);
        }
        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        string[] models = { "tiny", "base", "small", "medium" };

        menu.Items.Add("Показать окно", null, (_, _) => ShowSplash());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripLabel("Модель распознавания:"));
        foreach (var m in models)
        {
            var item = new ToolStripMenuItem(m) { CheckOnClick = true, Checked = _cfg.Model == m };
            var captured = m;
            item.Click += (_, _) =>
            {
                foreach (ToolStripMenuItem other in menu.Items.OfType<ToolStripMenuItem>())
                    if (other != item && models.Contains(other.Text))
                        other.Checked = false;
                item.Checked = true;
                _cfg.Model = captured;
                _cfg.Save();
                Balloon("Модель изменена", $"«{captured}» будет скачана в фоне.",
                    ToolTipIcon.Info);
                _ = Task.Run(async () =>
                {
                    try { await ModelManager.EnsureModelAsync(captured, null); }
                    catch (Exception ex)
                    {
                        Log.Error("Ошибка скачивания модели", ex);
                        Balloon("Ошибка", ex.Message, ToolTipIcon.Error);
                    }
                });
            };
            menu.Items.Add(item);
        }

        var paste = new ToolStripMenuItem("Авто-вставка текста") { Checked = _cfg.AutoPaste, CheckOnClick = true };
        paste.Click += (_, _) => { _cfg.AutoPaste = paste.Checked; _cfg.Save(); };
        menu.Items.Add(paste);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Создать ярлык на рабочем столе", null, (_, _) => CreateDesktopShortcut());
        menu.Items.Add("Открыть журнал", null, (_, _) => OpenLog());
        menu.Items.Add("Выход", null, (_, _) => Exit());

        return menu;
    }

    // ---- окно запуска --------------------------------------------------------

    private void ShowSplash()
    {
        _splash.Show();
        _splash.Activate();
    }

    private string RunMicTest()
    {
        try
        {
            Log.Info("Тест микрофона: старт");
            _recorder.Start();
            Thread.Sleep(3000);
            var samples = _recorder.Stop();
            float peak = 0f;
            foreach (var s in samples)
            {
                var a = Math.Abs(s);
                if (a > peak) peak = a;
            }
            int pct = (int)(peak * 100);
            Log.Info($"Тест микрофона: уровень {pct}%");
            if (pct < 1)
                return $"Уровень записи: {pct}% — микрофон МОЛЧИТ. " +
                       "Проверьте: Параметры → Конфиденциальность → Микрофон → доступ приложениям включён; " +
                       "микрофон не отключён в микшере; правильное устройство записи по умолчанию.";
            if (pct < 5)
                return $"Уровень записи: {pct}% — очень тихо. Поднимите громкость записи микрофона " +
                       "(Параметры → Система → Звук → свойства микрофона → громкость записи 100).";
            return $"Уровень записи: {pct}% — микрофон работает. Можно диктовать!";
        }
        catch (Exception ex)
        {
            Log.Error("Тест микрофона: ошибка", ex);
            return $"Ошибка микрофона: {ex.Message}";
        }
    }

    private void HideSplashToTray()
    {
        _splash.Hide();
        Balloon("Art Whisper работает",
            "Иконка — в трее, рядом с часами (возможно, в скрытых значках «^»). Зажмите правый Ctrl, чтобы продиктовать.",
            ToolTipIcon.Info);
    }

    private void OpenLog()
    {
        try
        {
            Log.Info("Открыт журнала пользователем");
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Balloon("Не удалось открыть журнал", ex.Message, ToolTipIcon.Error);
        }
    }

    // ---- прочее ---------------------------------------------------------------

    private void Balloon(string title, string text, ToolTipIcon icon)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(4000);
    }

    private void CreateDesktopShortcut()
    {
        try
        {
            dynamic wsh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string exePath = Application.ExecutablePath;
            string lnkPath = Path.Combine(desktop, "Art Whisper.lnk");
            dynamic lnk = wsh.CreateShortcut(lnkPath);
            lnk.TargetPath = exePath;
            lnk.WorkingDirectory = Path.GetDirectoryName(exePath);
            lnk.IconLocation = exePath;
            lnk.Description = "Art Whisper — голосовой ввод текста";
            lnk.Save();
            Balloon("Готово", $"Ярлык создан: {lnkPath}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Balloon("Не удалось создать ярлык", ex.Message, ToolTipIcon.Error);
        }
    }

    // ---- конечный автомат ---------------------------------------------------

    private void OnPttPressed()
    {
        if (_state != "idle") return;
        Ui(() =>
        {
            if (_state != "idle") return;
            try
            {
                _recorder.Start();
                SetState("listening");
            }
            catch (Exception ex)
            {
                Log.Error("Ошибка микрофона", ex);
                Balloon("Ошибка микрофона", ex.Message, ToolTipIcon.Error);
            }
        });
    }

    private void OnPttReleased()
    {
        if (_state != "listening") return;
        // Stop работает с потока PTT — быстро, не трогает UI
        float[] samples = _recorder.Stop();
        if (samples.Length < MinSamples)
        {
            Ui(() => SetState("idle"));
            return;
        }

        // Уровень записи: защита от «галлюцинаций» Whisper на тишине
        // (классика: «Редактор субтитров А.Синецкая, корректор…»)
        float peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        Log.Info($"Запись: {samples.Length / 16000.0:F1} с, уровень {peak * 100:F1}%");
        if (peak < 0.008f)
        {
            Ui(() =>
            {
                SetState("idle");
                Balloon("Микрофон молчит",
                    "В записи тишина — текст не вставлен.\n" +
                    "Проверьте: Параметры Windows → Конфиденциальность → Микрофон " +
                    "(доступ разрешён?), уровень записи, микрофон не отключён.\n" +
                    "Кнопка «Проверить микрофон» в окне Art Whisper.",
                    ToolTipIcon.Warning);
            });
            return;
        }

        // Нормализация громкости — спасает тихие микрофоны
        if (peak < 0.3f)
        {
            float gain = Math.Min(0.95f / peak, 100f);
            for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
        }
        Ui(() =>
        {
            SetState("transcribing");
            _transcribeCts = new CancellationTokenSource();
            var ct = _transcribeCts.Token;
            var progress = new Progress<string>(m =>
            {
                if (_state == "transcribing")
                    _tray.Text = $"Art Whisper — {m}";
            });
            Task.Run(async () =>
            {
                try
                {
                    string text = await _whisper.TranscribeAsync(samples, progress, ct);
                    Ui(() => FinishTranscribe(text));
                }
                catch (OperationCanceledException) { Ui(() => SetState("idle")); }
                catch (Exception ex)
                {
                    Log.Error("Ошибка распознавания", ex);
                    Ui(() =>
                    {
                        SetState("idle");
                        Balloon("Ошибка распознавания", ex.Message, ToolTipIcon.Error);
                    });
                }
            }, ct);
        });
    }

    private void FinishTranscribe(string text)
    {
        SetState("idle");
        if (string.IsNullOrWhiteSpace(text))
        {
            _overlay.HideState();
            return;
        }
        Clipboard.SetText(text);
        if (_cfg.AutoPaste)
        {
            Thread.Sleep(250);   // дать окну получить фокус после отпускания Ctrl
            SendKeys.SendWait("^v");
        }
        FlashDone();
    }

    private void SetState(string state)
    {
        _state = state;
        switch (state)
        {
            case "listening":
                _overlay.ShowDot(Color.FromArgb(0xD8, 0x32, 0x32), followMouse: true);
                _tray.Text = "Art Whisper — запись";
                break;
            case "transcribing":
                _overlay.ShowDot(Color.FromArgb(0x2E, 0x6B, 0xD6), followMouse: true);
                _tray.Text = "Art Whisper — распознаю";
                break;
            default:
                _doneFlash.Stop();
                _overlay.HideState();
                _tray.Text = "Art Whisper — готов (правая клавиша Ctrl)";
                break;
        }
    }

    /// <summary>Зелёная вспышка рядом с курсором: запись обработана, текст вставлен.</summary>
    private void FlashDone()
    {
        _overlay.ShowDot(Color.FromArgb(0x2E, 0xA8, 0x44), followMouse: true);
        _doneFlash.Stop();
        _doneFlash.Start();
    }

    private void Ui(Action action)
    {
        if (_overlay.IsDisposed || !_overlay.IsHandleCreated) { action(); return; }
        _overlay.BeginInvoke(action);
    }

    private void Exit()
    {
        _ptt.Stop();
        _transcribeCts?.Cancel();
        _doneFlash.Stop();
        _doneFlash.Dispose();
        _whisper.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        _splash.Dispose();
        Application.Exit();
    }
}
