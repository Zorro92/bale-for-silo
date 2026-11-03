namespace Bale.Core.Connection;

/// <summary>Disposable debug fixtures: no platform storage, vault, authenticated client or network dependency.</summary>
public sealed class ConnectionPreview
{
    public SavedServerRegistry Servers { get; }

    public ConnectionPreview()
    {
        Servers = new SavedServerRegistry(new MemoryStorage());
        Servers.AddAndActivate(new Uri("https://media.example.com"), "Second server");
        Servers.AddAndActivate(new Uri("https://example.com"), "Example server");
    }

    private sealed class MemoryStorage : ISavedServerStorage
    {
        private string? _json;
        public string? Read() => _json;
        public void Write(string json) => _json = json;
    }
}
