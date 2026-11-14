namespace Bale.Xbox.Services;

internal readonly record struct UiResourceCounts(int Images = 0, int Surfaces = 0, int Shadows = 0, int Samples = 0)
{
    public static UiResourceCounts operator +(UiResourceCounts a, UiResourceCounts b) =>
        new(a.Images + b.Images, a.Surfaces + b.Surfaces, a.Shadows + b.Shadows, a.Samples + b.Samples);

    public override string ToString() => $"images={Images} surfaces={Surfaces} shadows={Shadows} samples={Samples}";
}

internal interface IUiResource
{
    UiResourceCounts Resident { get; }
    UiResourceCounts Release();
    UiResourceCounts Restore();
}

/// <summary>
/// UI-thread policy, independent of XAML so background/resume races can be tested on Linux.
/// Registration is weak: the tracker must never become another owner of a cached page or bitmap.
/// Foreground only opens the gate; the visible tree decides which resources to restore.
/// </summary>
[Flags]
internal enum UiResidencyReason { Background = 1, Playback = 2 }

internal sealed class UiResidencyState(Action<Exception>? failure = null)
{
    private readonly List<WeakReference<IUiResource>> _resources = new();
    private UiResidencyReason _reasons;
    public bool IsBackground => (_reasons & UiResidencyReason.Background) != 0;
    public bool IsPlayback => (_reasons & UiResidencyReason.Playback) != 0;
    public bool IsSuppressed => _reasons != 0;
    public int Generation { get; private set; }
    public int Releases { get; private set; }
    public int Resumes { get; private set; }
    public UiResourceCounts LastReleased { get; private set; }
    public UiResourceCounts Restored { get; private set; }

    public void Register(IUiResource resource)
    {
        _resources.Add(new(resource));
        if (IsSuppressed) LastReleased += Run(resource.Release);
        if (_resources.Count % 128 == 0) Prune();
    }

    public UiResourceCounts Release(UiResidencyReason reason = UiResidencyReason.Background)
    {
        if ((_reasons & reason) == 0)
        {
            if (!IsSuppressed)
            {
                LastReleased = default;
                Restored = default;
            }
            _reasons |= reason;
            Generation++;
            Releases++;
        }
        // Also sweep a repeated Suspending: late page/model construction cannot retain an image.
        foreach (var reference in _resources.ToArray())
            if (reference.TryGetTarget(out var resource)) LastReleased += Run(resource.Release);
        Prune();
        return LastReleased;
    }

    public bool Resume(UiResidencyReason reason = UiResidencyReason.Background)
    {
        if ((_reasons & reason) == 0) return false;
        _reasons &= ~reason;
        Generation++;
        Resumes++;
        return true;
    }

    public UiResourceCounts Restore(IUiResource resource)
    {
        if (IsSuppressed) return default;
        var counts = Run(resource.Restore);
        Restored += counts;
        return counts;
    }

    public UiResourceCounts Resident
    {
        get
        {
            UiResourceCounts counts = default;
            foreach (var reference in _resources)
                if (reference.TryGetTarget(out var resource)) counts += resource.Resident;
            return counts;
        }
    }

    private UiResourceCounts Run(Func<UiResourceCounts> action)
    {
        try { return action(); }
        catch (Exception ex) { failure?.Invoke(ex); return default; }
    }

    private void Prune() => _resources.RemoveAll(reference => !reference.TryGetTarget(out _));
}
