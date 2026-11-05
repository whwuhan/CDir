Import-Module PSReadLine -ErrorAction Stop
Set-PSReadLineOption -HistorySaveStyle SaveNothing
Set-PSReadLineKeyHandler -Key UpArrow -Function PreviousHistory
Set-PSReadLineKeyHandler -Key DownArrow -Function NextHistory

# Use an explicit muted foreground: some VT renderers ignore the default faint text attribute.
& {
    $Options = Get-PSReadLineOption
    $MutedColor = [string][char]0x1b + '[38;2;111;111;111m'
    $Colors = @{}
    foreach ($Name in @('InlinePrediction', 'ListPredictionTooltip')) {
        if ($null -ne $Options.PSObject.Properties[$Name + 'Color']) { $Colors[$Name] = $MutedColor }
    }
    if ($Colors.Count -gt 0) { Set-PSReadLineOption -Colors $Colors }
}
