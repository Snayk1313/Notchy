# Puts the regular setup made by "vpk pack" inside the card program, so the installer people download shows Notchy's own card.
# The result replaces the setup in the Releases folder. If anything fails here, the regular setup stays as it was.
param(
    [Parameter(Mandatory)][string]$PackId,
    [Parameter(Mandatory)][string]$Version,
    [string]$Releases = 'Releases',
    [string]$Project = '.'
)
$ErrorActionPreference = 'Stop'

$setup = Join-Path $Releases "$PackId-win-Setup.exe"
$package = Join-Path $Releases "$PackId-$Version-full.nupkg"

# what will end up in the install's "current" folder (the updater's own two files go elsewhere)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $package).Path)
try {
    $total = ($zip.Entries | Where-Object { $_.FullName -like 'lib/*' -and $_.Name -and $_.Name -ne 'Squirrel.exe' -and $_.Name -notlike '*_ExecutionStub.exe' } | Measure-Object Length -Sum).Sum
} finally {
    $zip.Dispose()
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("notchy-wrap-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $info = Join-Path $work 'install-info.txt'
    [IO.File]::WriteAllLines($info, [string[]]@($PackId, $Version, "$total"))
    $out = Join-Path $work 'Setup.exe'
    $fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
    & "$fx\csc.exe" /nologo /target:winexe /optimize+ /platform:anycpu "/win32icon:$(Join-Path $Project 'Notchy.ico')" "/out:$out" `
        "/r:$fx\WPF\PresentationFramework.dll" "/r:$fx\WPF\PresentationCore.dll" "/r:$fx\WPF\WindowsBase.dll" "/r:$fx\System.Xaml.dll" `
        "/resource:$setup,Setup.exe" "/resource:$info,install-info.txt" (Join-Path $Project 'UpdateCard\UpdateCard.cs')
    if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
    Move-Item -LiteralPath $out -Destination $setup -Force
    Write-Host "Setup wrapped: $total bytes to install"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
