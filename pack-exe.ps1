[CmdletBinding()]
param(
    [string]$OutputFile = '',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputFile)) { $OutputFile = Join-Path $PSScriptRoot 'publish/current/FH6Telemetry.exe' }

function Invoke-BuildTool([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Tool exited with code $LASTEXITCODE." }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools are required.' }
$vsDirectory = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsDirectory) { throw 'The Visual Studio x64 C++ toolchain was not found.' }
$msvc = Get-ChildItem -LiteralPath (Join-Path $vsDirectory 'VC/Tools/MSVC') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $kits ('bin/' + $_.Name + '/x64/rc.exe')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $msvc -or -not $sdk) { throw 'The x64 C++ toolchain and Windows SDK are required.' }

$toolDirectory = Join-Path $msvc.FullName 'bin/Hostx64/x64'
$cl = Join-Path $toolDirectory 'cl.exe'
$linker = Join-Path $toolDirectory 'link.exe'
$rc = Join-Path $kits ('bin/' + $sdk.Name + '/x64/rc.exe')
$sdkLibraries = Join-Path $kits ('Lib/' + $sdk.Name)
$project = Join-Path $PSScriptRoot 'FH6Telemetry.csproj'
$launcher = Join-Path $PSScriptRoot 'Packaging/Launcher.cpp'
$destination = [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location).ProviderPath, $OutputFile))
$workParent = Join-Path $PSScriptRoot 'obj/SingleExe'
$work = Join-Path $workParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $publish = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
        "-p:PathMap=$PSScriptRoot=/_/", '-p:DebugType=none', '-p:DebugSymbols=false',
        '-p:CopyOutputSymbolsToPublishDirectory=false', '-o', (Join-Path $work 'payload'))
    if ($NoRestore) { $publish += '--no-restore' }
    Invoke-BuildTool 'dotnet' $publish

    $payload = Join-Path $work 'payload/FH6Telemetry.exe'
    $tag = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.Substring(0, 24).ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $work 'PayloadTag.h') -Encoding ascii -Value @(
        '#pragma once', ('inline constexpr wchar_t PayloadTag[] = L"' + $tag + '";'))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.manifest') -Destination $work
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets/FH6Telemetry.ico') -Destination $work
    Set-Content -LiteralPath (Join-Path $work 'Package.rc') -Encoding ascii -Value @(
        '1 ICON "FH6Telemetry.ico"', '1 24 "app.manifest"', '101 RCDATA "payload/FH6Telemetry.exe"')

    Push-Location -LiteralPath $work
    try {
        Invoke-BuildTool $rc @('/nologo', '/foPackage.res', 'Package.rc')
        Invoke-BuildTool $cl @('/nologo', '/c', '/MT', '/O2', '/EHsc', '/W4', '/WX', '/std:c++17', '/utf-8',
            '/DUNICODE', '/D_UNICODE', "/I$work", ('/I' + (Join-Path $msvc.FullName 'include')),
            ('/I' + (Join-Path $sdk.FullName 'ucrt')), ('/I' + (Join-Path $sdk.FullName 'shared')),
            ('/I' + (Join-Path $sdk.FullName 'um')), '/FoLauncher.obj', $launcher)
        Invoke-BuildTool $linker @('/NOLOGO', '/MACHINE:X64', '/SUBSYSTEM:WINDOWS', '/MANIFEST:NO', '/INCREMENTAL:NO',
            '/OUT:FH6Telemetry.exe', 'Launcher.obj', 'Package.res', ('/LIBPATH:' + (Join-Path $msvc.FullName 'lib/x64')),
            ('/LIBPATH:' + (Join-Path $sdkLibraries 'ucrt/x64')), ('/LIBPATH:' + (Join-Path $sdkLibraries 'um/x64')),
            'kernel32.lib', 'user32.lib', 'shell32.lib', 'ole32.lib', 'uuid.lib', 'xinput.lib')
    }
    finally { Pop-Location }

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $temporaryOutput = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::Copy((Join-Path $work 'FH6Telemetry.exe'), $temporaryOutput)
        Move-Item -LiteralPath $temporaryOutput -Destination $destination -Force
    }
    finally { [IO.File]::Delete($temporaryOutput) }
    Get-Item -LiteralPath $destination
}
finally {
    $resolvedWork = Get-Item -LiteralPath $work
    $resolvedParent = (Resolve-Path -LiteralPath $workParent).Path
    if ($resolvedWork.Parent.FullName -eq $resolvedParent -and
        -not ($resolvedWork.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $resolvedWork.FullName -Recurse -Force
    }
}
