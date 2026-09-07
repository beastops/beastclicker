using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BeastClicker;

public partial class MainWindow : Window
{
    private readonly ClickEngine _engine = new();
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _run = new();

    private AppConfig _cfg = new();
    private HotkeyManager? _hotkeys;

    /// <summary>Non-null while waiting for the user to press a key to rebind.</summary>
    private string? _capturing;
    private bool _loading = true;

    private static readonly Regex NumericOnly = new(@"^[0-9]*$", RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();

        _engine.Finished += () => Dispatcher.Invoke(OnEngineFinished);
        _engine.Error += m => Dispatcher.Invoke(() => Flash($"Engine error: {m}"));
        _ui.Tick += (_, _) => RefreshStats();

        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;

        foreach (var tb in new[] { TbHours, TbMinutes, TbSeconds, TbMillis, TbRepeat, TbX, TbY })
        {
            tb.PreviewTextInput += (_, e) => e.Handled = !NumericOnly.IsMatch(e.Text);
            tb.TextChanged += (_, _) => { if (!_loading) UpdateCps(); };
            DataObject.AddPastingHandler(tb, (_, e) => e.CancelCommand());
        }
    }

    /* ------------------------------ lifecycle ------------------------------ */

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cfg = AppConfig.Load();
        ApplyConfigToUi(_cfg);
        _loading = false;

        _hotkeys = new HotkeyManager(this);
        RegisterHotkeys();

        UpdateCps();
        SetRunningVisual(false);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _engine.Dispose();
        _hotkeys?.Dispose();
    }

    /* --------------------------- config <-> UI ----------------------------- */

    private void ApplyConfigToUi(AppConfig c)
    {
        TbHours.Text = Fmt(c.Hours);
        TbMinutes.Text = Fmt(c.Minutes);
        TbSeconds.Text = Fmt(c.Seconds);
        TbMillis.Text = Fmt(c.Millis);
        TbRepeat.Text = c.RepeatCount.ToString(CultureInfo.InvariantCulture);
        TbX.Text = c.FixedX.ToString(CultureInfo.InvariantCulture);
        TbY.Text = c.FixedY.ToString(CultureInfo.InvariantCulture);

        CbButton.SelectedIndex = c.Button switch
        {
            ClickButton.Right => 1,
            ClickButton.Middle => 2,
            _ => 0,
        };
        CbType.SelectedIndex = c.DoubleClick ? 1 : 0;

        RbInfinite.IsChecked = c.InfiniteRepeat;
        RbFixed.IsChecked = !c.InfiniteRepeat;

        RbCurrent.IsChecked = !c.UseFixedPos;
        RbFixedPos.IsChecked = c.UseFixedPos;

        ChkTop.IsChecked = c.AlwaysOnTop;
        Topmost = c.AlwaysOnTop;

        BtnHkToggle.Content = Show(c.HkToggle);
        BtnHkStart.Content = Show(c.HkStart);
        BtnHkStop.Content = Show(c.HkStop);
        BtnHkPick.Content = Show(c.HkPick);
    }

    private static string Show(string s) => string.IsNullOrEmpty(s) ? "—" : s;

    private static string Fmt(double v) =>
        v == Math.Floor(v)
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString(CultureInfo.InvariantCulture);

    private static double Read(TextBox tb) =>
        double.TryParse(tb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void PullUiIntoConfig()
    {
        _cfg.Hours = Read(TbHours);
        _cfg.Minutes = Read(TbMinutes);
        _cfg.Seconds = Read(TbSeconds);
        _cfg.Millis = Read(TbMillis);
        _cfg.RepeatCount = Math.Max(1, (int)Read(TbRepeat));
        _cfg.FixedX = (int)Read(TbX);
        _cfg.FixedY = (int)Read(TbY);

        _cfg.Button = CbButton.SelectedIndex switch
        {
            1 => ClickButton.Right,
            2 => ClickButton.Middle,
            _ => ClickButton.Left,
        };
        _cfg.DoubleClick = CbType.SelectedIndex == 1;
        _cfg.InfiniteRepeat = RbInfinite.IsChecked == true;
        _cfg.UseFixedPos = RbFixedPos.IsChecked == true;
        _cfg.AlwaysOnTop = ChkTop.IsChecked == true;
    }

    private void UpdateCps()
    {
        double ms = Math.Max(
            Read(TbHours) * 3_600_000 + Read(TbMinutes) * 60_000
            + Read(TbSeconds) * 1000 + Read(TbMillis),
            0.1);
        double cps = 1000.0 / ms;
        CpsPreview.Text = cps >= 100 ? $"{cps:N0} clicks/sec" : $"{cps:N1} clicks/sec";

        // Past a few hundred per second the bottleneck stops being us and becomes
        // the target application's input queue — games visibly stutter long before
        // 1000/sec. Flag it rather than let the number look free.
        CpsPreview.Foreground = cps > 200
            ? new SolidColorBrush(Color.FromRgb(0xB0, 0x5A, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    }

    /* ------------------------------- hotkeys ------------------------------- */

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterAll();

        var failed = new List<string>();
        void Bind(string spec, Action act)
        {
            if (string.IsNullOrWhiteSpace(spec)) return;
            if (!_hotkeys!.Register(spec, act)) failed.Add(spec);
        }

        Bind(_cfg.HkToggle, Toggle);
        Bind(_cfg.HkStart, StartClicking);
        Bind(_cfg.HkStop, StopClicking);
        Bind(_cfg.HkPick, CapturePickLocation);

        if (failed.Count > 0)
        {
            LblWarn.Text = $"Windows refused {string.Join(", ", failed)} — another app already owns them.";
            LblWarn.Visibility = Visibility.Visible;
        }
        else
        {
            LblWarn.Visibility = Visibility.Collapsed;
        }
    }

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        _capturing = (string)b.Tag;
        b.Content = "Press a key…";
        // Global registrations would swallow the very keys we want to capture.
        _hotkeys?.UnregisterAll();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is null) return;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _capturing = null;
            ApplyConfigToUi(_cfg);
            RegisterHotkeys();
            return;
        }

        // Alt-combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var spec = HotkeyManager.Describe(key, Keyboard.Modifiers);
        if (spec is null) return;

        // A combo can only drive one action, so clear any prior owner.
        if (_cfg.HkToggle == spec) _cfg.HkToggle = "";
        if (_cfg.HkStart == spec) _cfg.HkStart = "";
        if (_cfg.HkStop == spec) _cfg.HkStop = "";
        if (_cfg.HkPick == spec) _cfg.HkPick = "";

        switch (_capturing)
        {
            case "toggle": _cfg.HkToggle = spec; break;
            case "start": _cfg.HkStart = spec; break;
            case "stop": _cfg.HkStop = spec; break;
            case "pick": _cfg.HkPick = spec; break;
        }

        _capturing = null;
        BtnHkToggle.Content = Show(_cfg.HkToggle);
        BtnHkStart.Content = Show(_cfg.HkStart);
        BtnHkStop.Content = Show(_cfg.HkStop);
        BtnHkPick.Content = Show(_cfg.HkPick);
        RegisterHotkeys();
    }

    /* ------------------------------ position ------------------------------- */

    private void BtnPick_Click(object sender, RoutedEventArgs e) =>
        Flash($"Hover the target, then press {Show(_cfg.HkPick)}");

    private void CapturePickLocation()
    {
        var p = ClickEngine.GetCursor();
        Dispatcher.Invoke(() =>
        {
            TbX.Text = p.X.ToString(CultureInfo.InvariantCulture);
            TbY.Text = p.Y.ToString(CultureInfo.InvariantCulture);
            RbFixedPos.IsChecked = true;
            Flash($"Target set to {p.X}, {p.Y}");
        });
    }

    /* -------------------------------- run ---------------------------------- */

    private void BtnStart_Click(object sender, RoutedEventArgs e) => StartClicking();
    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopClicking();

    /// <summary>Hotkey-only: a toggle button would just duplicate whichever of
    /// Start/Stop is currently enabled.</summary>
    private void Toggle()
    {
        if (_engine.IsRunning) StopClicking();
        else StartClicking();
    }

    private void StartClicking() => Dispatcher.Invoke(() =>
    {
        if (_engine.IsRunning) return;
        PullUiIntoConfig();
        _run.Restart();
        _engine.Start(_cfg.ToEngineSettings());
        _ui.Start();
        SetRunningVisual(true);
    });

    private void StopClicking() => _engine.Stop();

    private void OnEngineFinished()
    {
        _ui.Stop();
        _run.Stop();
        RefreshStats();
        SetRunningVisual(false);
    }

    private void SetRunningVisual(bool running)
    {
        BtnStart.IsEnabled = !running;
        BtnStop.IsEnabled = running;
        LblState.Text = running ? "Running" : "Stopped";
    }

    private void RefreshStats()
    {
        long n = _engine.ClickCount;
        double secs = _run.Elapsed.TotalSeconds;
        double cps = secs > 0.001 ? n / secs : 0;
        LblStats.Text = n == 0
            ? "0 clicks"
            : $"{n:N0} clicks   {(cps >= 100 ? cps.ToString("N0") : cps.ToString("N1"))}/sec   {secs:N1}s";
    }

    /* ------------------------------- buttons ------------------------------- */

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        PullUiIntoConfig();
        Flash(_cfg.Save() ? "Settings saved" : "Could not save settings");
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        _cfg = new AppConfig
        {
            HkToggle = _cfg.HkToggle,
            HkStart = _cfg.HkStart,
            HkStop = _cfg.HkStop,
            HkPick = _cfg.HkPick,
        };
        _loading = true;
        ApplyConfigToUi(_cfg);
        _loading = false;
        UpdateCps();
        Flash("Settings reset");
    }

    private void ChkTop_Click(object sender, RoutedEventArgs e) => Topmost = ChkTop.IsChecked == true;

    /* -------------------------------- toast -------------------------------- */

    private DispatcherTimer? _toast;

    private void Flash(string msg)
    {
        LblWarn.Text = msg;
        LblWarn.Visibility = Visibility.Visible;

        _toast?.Stop();
        _toast = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _toast.Tick += (_, _) =>
        {
            _toast!.Stop();
            LblWarn.Visibility = Visibility.Collapsed;
        };
        _toast.Start();
    }
}
