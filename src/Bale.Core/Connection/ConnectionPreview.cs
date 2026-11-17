namespace Bale.Core.Connection;

/// <summary>Disposable debug fixtures: no platform storage, vault, authenticated client or network dependency.</summary>
public sealed class ConnectionPreview
{
    public SavedServerRegistry Servers { get; }

    public ConnectionPreview()
    {
        Servers = new SavedServerRegistry(new MemoryStorage());
        Servers.AddAndActivate(new Uri("https://media.example.com"), "Second server");
        Servers.AdoptSignIn("preview-user-1", "alex");
        Servers.AddAndActivate(new Uri("https://example.com"), "Example server");
        Servers.AdoptSignIn("preview-user-2", "sam");
        Servers.ActivateAccount(Servers.Active!.Id, null);
        Servers.AdoptSignIn("preview-user-3", "robin");
        Servers.MarkNeedsSignIn(Servers.ActiveAccount!.Id);
        Servers.ActivateAccount(Servers.Active.Id, Servers.AccountsOn(Servers.Active.Id)[0].Id);
    }

    private sealed class MemoryStorage : ISavedServerStorage
    {
        private string? _json;
        public string? Read() => _json;
        public void Write(string json) => _json = json;
    }
}
