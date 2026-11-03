using Bale.Core.Connection;
using Windows.Devices.Enumeration;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Bale.Xbox.Services;

/// <summary>
/// LanServerDiscovery.kt using UWP's supported AssociationEndpointService enumeration, not DnssdServiceWatcher.
/// All mutable browser state is dispatcher-owned; generation checks reject callbacks from stopped visits/browses.
/// </summary>
internal sealed class LanServerDiscovery : IDisposable
{
    private const string Addresses = "System.Devices.IpAddress";
    private const string Port = "System.Devices.Dnssd.PortNumber";
    private const string Attributes = "System.Devices.Dnssd.TextAttributes";
    private const string Selector = "System.Devices.AepService.ProtocolId:={4526e8c1-8aac-4153-9b16-55e86ada0e54} AND System.Devices.Dnssd.Domain:=\"local\" AND System.Devices.Dnssd.ServiceName:=\"_silo._tcp\"";
    private readonly CoreDispatcher _dispatcher;
    private readonly Action<IReadOnlyList<DiscoveredServer>> _publish;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _slots = new(4);
    private readonly ServerSetupProbe _probe = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, DeviceInformation> _devices = [];
    private readonly Dictionary<string, Entry> _entries = [];
    private DeviceWatcher? _watcher;
    private int _generation;
    private bool _started;
    private bool _failed;
    private bool _quickRetryUsed;

    private sealed class Entry(string serverId, string name, IReadOnlyList<Uri> origins, CancellationToken lifetime)
    {
        public string ServerId { get; } = serverId;
        public string Name { get; } = name;
        public IReadOnlyList<Uri> Origins { get; } = origins;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public LanDiscoveryHealth Health { get; set; } = new();
        public bool Busy { get; set; }
    }

    public LanServerDiscovery(CoreDispatcher dispatcher, Action<IReadOnlyList<DiscoveredServer>> publish)
    {
        _dispatcher = dispatcher;
        _publish = publish;
        _refresh.Tick += OnRefresh;
    }

    public void Start()
    {
        if (_started || _lifetime.IsCancellationRequested) return;
        _started = true;
        StartBrowse();
        _refresh.Start();
    }

    private void StartBrowse()
    {
        var generation = ++_generation;
        _failed = false;
        try
        {
            var watcher = DeviceInformation.CreateWatcher(Selector, [Addresses, Port, Attributes], DeviceInformationKind.AssociationEndpointService);
            _watcher = watcher;
            watcher.Added += (_, info) => Post(generation, () => { _devices[info.Id] = info; Resolve(info); });
            watcher.Updated += (_, update) => Post(generation, () =>
            {
                if (!_devices.TryGetValue(update.Id, out var info)) return;
                info.Update(update);
                Resolve(info);
            });
            watcher.Removed += (_, update) => Post(generation, () =>
            {
                _devices.Remove(update.Id);
                if (_entries.Remove(update.Id, out var entry)) entry.Cancellation.Cancel();
                Publish();
            });
            watcher.EnumerationCompleted += (_, _) => Post(generation, () => Log.Info("LAN browse enumeration complete"));
            watcher.Stopped += (_, _) => Post(generation, () =>
            {
                if (watcher.Status == DeviceWatcherStatus.Aborted) BrowseFailed();
            });
            watcher.Start();
            Log.Info("LAN browse started service=silo");
        }
        catch (Exception ex)
        {
            Log.Info($"LAN browse unavailable type={ex.GetType().Name}");
            BrowseFailed();
        }
    }

    private void Post(int generation, Action action)
    {
        _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
        {
            if (_lifetime.IsCancellationRequested || generation != _generation) return;
            try { action(); }
            catch (Exception ex) { Log.Info($"LAN browse event ignored type={ex.GetType().Name}"); }
        });
    }

    private void Resolve(DeviceInformation info)
    {
        // Property names/types are documented by Microsoft's PKEY_Devices_Dnssd_* references.
        var id = info.Properties.TryGetValue(Attributes, out var txt) && txt is IEnumerable<string> attributes ? LanDiscoveryRules.ServerId(attributes) : null;
        var port = info.Properties.TryGetValue(Port, out var value) && value is ushort number ? number : 0;
        IReadOnlyList<Uri> origins = info.Properties.TryGetValue(Addresses, out var ips) && ips is IEnumerable<string> addresses ? LanDiscoveryRules.Origins(addresses, port) : [];
        if (id is null)
        {
            if (_entries.Remove(info.Id, out var removed)) removed.Cancellation.Cancel();
            Publish();
            return;
        }
        if (origins.Count == 0) return;
        _entries.TryGetValue(info.Id, out var old);
        if (old is not null && old.ServerId == id && old.Name == info.Name && old.Origins.SequenceEqual(origins)) return;
        old?.Cancellation.Cancel();
        var entry = new Entry(id, info.Name, origins, _lifetime.Token);
        if (old?.ServerId == id && old.Health.Row is { } row && origins.Contains(row.Server)) entry.Health = old.Health;
        _entries[info.Id] = entry;
        Publish();
        _ = ConfirmAsync(info.Id, entry);
    }

    private bool Current(string key, Entry entry) => !_lifetime.IsCancellationRequested && !entry.Cancellation.IsCancellationRequested
        && _entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry);

    private async Task ConfirmAsync(string key, Entry entry)
    {
        if (entry.Busy || !Current(key, entry)) return;
        entry.Busy = true;
        var cancellation = entry.Cancellation.Token;
        try
        {
            // A newly found/unlisted row gets two attempts; listed rows count one missed refresh at a time.
            var attempts = entry.Health.Row is null ? 2 : 1;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
                await _slots.WaitAsync(cancellation);
                DiscoveredServer? confirmed = null;
                try
                {
                    foreach (var origin in entry.Origins)
                    {
                        string? identity;
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                        {
                            deadline.CancelAfter(TimeSpan.FromSeconds(4));
                            try { identity = await _probe.IdentityAsync(origin, deadline.Token); }
                            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { identity = null; }
                        }
                        if (identity?.Trim() != entry.ServerId) continue;
                        string? name = null;
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                        {
                            deadline.CancelAfter(TimeSpan.FromSeconds(4));
                            try { name = await _probe.NameAsync(origin, deadline.Token); }
                            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                        }
                        confirmed = new DiscoveredServer(entry.ServerId, string.IsNullOrWhiteSpace(name)
                            ? string.IsNullOrWhiteSpace(entry.Name) ? "Silo" : entry.Name : name.Trim(), origin);
                        break;
                    }
                }
                finally { _slots.Release(); }
                if (!Current(key, entry)) return;
                if (confirmed is null && attempt + 1 < attempts) continue;
                entry.Health.Confirm(confirmed);
                Log.Info($"LAN candidate confirmed={confirmed is not null} misses={entry.Health.Misses}");
                Publish();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Log.Info($"LAN candidate probe failed type={ex.GetType().Name}"); }
        finally { entry.Busy = false; }
    }

    private void Publish() => _publish(LanDiscoveryRules.Rows(_entries.Values.Where(entry => entry.Health.Row is not null)
        .Select(entry => (entry.Health.Row!, entry.Health.Misses))));

    private void OnRefresh(object? sender, object args)
    {
        if (_failed) RestartBrowse();
        else foreach (var pair in _entries.ToArray()) _ = ConfirmAsync(pair.Key, pair.Value);
    }

    private void BrowseFailed()
    {
        _failed = true;
        if (_quickRetryUsed || _lifetime.IsCancellationRequested) return;
        _quickRetryUsed = true;
        _ = RetryBrowseAsync();
    }

    private async Task RetryBrowseAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            if (_failed && !_lifetime.IsCancellationRequested) RestartBrowse();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void RestartBrowse()
    {
        StopBrowse();
        StartBrowse();
    }

    private void StopBrowse()
    {
        ++_generation;
        try
        {
            if (_watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
        }
        catch (Exception ex) { Log.Info($"LAN browse stop unavailable type={ex.GetType().Name}"); }
        _watcher = null;
        foreach (var entry in _entries.Values) entry.Cancellation.Cancel();
        _entries.Clear();
        _devices.Clear();
        _publish([]);
    }

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _refresh.Stop();
        _refresh.Tick -= OnRefresh;
        StopBrowse();
        _probe.Dispose();
        Log.Info("LAN browse stopped; visit discarded");
    }
}
