using Bale.Core.Settings;
using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>
/// <see cref="ISettingsMemory"/> over the app's local settings: device-only values such as the last buffering-quality
/// mode seen per server and profile (keys are hashes; values are short wire names).
/// </summary>
internal sealed class LocalSettingsMemory : ISettingsMemory
{
    private static ApplicationDataContainer Local => ApplicationData.Current.LocalSettings;

    public string? Read(string key)
    {
        try { return Local.Values[key] as string; }
        catch (Exception) { return null; }
    }

    public void Write(string key, string value)
    {
        try { Local.Values[key] = value; }
        catch (Exception ex) { Log.Info($"settings memory: write {ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}"); }
    }
}
