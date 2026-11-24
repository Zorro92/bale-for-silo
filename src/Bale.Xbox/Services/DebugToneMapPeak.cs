#if DEBUG_SERVER
using System.Globalization;
using System.Runtime.InteropServices;

namespace Bale.Xbox.Services;

/// <summary>
/// The source peak bale_gpu's tone map applies (bale_tonemap_peak_read, 2026-11-24): the stream's MaxCLL or mastering
/// maximum for PQ, else the config peak. Logs one line per stream (and per later change) and fills the "Tone-map
/// peak" stats row. Reads a small native record only: no D3D, no FFmpegInteropX source lock.
/// </summary>
internal sealed class DebugToneMapPeak(ulong? baselineStage)
{
    /// <summary>BaleToneMapPeakInfo (native/bale-gpu/include/bale_tonemap.h), 40 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Info
    {
        public ulong Stage;
        public uint Changes, Source;
        public float PeakNits, ConfigPeakNits;
        public uint MaxCll;
        public float MasteringMaxNits;
        public uint Transfer, Flags;
    }

    private ulong _lastStage;
    private uint _lastChanges;
    private string _row = baselineStage is null ? "unavailable (native snapshot busy at mount)" : "no tone-map stage";

    internal static DebugToneMapPeak Capture() => new(Read()?.Stage);

    internal string ReadRow(out string? logLine)
    {
        logLine = null;
        return Read() is { } info ? Accept(info, out logLine) : _row;
    }

    // Separated from the P/Invoke so Linux tests exercise stale stages, log deduplication and formatting.
    internal string Accept(Info info, out string? logLine)
    {
        logLine = null;
        // Nothing yet, or a stage of an earlier playback (the record follows the newest stage only).
        if (baselineStage is null || info.Stage <= baselineStage.Value) return _row;
        if (info.Changes == 0)
            return _row = string.Create(CultureInfo.InvariantCulture, $"no PQ or HLG frame yet · stage {info.Stage}");
        if (info.Stage != _lastStage || info.Changes != _lastChanges)
        {
            logLine = "Bale tone map peak: " + Describe(info);
            _lastStage = info.Stage;
            _lastChanges = info.Changes;
        }
        return _row = Describe(info);
    }

    internal static string Describe(Info info)
    {
        var source = info.Source switch
        {
            1 => "config",
            2 => "maxcll",
            3 => "mastering",
            _ => "unknown",
        };
        var transfer = info.Transfer switch
        {
            16 => "PQ",
            18 => "HLG",
            _ => string.Create(CultureInfo.InvariantCulture, $"transfer {info.Transfer}"),
        };
        var maxCll = info.MaxCll == 0 ? "none" : info.MaxCll.ToString(CultureInfo.InvariantCulture);
        var mastering = info.MasteringMaxNits > 0 ? Nits(info.MasteringMaxNits) : "none";
        var config = info.ConfigPeakNits > 0 ? Nits(info.ConfigPeakNits) : "unset";
        var fixedPeak = (info.Flags & 1) != 0 ? " · debug fixedpeak" : string.Empty;
        return $"{Nits(info.PeakNits)} nits from {source} ({transfer} · MaxCLL {maxCll} · mastering {mastering} · config {config}){fixedPeak} · stage {info.Stage.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string Nits(float nits) => nits.ToString("0.###", CultureInfo.InvariantCulture);

    private static unsafe Info? Read()
    {
        try
        {
            Info info = default;
            return bale_tonemap_peak_read(&info) == 0 ? info : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    // Pointer-only signature: DisableRuntimeMarshalling=true, Native AOT and CA1420.
    [DllImport("bale_gpu.dll", ExactSpelling = true)]
    private static extern unsafe int bale_tonemap_peak_read(Info* info);
}
#endif
