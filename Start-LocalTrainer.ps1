[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ServerRoot,
    [ValidateRange(1024, 65535)]
    [int] $Port = 27015,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ServerRoot).Path
$game = Join-Path $root 'game/csgo'
$exe = Join-Path $root 'game/bin/win64/cs2.exe'
$addons = Join-Path $game 'addons'
$css = Join-Path $addons 'counterstrikesharp'

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'ServerRoot must contain game/bin/win64/cs2.exe.'
}
if (-not (Test-Path -LiteralPath (Join-Path $addons 'metamod.vdf')) -or
    -not (Test-Path -LiteralPath (Join-Path $css 'plugins') -PathType Container) -or
    -not (Select-String -LiteralPath (Join-Path $game 'gameinfo.gi') -Pattern 'csgo/addons/metamod' -Quiet)) {
    throw 'Install and enable Metamod and CounterStrikeSharp on this dedicated server first. See README.md.'
}
$dll = Join-Path $PSScriptRoot 'bin/Release/net10.0/Dust2Trainer.dll'
if (-not $SkipBuild) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install the .NET 10 SDK and reopen PowerShell, or use -SkipBuild with an existing Release DLL.'
    }
    $project = Join-Path $PSScriptRoot 'Dust2Trainer.csproj'
    dotnet build $project --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
}
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Release DLL missing. Build the plugin first.' }

$plugin = Join-Path $css 'plugins/Dust2Trainer'
New-Item -ItemType Directory -Path $plugin -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $plugin
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dust2_trainer.cfg') -Destination (Join-Path $game 'cfg/dust2_trainer.cfg')

Write-Host "In the CS2 client console: connect 127.0.0.1:$Port"
Push-Location -LiteralPath $root
try {
    $server = Start-Process -FilePath $exe -ArgumentList @(
        '-dedicated', '-console', '-insecure', '-ip', '127.0.0.1', '-port', "$Port",
        '+sv_lan', '1', '+game_type', '0', '+game_mode', '0',
        '+exec', 'dust2_trainer.cfg', '+map', 'de_dust2', '+meta', 'list', '+css_plugins', 'list'
    ) -NoNewWindow -PassThru -Wait
    if ($server.ExitCode -ne 0) { throw "CS2 server exited with code $($server.ExitCode)." }
}
finally {
    Pop-Location
}
