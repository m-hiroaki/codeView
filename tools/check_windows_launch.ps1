# A smoke test of the normal AOT executable, on a disposable Windows CI desktop.
param(
    [Parameter(Mandatory)][string]$App,
    [Parameter(Mandatory, ParameterSetName='File')][string]$InputFile,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory, ParameterSetName='EmptyShortcut')][switch]$EmptyCloseShortcut
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or !$IsWindows) {
    throw 'Run only in the disposable Windows Actions desktop.'
}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CodeViewPublishWindow {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$appPath = (Resolve-Path -LiteralPath $App).Path
$inputPath = if (!$EmptyCloseShortcut) { (Resolve-Path -LiteralPath $InputFile).Path }
$before = if ($inputPath) { (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash }
$scope = if ($EmptyCloseShortcut) { 'empty startup window and Ctrl+W normal exit' } else { 'visible native window and normal close only' }
$report = [ordered]@{ schema = 1; commit = $env:GITHUB_SHA; app = $appPath; input = $inputPath;
    input_sha256_before = $before; passed = $false; scope = $scope }
$process = $null
try {
    $start = [System.Diagnostics.ProcessStartInfo]::new($appPath)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $appPath
    if ($inputPath) { $start.ArgumentList.Add($inputPath) }
    $process = [System.Diagnostics.Process]::Start($start)
    $report.pid = $process.Id
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($process.HasExited) { throw "Exited before window: $($process.ExitCode)" }
        $process.Refresh()
        $window = $process.MainWindowHandle
        if ($window -ne [IntPtr]::Zero -and [CodeViewPublishWindow]::IsWindowVisible($window)) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($window -eq [IntPtr]::Zero -or ![CodeViewPublishWindow]::IsWindowVisible($window)) {
        throw 'No visible native window within 30 seconds'
    }
    $rect = [CodeViewPublishWindow+Rect]::new()
    if (![CodeViewPublishWindow]::GetClientRect($window, [ref]$rect)) { throw 'GetClientRect failed' }
    if ($rect.Right -le $rect.Left -or $rect.Bottom -le $rect.Top) { throw 'Empty client area' }
    $report.window = @{ title = $process.MainWindowTitle; width = $rect.Right - $rect.Left;
        height = $rect.Bottom - $rect.Top; visible = $true }
    # Allow early initialization failures to surface; this is not a paint/content timing claim.
    Start-Sleep -Seconds 2
    if ($process.HasExited) { throw "Exited before close: $($process.ExitCode)" }
    $process.Refresh()
    $report.modules = @($process.Modules | ForEach-Object { $_.ModuleName } | Sort-Object)
    if ($report.modules | Where-Object { $_ -in @('coreclr.dll', 'clrjit.dll', 'hostfxr.dll', 'hostpolicy.dll') }) {
        throw 'Managed JIT runtime loaded'
    }
    if ($EmptyCloseShortcut) {
        Add-Type -AssemblyName System.Windows.Forms
        $shell = New-Object -ComObject WScript.Shell
        $null = $shell.AppActivate($process.Id)
        if ([CodeViewPublishWindow]::GetForegroundWindow() -ne $window) {
            throw 'Expected the owned codeView window to have keyboard focus'
        }
        [System.Windows.Forms.SendKeys]::SendWait('^w')
    } elseif (!$process.CloseMainWindow()) {
        throw 'Could not request normal close'
    }
    if (!$process.WaitForExit(15000)) { throw 'Normal close timed out' }
    $report.exit_code = $process.ExitCode
    if ($process.ExitCode -ne 0) { throw "App failed: $($process.ExitCode)" }
    if ($inputPath) {
        $after = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
        $report.input_sha256_after = $after
        if ($before -ne $after) { throw 'Input changed' }
    }
    $report.passed = $true
} catch {
    $report.error = $_.Exception.ToString()
    throw
} finally {
    if ($null -ne $process) {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding utf8
}
