using System.IO;
using System.Text.Json;

namespace BeastClicker;

public sealed class AppConfig
{
    public double Hours { get; set; }
    public double Minutes { get; set; }
    public double Seconds { get; set; }
    public double Millis { get; set; } = 25;

    public ClickButton Button { get; set; } = ClickButton.Left;
    public bool DoubleClick { get; set; }

    public bool UseFixedPos { get; set; }
    public int FixedX { get; set; }
    public int FixedY { get; set; }

    public bool InfiniteRepeat { get; set; } = true;
    public int RepeatCount { get; set; } = 100;

    public bool AlwaysOnTop { get; set; }

    public string HkToggle { get; set; } = "F6";
    public string HkStart { get; set; } = "F7";
    public string HkStop { get; set; } = "F8";
    public string HkPick { get; set; } = "F9";

    public double IntervalMs =>
        Math.Max(Hours * 3_600_000 + Minutes * 60_000 + Seconds * 1000 + Millis, 0.1);

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private static string Path_ => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BeastClicker",
        "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path_)) ?? new AppConfig();
        }
        catch
        {
            // Corrupt or unreadable config should never block startup.
        }
        return new AppConfig();
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this, Opts));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Timing spread, as a fraction of the interval. Deliberately not exposed in
    /// the UI: the scheduler places deadlines on a fixed grid with zero-mean
    /// offsets, so this changes the rhythm without altering the achieved click
    /// rate, and there is no user decision worth surfacing.
    /// </summary>
    private const double InternalVariance = 0.18;

    public EngineSettings ToEngineSettings() => new()
    {
        IntervalMs = IntervalMs,
        Button = Button,
        DoubleClick = DoubleClick,
        Limit = InfiniteRepeat ? 0 : Math.Max(1, RepeatCount),
        UseFixedPos = UseFixedPos,
        FixedX = FixedX,
        FixedY = FixedY,
        Variance = InternalVariance,
        // Always zero: press and release then ship in one atomic SendInput call,
        // so no cursor movement can land between them and turn a click into a drag.
        HoldMs = 0,
    };
}
