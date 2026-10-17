# Lists what UI Automation sees in the Ferry window (for debugging the smoke test).
param([Parameter(Mandatory)] [string]$Exe)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$process = Start-Process -FilePath $Exe -PassThru
try {
    for ($i = 0; $i -lt 60 -and $process.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 250; $process.Refresh() }
    Start-Sleep -Seconds 2
    $window = $AE::FromHandle($process.MainWindowHandle)
    Write-Host "Window: '$($window.Current.Name)' class=$($window.Current.ClassName)"
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    function Walk($element, $depth) {
        if ($depth -gt 14) { return }
        $child = $walker.GetFirstChild($element)
        while ($child) {
            $name = $child.Current.Name
            $type = $child.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            if ($name -or $type -in 'Button', 'CheckBox', 'Edit') { Write-Host ("{0}{1}: '{2}'" -f ('  ' * $depth), $type, $name) }
            Walk $child ($depth + 1)
            $child = $walker.GetNextSibling($child)
        }
    }
    Walk $window 0
}
finally {
    if (-not $process.HasExited) { $process.Kill() }
}
