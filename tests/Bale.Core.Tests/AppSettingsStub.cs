// Exercise AppSettings itself without WinRT. Only LocalSettings is modeled; the credential vault
// and server-registry folder are deliberately unavailable to these device-preference tests.
namespace Windows.Storage
{
    internal sealed class ApplicationData
    {
        public static ApplicationData Current { get; } = new();
        public ApplicationDataContainer LocalSettings { get; } = new();
        public StorageFolder LocalFolder => throw new NotSupportedException();
    }

    internal sealed class ApplicationDataContainer
    {
        public LocalSettingsValues Values { get; } = new();
    }

    internal sealed class LocalSettingsValues
    {
        private readonly Dictionary<string, object?> _values = [];
        public object? this[string key]
        {
            get => _values.GetValueOrDefault(key);
            set => _values[key] = value;
        }
        public bool Remove(string key) => _values.Remove(key);
    }

    internal sealed class StorageFolder
    {
        public string Path => throw new NotSupportedException();
    }
}

namespace Windows.Security.Credentials
{
    internal sealed class PasswordVault
    {
        public PasswordCredential Retrieve(string resource, string userName) => throw new NotSupportedException();
        public void Add(PasswordCredential credential) => throw new NotSupportedException();
        public void Remove(PasswordCredential credential) => throw new NotSupportedException();
    }

    internal sealed class PasswordCredential
    {
        public PasswordCredential(string resource, string userName, string password) => Password = password;
        public string Password { get; }
        public void RetrievePassword() => throw new NotSupportedException();
    }
}
