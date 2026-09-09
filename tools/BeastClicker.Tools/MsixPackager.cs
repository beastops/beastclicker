using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace BeastClicker.Tools;

/// <summary>
/// Builds dist/BeastClicker.msix.
///
/// IMPORTANT, and stated up front because it changes how useful this package is:
/// an MSIX must be signed by a certificate the target machine already trusts.
/// This signs with a SELF-SIGNED certificate, which means a user cannot simply
/// double-click the .msix — they must first install the accompanying .cer into
/// Local Machine > Trusted People. Asking strangers to trust a certificate is a
/// real security ask, so the portable .exe stays the recommended download.
/// Shipping an MSIX that installs cleanly for everyone requires a certificate
/// from a trusted CA (or Microsoft Store signing).
///
/// The certificate is created in the CURRENT USER's personal store only. Nothing
/// is added to any machine-wide trust store.
/// </summary>
public static class MsixPackager
{
    private const string Publisher = "CN=beastops";
    private const string PublisherDisplay = "beastops";

    public static void Run(string repoRoot)
    {
        string version = ReadVersion(repoRoot);
        Console.WriteLine($"version {version} (read from BeastClicker.csproj)");

        var makeappx = FindSdkTool("makeappx.exe")
            ?? throw new InvalidOperationException("makeappx.exe not found — install the Windows SDK.");
        string signtool = Path.Combine(Path.GetDirectoryName(makeappx)!, "signtool.exe");
        Console.WriteLine($"SDK: {Path.GetDirectoryName(makeappx)}");

        string stage = Path.Combine(repoRoot, "build", "msix");
        string assets = Path.Combine(stage, "Assets");
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        Directory.CreateDirectory(assets);

        // ---- app payload ----
        Console.WriteLine("publishing app into the package layout...");
        Exec("dotnet", $"publish \"{Path.Combine(repoRoot, "src", "BeastClicker")}\" " +
                       "-c Release -r win-x64 --self-contained false " +
                       $"-p:PublishSingleFile=true -o \"{stage}\"", repoRoot);
        string pdb = Path.Combine(stage, "BeastClicker.pdb");
        if (File.Exists(pdb)) File.Delete(pdb);

        // ---- tile assets, scaled from the master icon ----
        using (var src = Image.FromFile(Path.Combine(repoRoot, "docs", "icon.png")))
        {
            SaveTile(src, assets, 44, 44, "Square44x44Logo.png", 0.86);
            SaveTile(src, assets, 150, 150, "Square150x150Logo.png", 0.62);
            SaveTile(src, assets, 310, 150, "Wide310x150Logo.png", 0.62);
            SaveTile(src, assets, 50, 50, "StoreLogo.png", 0.86);
            // Windows also looks for the targetsize variant of the small tile
            SaveTile(src, assets, 44, 44, "Square44x44Logo.targetsize-44_altform-unplated.png", 0.86);
        }

        // ---- manifest ----
        File.WriteAllText(Path.Combine(stage, "AppxManifest.xml"), Manifest(version), new UTF8Encoding(false));

        // ---- pack ----
        string distDir = Path.Combine(repoRoot, "dist");
        Directory.CreateDirectory(distDir);
        string msix = Path.Combine(distDir, "BeastClicker.msix");
        if (File.Exists(msix)) File.Delete(msix);

        Console.WriteLine("packing...");
        Exec(makeappx, $"pack /d \"{stage}\" /p \"{msix}\" /o", repoRoot);
        if (!File.Exists(msix)) throw new InvalidOperationException("makeappx failed");

        // ---- sign ----
        var cert = FindOrCreateCert();
        Console.WriteLine($"signing with {cert.Thumbprint}...");
        Exec(signtool, $"sign /fd SHA256 /sha1 {cert.Thumbprint} " +
                       $"/t http://timestamp.digicert.com \"{msix}\"", repoRoot);

        // Export the public certificate so a user can choose to trust it.
        string cerPath = Path.Combine(distDir, "BeastClicker.cer");
        File.WriteAllBytes(cerPath, cert.Export(X509ContentType.Cert));

        Console.WriteLine($"\nwrote {msix} ({new FileInfo(msix).Length / 1024.0:N0} KB)");
        Console.WriteLine($"wrote {cerPath}  (must be installed to Trusted People before the MSIX will install)");
    }

    /// <summary>
    /// The package version follows the csproj rather than being repeated here. A
    /// second copy of the number is how a release ends up shipping an MSIX still
    /// labelled with the previous version. MSIX wants four parts and the project
    /// gives three, so the revision is pinned at 0.
    /// </summary>
    private static string ReadVersion(string repoRoot)
    {
        string csproj = Path.Combine(repoRoot, "src", "BeastClicker", "BeastClicker.csproj");
        var m = Regex.Match(File.ReadAllText(csproj), @"<Version>\s*([0-9]+(?:\.[0-9]+){1,2})\s*</Version>");
        if (!m.Success) throw new InvalidOperationException($"could not read <Version> from {csproj}");
        var parts = m.Groups[1].Value.Split('.').ToList();
        while (parts.Count < 4) parts.Add("0");
        return string.Join('.', parts);
    }

    private static string? FindSdkTool(string name)
    {
        const string kits = @"C:\Program Files (x86)\Windows Kits\10\bin";
        if (!Directory.Exists(kits)) return null;
        var all = Directory.EnumerateFiles(kits, name, SearchOption.AllDirectories).ToList();
        return all.Where(p => p.Contains(@"\x64\", StringComparison.OrdinalIgnoreCase))
                  .OrderByDescending(p => p, StringComparer.Ordinal)
                  .FirstOrDefault()
            ?? all.OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault();
    }

    private static void SaveTile(Image src, string assets, int w, int h, string name, double fill)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            int side = Ps.Int(Math.Min(w, h) * fill);
            g.DrawImage(src, Ps.Int((w - side) / 2.0), Ps.Int((h - side) / 2.0), side, side);
        }
        bmp.Save(Path.Combine(assets, name), ImageFormat.Png);
    }

    private static string Manifest(string version) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package
          xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
          xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
          xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
          IgnorableNamespaces="uap rescap">

          <Identity Name="BeastOps.BeastClicker"
                    Publisher="{Publisher}"
                    Version="{version}"
                    ProcessorArchitecture="x64" />

          <Properties>
            <DisplayName>Beast Clicker</DisplayName>
            <PublisherDisplayName>{PublisherDisplay}</PublisherDisplayName>
            <Logo>Assets\StoreLogo.png</Logo>
            <Description>A precise, low-overhead auto clicker for Windows.</Description>
          </Properties>

          <Dependencies>
            <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>

          <Resources>
            <Resource Language="en-us" />
          </Resources>

          <Applications>
            <Application Id="BeastClicker" Executable="BeastClicker.exe" EntryPoint="Windows.FullTrustApplication">
              <uap:VisualElements
                DisplayName="Beast Clicker"
                Description="A precise, low-overhead auto clicker for Windows."
                BackgroundColor="transparent"
                Square150x150Logo="Assets\Square150x150Logo.png"
                Square44x44Logo="Assets\Square44x44Logo.png">
                <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
              </uap:VisualElements>
            </Application>
          </Applications>

          <Capabilities>
            <!-- Desktop app packaged as MSIX: needs full trust to synthesise input. -->
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;

    /// <summary>
    /// Reuses an existing self-signed certificate for <see cref="Publisher"/> or
    /// creates one. Replaces New-SelfSignedCertificate; the private key has to be
    /// persisted into the user's store or signtool cannot find it by thumbprint.
    /// </summary>
    private static X509Certificate2 FindOrCreateCert()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Where(c => c.Subject == Publisher && c.HasPrivateKey)
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
        if (existing is not null) return existing;

        Console.WriteLine($"creating a self-signed code-signing certificate for {Publisher} (current user store only)...");

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(Publisher, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, false));   // code signing
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        using var ephemeral = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // CreateSelfSigned hands back an ephemeral key. Round-tripping through a
        // PFX is what attaches a persisted key container, which is what signtool
        // needs to sign by thumbprint.
        string pw = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pfx = ephemeral.Export(X509ContentType.Pfx, pw);
        var persisted = X509CertificateLoader.LoadPkcs12(
            pfx, pw,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

        store.Add(persisted);
        return persisted;
    }

    private static void Exec(string file, string args, string workingDir)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {file}");

        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        // Only the tail is interesting: dotnet publish and makeappx are both very
        // chatty on success and the failure text is always at the end.
        foreach (var line in Tail(stdout, 3)) Console.WriteLine(line);
        if (p.ExitCode != 0)
        {
            foreach (var line in Tail(stderr, 5)) Console.Error.WriteLine(line);
            throw new InvalidOperationException($"{Path.GetFileName(file)} exited with {p.ExitCode}");
        }
    }

    private static IEnumerable<string> Tail(string s, int n) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
         .Select(l => l.TrimEnd('\r'))
         .Where(l => l.Trim().Length > 0)
         .TakeLast(n);
}
