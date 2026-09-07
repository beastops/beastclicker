using System.Windows;

namespace BeastClicker;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        // Never leave a synthetic button latched down.
        ClickEngine.ReleaseAllButtons();
        base.OnExit(e);
    }
}
