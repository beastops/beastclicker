namespace BeastClicker.Tools;

/// <summary>
/// These tools were ported from PowerShell, and the two languages disagree about
/// what casting a double to an int means. PowerShell's [int] goes through
/// Convert.ToInt32 and rounds half-to-even; C#'s (int) truncates toward zero.
///
/// The difference is not cosmetic. The icon's plate inset is [int](256 * 0.045):
/// 12 under PowerShell, 11 under a C# cast, which shifts the entire plate by a
/// pixel. The drop shadows stack alphas of [int](2 + n * 0.9), so truncating
/// changes every layer. Ported arithmetic therefore goes through <see cref="Int"/>
/// rather than a cast, so the generated assets stay identical to the ones the
/// scripts produced.
/// </summary>
internal static class Ps
{
    /// <summary>PowerShell's [int] conversion: round half to even.</summary>
    public static int Int(double v) => (int)Math.Round(v, MidpointRounding.ToEven);
}
