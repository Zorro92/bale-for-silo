using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;

namespace Bale.Core.Tests;

/// <summary>
/// Microsoft Store items in the package manifest and the build (docs/store-readiness.md): Xbox as the only device family,
/// the Store's capabilities alone in production (the debug server's input injection only in debug-server builds), the
/// debug server as an opt-in, production's revision 0 and the client channel each build reports to Silo.
/// </summary>
public sealed class StoreManifestTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    [Fact]
    public void ThePackageTargetsXboxAloneAtTheProjectsVersions()
    {
        var family = Assert.Single(XDocument.Parse(Read("Package.appxmanifest")).Descendants(Foundation + "TargetDeviceFamily"));
        Assert.Equal("Windows.Xbox", family.Attribute("Name")!.Value);
        // A family other than Windows.Universal takes nothing from the project, so the manifest repeats its versions;
        // Windows.Xbox needs at least 10.0.14393.0 (TargetDeviceFamily schema docs).
        var project = XDocument.Parse(Read("Bale.Xbox.csproj.txt"));
        var min = family.Attribute("MinVersion")!.Value;
        Assert.Equal(project.Descendants("TargetPlatformMinVersion").Single().Value, min);
        Assert.True(Version.Parse(min) >= new Version(10, 0, 14393, 0));
        Assert.EndsWith("-windows" + family.Attribute("MaxVersionTested")!.Value, project.Descendants("TargetFramework").Single().Value);
    }

    [Fact]
    public void TheSourceManifestDeclaresOnlyTheStoreCapabilities()
    {
        var manifest = XDocument.Parse(Read("Package.appxmanifest"));
        Assert.Equal(StoreCapabilities, Capabilities(manifest));
        Assert.DoesNotContain(manifest.Descendants(), element => element.Attribute("Name")?.Value == "inputInjectionBrokered");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("True")]
    public void AProductionTransformKeepsTheStoreCapabilities(string? debugServer)
    {
        // The stylesheet compares exactly; the project hands it a normalised 'true' or 'false' (below).
        var generated = Transform(debugServer);
        Assert.Equal(StoreCapabilities, Capabilities(generated));
        Assert.True(XNode.DeepEquals(XDocument.Parse(Read("Package.appxmanifest")).Root!.Element(Foundation + "Capabilities"),
            generated.Root!.Element(Foundation + "Capabilities")));
    }

    [Fact]
    public void TheDebugServerTransformAppendsInputInjectionAndChangesNothingElse()
    {
        var debug = Transform("true");
        Assert.Equal([.. StoreCapabilities, (Rescap, "inputInjectionBrokered")], Capabilities(debug));
        var production = Transform("false");
        foreach (var name in new[] { "Identity", "Dependencies", "Applications", "Extensions" })
            Assert.True(XNode.DeepEquals(production.Root!.Element(Foundation + name), debug.Root!.Element(Foundation + name)), name);
        // The capability reuses the root's rescap prefix instead of declaring it again.
        var injection = debug.Descendants(Rescap + "Capability").Last();
        Assert.DoesNotContain(injection.Attributes(), attribute => attribute.IsNamespaceDeclaration);
    }

    [Fact]
    public void TheDebugServerIsOptInAndDrivesTheManifest()
    {
        var project = XDocument.Parse(Read("Bale.Xbox.csproj.txt"));
        var setting = project.Descendants("BaleDebugServer").Single();
        Assert.Equal("'$(BaleDebugServer)' == ''", setting.Attribute("Condition")!.Value);
        Assert.Equal("false", setting.Value);
        var target = project.Descendants("Target").Single(t => t.Attribute("Name")!.Value == "PrepareFFmpegManifest");
        var flags = target.Descendants("_BaleManifestDebugServer").ToArray();
        Assert.Equal(("false", null), (flags[0].Value, flags[0].Attribute("Condition")?.Value));
        Assert.Equal(("true", "'$(BaleDebugServer)' == 'true'"), (flags[1].Value, flags[1].Attribute("Condition")?.Value));
        var transform = target.Descendants("XslTransformation").Single();
        Assert.Equal("<Parameter Name='debugServer' Value='$(_BaleManifestDebugServer)' />", transform.Attribute("Parameters")!.Value);
        // CI names the build in every msbuild call, so neither matrix entry leans on the default.
        var ci = Read("ci.yml.txt");
        Assert.Equal(2, ci.Split("/p:BaleDebugServer=${{ matrix.debug_server }}").Length - 1);
        Assert.Contains("-p:BaleDebugServer=$debug_server", Read("check-cswinrt.sh"));
    }

    [Fact]
    public void ProductionPackagesStampRevisionZero()
    {
        var ci = Read("ci.yml.txt");
        var stamp = ci[ci.IndexOf("- name: Stamp version", StringComparison.Ordinal)..];
        stamp = stamp[..stamp.IndexOf("- name:", 10, StringComparison.Ordinal)];
        Assert.Contains("$revision = if ('${{ matrix.debug_server }}' -eq 'true') { [int]'${{ github.run_attempt }}' - 1 } else { 0 }", stamp);
        Assert.Contains("$manifest.Package.Identity.Version = \"0.2.${{ github.run_number }}.$revision\"", stamp);
    }

    [Fact]
    public void StoreBuildsReportTheReleaseChannelAndDebugBuildsSideload()
    {
        // silo-server stores the channel verbatim and leaves "release" out of its labels (docs/settings-api.md); the official
        // clients send "release" from their stores.
        var source = Read("DeviceIdentity.cs.txt");
        Assert.Contains("internal const string Channel = \"sideload\";", source);
        var release = Rav1gReleaseTests.Release("DeviceIdentity.cs.txt");
        Assert.Contains("internal const string Channel = \"release\";", release);
        Assert.DoesNotContain("sideload\";", release);
        Assert.Contains("ClientChannel: Channel,", release);
    }

    private static readonly (XNamespace, string)[] StoreCapabilities =
        [(Foundation, "internetClient"), (Foundation, "privateNetworkClientServer"), (Rescap, "hevcPlayback")];

    private static (XNamespace, string)[] Capabilities(XDocument manifest) =>
        [.. manifest.Root!.Element(Foundation + "Capabilities")!.Elements().Select(capability =>
            (capability.Name.Namespace, capability.Attribute("Name")!.Value))];

    private static XDocument Transform(string? debugServer)
    {
        var transform = new XslCompiledTransform();
        using (var stylesheet = XmlReader.Create(Path.Combine(AppContext.BaseDirectory, "FFmpegManifest.xslt"))) transform.Load(stylesheet);
        var arguments = new XsltArgumentList();
        if (debugServer is not null) arguments.AddParam("debugServer", "", debugServer);
        using var output = new StringWriter();
        using (var input = XDocument.Parse(Read("Package.appxmanifest")).CreateReader()) transform.Transform(input, arguments, output);
        return XDocument.Parse(output.ToString());
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
