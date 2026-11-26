<#
Runs the Windows App Certification Kit (appcert.exe, part of the Windows SDK) on a built MSIX and prints the tests that
did not pass (docs/store-readiness.md, "WACK"). CI runs it on production packages, non-blocking: the XML report goes to
-ReportDirectory for the workflow to upload. Every outcome that is not a finished run is reported in one clear line and
exits 0, so the job goes on: no appcert.exe on the runner, a run that needs an interactive session and never finishes,
or no report. A finished run exits 0 as well; the report and the printed summary carry its result.
#>
param(
    [string]$PackagePath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [string]$CertificatePath,
    [int]$TimeoutMinutes = 20,
    # Summarise the report already in -ReportDirectory without running appcert (scripts/test-production-package-checks.ps1).
    [switch]$SummaryOnly
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $ReportDirectory | Out-Null
$report = Join-Path (Resolve-Path $ReportDirectory).Path 'wack-report.xml'

# The report's overall result and every test that did not pass, with its messages.
function Write-Summary([string]$Path, [string]$ExitCode) {
    [xml]$xml = Get-Content $Path -Raw
    $overall = $xml.DocumentElement.GetAttribute('OVERALL_RESULT')
    $tests = @($xml.SelectNodes('//TEST'))
    $failed = @($tests | Where-Object { ($_.SelectSingleNode('RESULT').InnerText).Trim() -notin @('PASS', 'NOT RUN', 'NOTRUN') })
    Write-Output "WACK: overall $overall; $($tests.Count) tests, $($failed.Count) not passed (appcert exit $ExitCode)"
    foreach ($test in $failed) {
        Write-Output "  $(($test.SelectSingleNode('RESULT').InnerText).Trim()): $($test.GetAttribute('NAME'))"
        foreach ($message in @($test.SelectNodes('.//MESSAGE'))) { Write-Output "    $($message.GetAttribute('TEXT'))" }
    }
}

if ($SummaryOnly) {
    Write-Summary $report 'none'
    exit 0
}
if (-not $PackagePath) { throw '-PackagePath is required unless -SummaryOnly' }

$appcert = @("${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe",
    "$env:ProgramFiles\Windows Kits\10\App Certification Kit\appcert.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $appcert) {
    Write-Output 'WACK: NOT RUN - appcert.exe is not installed on this runner (Windows SDK App Certification Kit).'
    exit 0
}

# WACK installs the package: its signer must be trusted machine-wide (CI signs with a throwaway certificate).
if ($CertificatePath) {
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}

# appcert's exit code, or $null when it ran past the timeout; its output's tail goes to the host, not the pipeline.
function Invoke-AppCert([string[]]$Arguments, [string]$Name) {
    $log = Join-Path (Resolve-Path $ReportDirectory).Path "appcert-$Name.log"
    $process = Start-Process -FilePath $appcert -ArgumentList $Arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $process.Handle # keeps the exit code readable after exit
    if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        $process | Stop-Process -Force -ErrorAction SilentlyContinue
        return $null
    }
    Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Where-Object { $_ } | Select-Object -Last 20 |
        ForEach-Object { Write-Host "  appcert $($Name): $_" }
    return $process.ExitCode
}

$reset = Invoke-AppCert @('reset') 'reset'
if ($null -eq $reset) {
    Write-Output "WACK: NOT FINISHED - appcert.exe reset ran past $TimeoutMinutes minutes and was stopped. It may need an interactive session, which hosted runners lack."
    exit 0
}
$exit = Invoke-AppCert @('test', '-appxpackagepath', "`"$((Resolve-Path $PackagePath).Path)`"", '-reportoutputpath', "`"$report`"") 'test'
if ($null -eq $exit) {
    Write-Output "WACK: NOT FINISHED - appcert.exe ran past $TimeoutMinutes minutes and was stopped. It may need an interactive session, which hosted runners lack."
    exit 0
}
if (-not (Test-Path $report)) {
    Write-Output "WACK: NO REPORT - appcert.exe exited $exit without writing $report (see appcert-test.log in the artifact)."
    exit 0
}

Write-Summary $report $exit
exit 0
