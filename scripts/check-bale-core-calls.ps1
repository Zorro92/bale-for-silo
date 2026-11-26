<#
Asks a built bale_core library (the packaged bale_core.dll in CI, or a Linux libbale_core.so locally) whether it has the
debug-server JSON calls (native/bale-core, Cargo feature debug-probes): a debug-server build must answer each one, a
production build must report each as an unknown function. Their names can't be found as strings in the binary, because
the dispatcher's string match compiles to integer comparisons, so the library is loaded and called. Every call gets the
argument 0, which each function refuses before doing anything: nothing is armed and no request is made.
#>
param(
    [Parameter(Mandatory)][string]$LibraryPath,
    [Parameter(Mandatory)][ValidateSet('true', 'false')][string]$DebugServer
)

$ErrorActionPreference = 'Stop'

# native/bale-core/src/abi.rs: the calls only debug-server builds have.
$DebugOnlyCalls = @('media_fault.set', 'http_probe.run', 'http_probe.fetch_pattern')

if (-not ('BaleCoreCallCheck' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class BaleCoreCallCheck
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CallFunction(byte[] name, UIntPtr nameLength, byte[] input, UIntPtr inputLength,
        out IntPtr output, out UIntPtr outputLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFunction(IntPtr buffer, UIntPtr length);

    /// <summary>bale_core_call's status and JSON body for each name, all with the same argument.</summary>
    public static string[] Call(string library, string input, string[] names)
    {
        var module = NativeLibrary.Load(library);
        try
        {
            var call = Marshal.GetDelegateForFunctionPointer<CallFunction>(NativeLibrary.GetExport(module, "bale_core_call"));
            var free = Marshal.GetDelegateForFunctionPointer<FreeFunction>(NativeLibrary.GetExport(module, "bale_core_free"));
            var argument = Encoding.UTF8.GetBytes(input);
            var results = new string[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                var name = Encoding.UTF8.GetBytes(names[i]);
                var status = call(name, (UIntPtr)name.Length, argument, (UIntPtr)argument.Length, out var output, out var length);
                if (status < 0) { results[i] = status + ":"; continue; }
                try
                {
                    var body = new byte[checked((int)length.ToUInt64())];
                    Marshal.Copy(output, body, 0, body.Length);
                    results[i] = status + ":" + Encoding.UTF8.GetString(body);
                }
                finally { free(output, length); }
            }
            return results;
        }
        finally { NativeLibrary.Free(module); }
    }
}
'@
}

$library = (Resolve-Path $LibraryPath).Path
# A call every build has, so a library that refuses everything cannot pass as production.
$common = [BaleCoreCallCheck]::Call($library, '" H.265 "', @('software_decode.normalize_codec'))[0]
if ($common -cne '0:"hevc"') { throw "bale_core did not answer software_decode.normalize_codec ($common)" }
$results = [BaleCoreCallCheck]::Call($library, '0', [string[]]$DebugOnlyCalls)
for ($i = 0; $i -lt $DebugOnlyCalls.Count; $i++) {
    $name = $DebugOnlyCalls[$i]
    $unknown = $results[$i] -ceq "1:`"unknown function $name`""
    if ($DebugServer -eq 'true' -and $unknown) { throw "The debug-server bale_core lacks the debug call $name" }
    if ($DebugServer -eq 'false' -and -not $unknown) { throw "The production bale_core still answers the debug call $name ($($results[$i]))" }
}
$build = if ($DebugServer -eq 'true') { 'debug-server build: present' } else { 'production build: absent' }
Write-Output "bale_core debug JSON calls ($build): $($DebugOnlyCalls -join ', ')"
