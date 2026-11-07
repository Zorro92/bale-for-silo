// Deliberately small stand-in: tests exercise our orchestration, not WinRT or HDMI hardware.
namespace Windows.Graphics.Display.Core
{
    internal enum HdmiDisplayHdrOption { None, Eotf2084 }

    internal sealed class HdmiDisplayMode
    {
        public bool IsSmpte2084Supported { get; init; }
        public bool Is2086MetadataSupported { get; init; }
        public bool StereoEnabled { get; init; }
        public uint ResolutionWidthInRawPixels { get; init; } = 1920;
        public uint ResolutionHeightInRawPixels { get; init; } = 1080;
        public double RefreshRate { get; init; } = 60;

        public bool IsEqual(HdmiDisplayMode other) => ResolutionWidthInRawPixels == other.ResolutionWidthInRawPixels
            && ResolutionHeightInRawPixels == other.ResolutionHeightInRawPixels && RefreshRate == other.RefreshRate
            && StereoEnabled == other.StereoEnabled && IsSmpte2084Supported == other.IsSmpte2084Supported
            && Is2086MetadataSupported == other.Is2086MetadataSupported;
    }

    internal sealed class HdmiDisplayInformation
    {
        internal static Func<HdmiDisplayInformation?> ForCurrentView { get; set; } = () => null;
        internal Func<IReadOnlyList<HdmiDisplayMode>> ReadModes { get; set; } = () => [];
        internal HdmiDisplayMode? Current { get; set; } = new();
        internal Func<Task<bool>> Switch { get; set; } = () => Task.FromResult(true);
        internal Func<Task> Restore { get; set; } = () => Task.CompletedTask;
        internal int Switches { get; private set; }
        internal int Restores { get; private set; }
        internal HdmiDisplayMode? RequestedMode { get; private set; }
        internal HdmiDisplayHdrOption? RequestedOption { get; private set; }

        public static HdmiDisplayInformation? GetForCurrentView() => ForCurrentView();
        public HdmiDisplayMode? GetCurrentDisplayMode() => Current;
        public IReadOnlyList<HdmiDisplayMode> GetSupportedDisplayModes() => ReadModes();
        public Task<bool> RequestSetCurrentDisplayModeAsync(HdmiDisplayMode mode, HdmiDisplayHdrOption option)
        {
            Switches++;
            RequestedMode = mode;
            RequestedOption = option;
            return Switch();
        }
        public Task SetDefaultDisplayModeAsync()
        {
            Restores++;
            return Restore();
        }
    }
}

namespace Windows.Graphics.Display
{
    internal enum AdvancedColorKind { StandardDynamicRange, WideColorGamut, HighDynamicRange }

    internal sealed class AdvancedColorInfo
    {
        public AdvancedColorKind CurrentAdvancedColorKind { get; init; }
    }

    internal sealed class DisplayInformation
    {
        internal static Func<AdvancedColorInfo?> ReadColor { get; set; } = () => new();

        public static DisplayInformation GetForCurrentView() => new();
        public AdvancedColorInfo? GetAdvancedColorInfo() => ReadColor();
    }
}

namespace Bale.Xbox.Services
{
    internal static class Log
    {
        internal static List<string> Messages { get; } = [];
        internal static void Info(string message) => Messages.Add(message);
    }
}
