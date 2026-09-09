using BeastClicker.Tools;

// Build tooling for the repository: icon, social card, README banner, demo
// capture and the MSIX package. Each was a PowerShell script; they are one
// console app now so the repo is C# throughout and the shared GIF encoder is
// compiled rather than dot-sourced.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        Usage: BeastClicker.Tools <command> [options]

          icon              regenerate src/BeastClicker/Assets/app.ico and docs/icon.png
          social            regenerate docs/social-preview.png
          banner            regenerate docs/banner.gif
          demo              record docs/demo.gif from the running app
          msix              build and sign dist/BeastClicker.msix

        Run from anywhere in the repository.
        """);
    return args.Length == 0 ? 1 : 0;
}

string root = FindRepoRoot();

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "icon":
            IconGenerator.Run(root);
            break;
        case "social":
            SocialCard.Run(root);
            break;
        case "banner":
            Banner.Run(root);
            break;
        case "demo":
            DemoCapture.Run(root);
            break;
        case "msix":
            MsixPackager.Run(root);
            break;
        default:
            Console.Error.WriteLine($"unknown command '{args[0]}' — try --help");
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

return 0;

// Walks up from the binary rather than taking a path argument, so the tools work
// the same whether invoked by `dotnet run` or from the build output directory.
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "BeastClicker.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("could not locate BeastClicker.sln above the tools binary");
}
