param(
    [string]$TerrariaDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $TerrariaDirectory) {
    $localProps = Join-Path $repositoryRoot 'Local.props'
    if (Test-Path -LiteralPath $localProps) {
        [xml]$settings = Get-Content -LiteralPath $localProps -Raw
        $TerrariaDirectory = [string]$settings.Project.PropertyGroup.TerrariaDirectory
    }
    if (-not $TerrariaDirectory) {
        $TerrariaDirectory = Join-Path ${env:ProgramFiles(x86)} 'Steam/steamapps/common/Terraria'
    }
}
$gamePath = Join-Path $TerrariaDirectory 'Terraria.exe'
$injector = Join-Path $repositoryRoot "bin/Win32/$Configuration/SpookyInjector.exe"
if (-not (Test-Path -LiteralPath $gamePath -PathType Leaf)) { throw "Terraria was not found at $gamePath" }
if (-not (Test-Path -LiteralPath $injector -PathType Leaf)) { throw "Build the x86 $Configuration solution first." }
if (Get-Process -Name Terraria -ErrorAction SilentlyContinue) {
    throw 'Close Terraria before using this launcher; Windows fixes DPI awareness when the game starts.'
}

$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $gamePath
$start.WorkingDirectory = $TerrariaDirectory
$start.UseShellExecute = $false
# Apply the display-compatibility setting only to this child process, without registry changes.
$layers = @($start.EnvironmentVariables['__COMPAT_LAYER'] -split '\s+' |
    Where-Object { $_ -and $_ -notin @('HIGHDPIAWARE', 'DPIUNAWARE', 'GDIDPISCALING') })
$start.EnvironmentVariables['__COMPAT_LAYER'] = ($layers + 'HIGHDPIAWARE') -join ' '
Write-Output 'Starting Terraria with application DPI scaling...'
$game = [Diagnostics.Process]::Start($start)
$deadline = [DateTime]::UtcNow.AddSeconds(90)
do {
    if ($game.HasExited) { throw "Terraria exited before opening a window (code $($game.ExitCode))." }
    $game.Refresh()
    if ($game.MainWindowHandle -ne [IntPtr]::Zero) { break }
    if ([DateTime]::UtcNow -ge $deadline) { throw 'Terraria did not open a window within 90 seconds.' }
    Start-Sleep -Milliseconds 250
} while ($true)

# The window exists and CLR/XNA have initialized; the managed payload waits for game callbacks.
& $injector
if ($LASTEXITCODE -ne 0) { throw "The injector exited with code $LASTEXITCODE." }
