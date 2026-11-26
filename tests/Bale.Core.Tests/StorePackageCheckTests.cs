using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>
/// CI's Store checks (docs/store-readiness.md): scripts/check-production-package.ps1 on every package, the Windows App
/// Certification Kit on production ones, production builds still only on request. The script's fixtures run in CI
/// (scripts/test-production-package-checks.ps1); here its lists are held to the sources they describe.
/// </summary>
public sealed partial class StorePackageCheckTests
{
    [Fact]
    public void EveryPackageIsCheckedAndProductionGetsAWackRunThatCannotFailTheJob()
    {
        var ci = Read("ci.yml.txt");
        Assert.Contains("./scripts/test-production-package-checks.ps1", Step(ci, "Test native package validation scripts"));
        var check = Step(ci, "Check the package against the Store rules");
        Assert.DoesNotContain("if:", check);
        Assert.Contains("./scripts/check-production-package.ps1 -PackagePath $package.FullName -DebugServer '${{ matrix.debug_server }}'", check);
        var wack = Step(ci, "Windows App Certification Kit (production)");
        Assert.Contains("if: matrix.debug_server == 'false'", wack);
        Assert.Contains("continue-on-error: true", wack);
        Assert.Contains("./scripts/run-wack.ps1 -PackagePath $package.FullName -ReportDirectory wack -CertificatePath out\\Bale.cer", wack);
        var upload = Step(ci, "Upload the WACK report");
        Assert.Contains("if: always() && matrix.debug_server == 'false' && steps.wack.outcome != 'skipped'", upload);
        Assert.Contains("name: wack-report", upload);
        // Production packages stay manual (owner: no routine production builds): pushes build the debug-server package.
        Assert.Contains("      production:\n        description: Also build the production (debug server off) package\n        type: boolean\n        default: false\n", ci);
        Assert.Contains("debug_server: ${{ fromJSON(inputs.production && '[\"true\", \"false\"]' || '[\"true\"]') }}", ci);
        var script = Read("run-wack.ps1.txt");
        Assert.Contains("'reset'", script);
        Assert.Contains("@('test', '-appxpackagepath',", script);
        Assert.Contains("'-reportoutputpath'", script);
    }

    [Fact]
    public void TheCapabilityAllowListIsTheSourceManifests()
    {
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var manifest = XDocument.Parse(Read("Package.appxmanifest"));
        var declared = manifest.Root!.Element(foundation + "Capabilities")!.Elements().Select(c => c.Attribute("Name")!.Value);
        var script = Read("check-production-package.ps1.txt");
        var list = script[script.IndexOf("$StoreCapabilities = @(", StringComparison.Ordinal)..];
        list = list[..list.IndexOf(')')];
        Assert.Equal(declared.Order(), Quoted().Matches(list).Select(match => match.Groups[1].Value).Order());
        Assert.Contains("$DebugCapability = 'inputInjectionBrokered'", script);
    }

    [Fact]
    public void EachDebugMarkerIsDebugServerCode()
    {
        var script = Read("check-production-package.ps1.txt");
        var markers = MarkerPattern().Matches(script).Select(match => (File: match.Groups[1].Value, Encoding: match.Groups[2].Value,
            Text: match.Groups[3].Value)).ToArray();
        Assert.Equal(5, markers.Length);
        foreach (var (file, encoding, text) in markers)
        {
            if (file == "Bale.exe")
            {
                // A route literal of the debug server, which only DEBUG_SERVER builds compile (DebugSourceGuardTests).
                Assert.Equal("utf-16", encoding);
                Assert.Contains($"\"{text}\"", Read("DebugServer.cs.txt"));
            }
            else
            {
                // Report text of a bale_gpu export the debug-probes feature gates (BaleGpuDebugExportTests).
                Assert.Equal(("bale_gpu.dll", "utf-8"), (file, encoding));
                var source = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "bale-gpu-src"), "*.rs", SearchOption.AllDirectories)
                    .Select(path => (Path: path, Text: File.ReadAllText(path))).Single(rust => rust.Text.Contains(text, StringComparison.Ordinal));
                var at = source.Text.IndexOf(text, StringComparison.Ordinal);
                var function = source.Text.LastIndexOf("\n    fn ", at, StringComparison.Ordinal) is var nested and >= 0
                    && nested > source.Text.LastIndexOf("\nfn ", at, StringComparison.Ordinal)
                    && nested > source.Text.LastIndexOf("extern \"C\" fn ", at, StringComparison.Ordinal)
                    ? nested : Math.Max(source.Text.LastIndexOf("\nfn ", at, StringComparison.Ordinal),
                        source.Text.LastIndexOf("extern \"C\" fn ", at, StringComparison.Ordinal));
                var attributes = source.Text[Math.Max(0, function - 200)..function];
                Assert.Contains("feature = \"debug-probes\"", attributes);
            }
        }
    }

    private static string Step(string workflow, string name)
    {
        var start = workflow.IndexOf($"- name: {name}", StringComparison.Ordinal);
        Assert.True(start >= 0, name);
        var end = workflow.IndexOf("\n      - ", start + 10, StringComparison.Ordinal);
        return end < 0 ? workflow[start..] : workflow[start..end];
    }

    [GeneratedRegex("'([A-Za-z]+)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"@\{ File = '([^']+)'; Encoding = '([^']+)'; Text = '([^']+)' \}")]
    private static partial Regex MarkerPattern();

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
