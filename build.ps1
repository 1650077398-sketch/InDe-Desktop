[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}

$dist = Join-Path $PSScriptRoot 'dist'
$name = "InDe-Desktop-v$Version-win-x64"
$package = Join-Path $dist $name
if (Test-Path -LiteralPath $package) { throw "Build folder already exists: $package" }
New-Item -ItemType Directory -Path $package | Out-Null

$application = Join-Path $package 'InDeDesktop.exe'
$icon = Join-Path $PSScriptRoot 'InDe.ico'
$mark = Join-Path $PSScriptRoot 'InDeMark.png'
$main = Join-Path $PSScriptRoot 'InDeDesktop.cs'
$backend = Join-Path $PSScriptRoot 'BackendInstaller.cs'
$options = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
    "/win32icon:$icon", "/resource:$mark,InDe.Header", "/out:$application"
)
& $compiler @options $main $backend
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }

foreach ($file in @('process_inde.py', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $package
}
$archive = Join-Path $dist "$name.zip"
Compress-Archive -LiteralPath $package -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $dist "$name.sha256.txt") -Encoding Ascii -Value "$hash  $name.zip"
Write-Output $archive
