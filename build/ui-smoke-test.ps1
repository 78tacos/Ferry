# Drives the Ferry app through a file migration with UI Automation: save Favorites to a migration file,
# then open it as the "new PC" and do a dry run. Nothing on the PC is changed. Run with Windows PowerShell 5.1.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$WorkFolder
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]

function Wait-For([scriptblock]$Condition, [int]$Seconds = 30, [string]$What = 'condition') {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $What"
}

# Re-read the window each time: an element taken before WPF has hooked up its automation provider never sees the content.
function Get-Window { $AE::FromHandle($script:Hwnd) }

function Find([string]$Name, $Root = (Get-Window)) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $Name
    return $Root.FindFirst($Tree::Descendants, $cond)
}

function Find-Like([string]$Pattern) {
    $all = (Get-Window).FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) { if ($e.Current.Name -like $Pattern) { return $e } }
    return $null
}

function Click([string]$Name, [int]$Seconds = 30) {
    $el = Wait-For { $e = Find-Typed $Name ([System.Windows.Automation.ControlType]::Button); if ($e -and $e.Current.IsEnabled) { $e } } $Seconds "button '$Name'"
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "  clicked '$Name'"
}

function Find-Typed([string]$Name, $Type) {
    $cond = New-Object System.Windows.Automation.AndCondition (
        (New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $Name)),
        (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), $Type)
    (Get-Window).FindFirst($Tree::Descendants, $cond)
}

function Set-Text([string]$Name, [string]$Value) {
    $el = Wait-For { Find-Typed $Name ([System.Windows.Automation.ControlType]::Edit) } 15 "text box '$Name'"
    $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
    Write-Host "  typed into '$Name'"
}

function Set-Check([string]$Name, [bool]$On) {
    $el = Wait-For {
        $cond = New-Object System.Windows.Automation.AndCondition (
            (New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), $Name)),
            (New-Object System.Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([System.Windows.Automation.ControlType]::CheckBox))
        (Get-Window).FindFirst($Tree::Descendants, $cond)
    } 15 "check box '$Name'"
    $toggle = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $want = if ($On) { 'On' } else { 'Off' }
    for ($i = 0; $i -lt 3 -and $toggle.Current.ToggleState.ToString() -ne $want; $i++) { $toggle.Toggle() }
    Write-Host "  set '$Name' to $want"
}

function Expect-Title([string]$Pattern, [int]$Seconds = 60) {
    $el = Wait-For { Find-Like $Pattern } $Seconds "page '$Pattern'"
    Write-Host "PAGE: $($el.Current.Name)"
}

$package = Join-Path $WorkFolder 'ui-test.ferry'
Remove-Item "$package*" -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $Exe -PassThru
try {
    $script:Hwnd = Wait-For {
        $process.Refresh()
        if ($process.MainWindowHandle -ne 0) { $process.MainWindowHandle }
    } 30 'the Ferry window'

    Expect-Title 'Move to a new PC'
    Click 'This is my old PC'
    Expect-Title 'How do you want to move your stuff?'
    Click 'With a migration file'
    Expect-Title 'Choose what to move' 120
    Click 'None'
    Set-Text 'Search' 'Favorites'
    Start-Sleep -Milliseconds 800
    $items = (Get-Window).FindAll($Tree::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($AE::NameProperty), 'Favorites'))
    $box = $null
    foreach ($e in $items) { if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.CheckBox') { $box = $e } }
    if (-not $box) { throw 'No Favorites check box' }
    $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Write-Host '  ticked Favorites'
    $summary = Wait-For { Find-Like '1 item selected*' } 10 'the selection summary'
    Write-Host "  $($summary.Current.Name)"
    Click 'Next'

    Expect-Title 'Where should the migration file go?'
    Set-Text 'Save to' $package
    Set-Check 'Protect the file with a password' $false
    Start-Sleep -Milliseconds 600
    Click 'Save'
    Expect-Title 'All done' 120
    $sub = Find-Like '*saved to ui-test.ferry*'
    if (-not $sub) { throw 'The done page does not mention the file' }
    Write-Host "  $($sub.Current.Name)"
    if (-not (Test-Path $package)) { throw "The migration file was not written" }
    Click 'Finish'

    Expect-Title 'Move to a new PC'
    Click 'This is my new PC'
    Expect-Title 'How is your stuff coming over?'
    Click 'From a migration file'
    Expect-Title 'Open the migration file'
    Set-Text 'Migration file' $package
    Start-Sleep -Milliseconds 1200
    Click 'Open'
    Expect-Title 'Choose what to restore from*' 60
    Click 'Next'
    Expect-Title 'How should things be set up here?'
    Set-Check 'Dry run' $true
    Click 'Next'
    Expect-Title 'Ready for a dry run'
    Click 'Start dry run'
    Expect-Title 'Dry run finished' 120
    $sub = Find-Like 'Nothing was changed*'
    Write-Host "  $($sub.Current.Name)"
    Write-Host 'UI SMOKE TEST PASSED'
}
finally {
    if (-not $process.HasExited) { $process.Kill() }
}
