# Fetches what the build needs that isn't on a Windows PC with Visual Studio already, into one folder
# (no installers, nothing system-wide): the .NET 10 SDK and the Windows Driver Kit's NuGet package.
# Each download is checked against a pinned hash. Run by build.cmd; safe to run again.
param([Parameter(Mandatory)][string] $Tools)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-Pinned([string] $Url, [string] $File, [string] $Algorithm, [string] $Hash) {
    if (-not (Test-Path $File)) {
        Write-Host "Downloading $(Split-Path $File -Leaf)"
        Invoke-WebRequest -Uri $Url -OutFile $File
    }
    $got = (Get-FileHash $File -Algorithm $Algorithm).Hash
    if ($got -ne $Hash) {
        Remove-Item $File
        throw "$(Split-Path $File -Leaf): hash mismatch (expected $Hash, got $got)"
    }
}

New-Item -ItemType Directory -Force (Join-Path $Tools 'downloads') | Out-Null
$dl = Join-Path $Tools 'downloads'

# .NET 10 SDK, used from this folder only
$dotnet = Join-Path $Tools 'dotnet'
if (-not (Test-Path (Join-Path $dotnet 'dotnet.exe'))) {
    $zip = Join-Path $dl 'dotnet-sdk-10.0.401-win-x64.zip'
    Get-Pinned 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip' $zip 'SHA512' `
        '24B670AD3D923BFCF47DF6C3B034152398B42F6DBC388E10D783AEE1CFB5E5817D399FC0AE2A12CFA822A55E61D34830CCB15C50EF6EFEE437AB874BB7C79430'
    Expand-Archive $zip -DestinationPath $dotnet -Force
}

# Windows Driver Kit (the NuGet package, not the installer): kernel and UMDF headers, libraries, Inf2Cat
$wdk = Join-Path $Tools 'wdk'
if (-not (Test-Path (Join-Path $wdk 'c\Include'))) {
    $pkg = Join-Path $dl 'microsoft.windows.wdk.x64.10.0.26100.6584.nupkg'
    Get-Pinned 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.wdk.x64/10.0.26100.6584/microsoft.windows.wdk.x64.10.0.26100.6584.nupkg' $pkg 'SHA256' `
        'C393D03DFB640B5C92F546B32F6770EF68CD3AAF691956E7D66D8E2C28A1B55E'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($pkg, $wdk)
}

# Inf2Cat as HIDMaestro embeds it: the package keeps its logger with the x64 tools and has no
# external manifest, so the folder is put together here
$inf2cat = Join-Path $Tools 'inf2cat\x86'
if (-not (Test-Path (Join-Path $inf2cat 'Inf2Cat.exe'))) {
    New-Item -ItemType Directory -Force $inf2cat | Out-Null
    $bin = Join-Path $wdk 'c\bin\10.0.26100.0'
    Copy-Item (Join-Path $bin 'x86\Inf2Cat.exe'), (Join-Path $bin 'x86\WindowsProtectedFiles.xml') $inf2cat
    Copy-Item (Join-Path $bin 'x86\Microsoft.UniversalStore.HardwareWorkflow.*.dll') $inf2cat
    Copy-Item (Join-Path $bin 'x64\Microsoft.Kits.Logger.dll') $inf2cat
    Set-Content (Join-Path $inf2cat 'inf2cat.exe.manifest') -Encoding UTF8 -Value @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
'@
}
