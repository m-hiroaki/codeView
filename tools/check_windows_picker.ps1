# Exercise the normal application and native dialogs through Windows UI Automation.
param(
    [Parameter(Mandatory)][string]$App,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:OS -ne 'Windows_NT') {
    throw 'Run only in the disposable Windows Actions desktop.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PickerMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
}
'@
function Click-Control($Control) {
    $bounds = $Control.Current.BoundingRectangle
    if ($bounds.IsEmpty -or $bounds.Width -le 0 -or $bounds.Height -le 0) {
        throw "No bounds for $($Control.Current.Name)"
    }
    if (![PickerMouse]::SetCursorPos([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))) {
        throw "Could not move mouse to $($Control.Current.Name)"
    }
    [PickerMouse]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    [PickerMouse]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}

function Wait-For([string]$Description, [scriptblock]$Read) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ($process.HasExited) { throw "Application exited: $($process.ExitCode)" }
        try {
            $value = & $Read
            if ($value) { return $value }
        } catch [System.Windows.Automation.ElementNotAvailableException] {}
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description"
}
function Find-Named($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-ById($Root, [string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-Control($Control) {
    $pattern = $null
    if ($Control.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
    } else {
        Click-Control $Control
    }
}
function Open-Picker([string]$Title) {
    $menu = Wait-For 'File menu' { Find-Named $window 'ファイル' }
    Click-Control $menu
    $item = Wait-For $Title {
        $item = Find-Named $window ($Title + '…')
        if ($item -and !$item.Current.BoundingRectangle.IsEmpty) { $item }
    }
    Click-Control $item
    Wait-For "$Title native dialog" {
        $dialog = Find-Named ([System.Windows.Automation.AutomationElement]::RootElement) $Title
        if ($dialog -and $dialog.Current.ProcessId -eq $process.Id -and $dialog.Current.ClassName -eq '#32770') { $dialog }
    }
}
function Set-PickerPath($Dialog, [string]$Path) {
    $edit = Wait-For 'native filename edit' { Find-ById $Dialog '1148' }
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Path)
    Invoke-Control (Wait-For 'native confirmation button' { Find-ById $Dialog '1' })
}
function Get-Hashes($Files) {
    @($Files | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
}

$appPath = (Resolve-Path -LiteralPath $App).Path
$outputPath = [IO.Path]::GetFullPath($Output)
$folder = Join-Path (Split-Path $outputPath) '選択 テスト'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$files = @((Join-Path $folder '日本語 1.cs'), (Join-Path $folder '日本語 2.cs'))
Set-Content -LiteralPath $files[0] -Value 'class First {}' -Encoding utf8
Set-Content -LiteralPath $files[1] -Value 'class Second {}' -Encoding utf8
$before = Get-Hashes $files
$report = [ordered]@{ commit = $env:GITHUB_SHA; passed = $false; checks = @(); sha256_before = $before }
$process = $null
try {
    $process = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -PassThru
    $window = Wait-For 'codeView window' {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        }
    }
    $dialog = Open-Picker 'ファイルを開く'
    Set-PickerPath $dialog ('"{0}" "{1}"' -f $files[0], $files[1])
    foreach ($file in $files) {
        $name = Split-Path $file -Leaf
        $null = Wait-For "tab $name" { Find-Named $window $name }
    }
    $report.checks += 'native-file-picker-opens-both-tabs'
    $active = Wait-For 'active source path' {
        foreach ($file in $files) { if (Find-Named $window $file) { return $file } }
    }

    $dialog = Open-Picker 'ファイルを開く'
    Invoke-Control (Wait-For 'native cancel button' { Find-ById $dialog '2' })
    $null = Wait-For 'dialog closes and active path stays' {
        if ($window.Current.IsEnabled -and (Find-Named $window $active)) { $true }
    }
    foreach ($file in $files) {
        if (!(Find-Named $window (Split-Path $file -Leaf))) { throw "Tab lost after cancel: $file" }
    }
    $report.checks += 'native-file-picker-cancel-keeps-tabs-and-active-file'

    $dialog = Open-Picker 'フォルダを開く'
    Set-PickerPath $dialog $folder
    $null = Wait-For 'selected workspace path' { Find-Named $window $folder }
    $tree = Wait-For 'file tree' { Find-Named $window 'ファイルツリー' }
    foreach ($file in $files) {
        $name = Split-Path $file -Leaf
        $null = Wait-For "tree item $name" { Find-Named $tree $name }
    }
    $report.checks += 'native-folder-picker-loads-workspace-and-tree'

    if (!$process.CloseMainWindow() -or !$process.WaitForExit(15000) -or $process.ExitCode -ne 0) {
        throw 'Application did not close normally'
    }
    $report.sha256_after = Get-Hashes $files
    if (Compare-Object $before $report.sha256_after -SyncWindow 0) { throw 'Input files changed' }
    $report.passed = $true
} catch {
    $report.error = $_.ToString()
    throw
} finally {
    if ($process) {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $outputPath -Encoding utf8
}
