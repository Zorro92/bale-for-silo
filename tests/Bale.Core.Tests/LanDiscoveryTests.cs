using Bale.Core.Connection;

namespace Bale.Core.Tests;

public class LanDiscoveryTests
{
    [Fact]
    public void TxtIdentityIsRequiredCaseInsensitiveAndTrimmed()
    {
        Assert.Equal("deployment", LanDiscoveryRules.ServerId(["v=2", "ID= deployment ", "other=value"]));
        Assert.Null(LanDiscoveryRules.ServerId(["name=Silo", "id= "]));
        Assert.Null(LanDiscoveryRules.ServerId(["version=2"]));
    }

    [Fact]
    public void NumericOriginsPreferIpv4RejectUnsafeAddressesAndBoundInterfaces()
    {
        var origins = LanDiscoveryRules.Origins([
            "2001:db8::1", "192.0.2.10", "192.0.2.10", "127.0.0.1", "0.0.0.0", "224.0.0.1", "255.255.255.255",
            "::1", "::", "fe80::1%3", "ff02::1", "silo.local", "2130706433", "::ffff:127.0.0.1", "192.0.2.11", "192.0.2.12", "192.0.2.13",
        ], 8090);
        Assert.Equal(4, origins.Count);
        Assert.Equal("http://192.0.2.10:8090", ServerAddress.ToBaseUrl(origins[0]));
        Assert.All(origins, origin => Assert.Contains("192.0.2.", origin.Host));
        Assert.Equal("http://[2001:db8::1]:8090", ServerAddress.ToBaseUrl(Assert.Single(LanDiscoveryRules.Origins(["2001:db8::1"], 8090))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidPortProducesNoCandidates(int port) => Assert.Empty(LanDiscoveryRules.Origins(["192.0.2.10"], port));

    [Fact]
    public void DuplicateDeploymentsPreferHealthyThenLowestUrlAndSortNamesIgnoringCase()
    {
        var rows = LanDiscoveryRules.Rows([
            (new DiscoveredServer("same", "Beta", new Uri("http://192.0.2.10:8090")), 2),
            (new DiscoveredServer("same", "Beta", new Uri("http://192.0.2.11:8090")), 0),
            (new DiscoveredServer("same", "Beta", new Uri("http://192.0.2.12:8090")), 0),
            (new DiscoveredServer("other", "alpha", new Uri("http://192.0.2.13:8090")), 0),
        ]);
        Assert.Equal(["other", "same"], rows.Select(row => row.ServerId));
        Assert.Equal("192.0.2.11", rows[1].Server.Host);
    }

    [Fact]
    public void ListedRowSurvivesTwoMissesDisappearsOnThirdAndSuccessResetsHealth()
    {
        var health = new LanDiscoveryHealth();
        var row = new DiscoveredServer("deployment", "Example", new Uri("http://192.0.2.10:8090"));
        health.Confirm(row);
        health.Confirm(null);
        health.Confirm(null);
        Assert.Same(row, health.Row);
        Assert.Equal(2, health.Misses);
        health.Confirm(row);
        Assert.Equal(0, health.Misses);
        health.Confirm(null);
        health.Confirm(null);
        health.Confirm(null);
        Assert.Null(health.Row);
    }

    [Fact]
    public void PreviewMutationsAreDisposableAndNeverShareARegistry()
    {
        var first = new ConnectionPreview();
        var second = new ConnectionPreview();
        var original = second.Servers.Snapshot;
        var http = new Uri("http://192.0.2.10:8090");
        first.Servers.ApproveHttp(http);
        first.Servers.AddAndActivate(http);
        foreach (var entry in first.Servers.Snapshot.Entries) first.Servers.Remove(entry.Id);
        Assert.Empty(first.Servers.Snapshot.Entries);
        Assert.Equal(original.Entries, second.Servers.Snapshot.Entries);
        Assert.Equal(original.ActiveServerId, second.Servers.Active!.Id);
        Assert.True(second.Servers.RequiresHttpApproval(http));
    }
}
