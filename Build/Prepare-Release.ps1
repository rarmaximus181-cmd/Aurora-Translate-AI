param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'ReleaseArtifacts')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$runtimePath = Join-Path $outputPath 'runtime'
dotnet publish (Join-Path $projectRoot 'AuroraTranslate.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -o $runtimePath
if ($LASTEXITCODE -ne 0) { throw 'The application build failed.' }
$exePath = Join-Path $outputPath 'AuroraTranslate.exe'
Copy-Item -LiteralPath (Join-Path $runtimePath 'AuroraTranslate.exe') -Destination $exePath -Force

# Retain exact corresponding native sources and notices alongside the portable app.
$sources = @(
    @{ Name='piper-2023.11.14-2-source.zip'; Url='https://github.com/rhasspy/piper/archive/refs/tags/2023.11.14-2.zip'; Hash='48b4eef63a3694c1349c3227c94d6dc54c1d07849e058f40f33e8391f8487539' },
    @{ Name='piper-phonemize-source.zip'; Url='https://github.com/rhasspy/piper-phonemize/archive/fccd4f335aa68ac0b72600822f34d84363daa2bf.zip'; Hash='d4d899cca03114a6b2e68635dcdb7fba48b70599e36a481dfe6b6d9e2b37aa84' },
    @{ Name='espeak-ng-source.zip'; Url='https://github.com/rhasspy/espeak-ng/archive/0f65aa301e0d6bae5e172cc74197d32a6182200f.zip'; Hash='150fc955926a084a77ebe25c9e8287bde2e2a17bbf9b23a25c147b556837949f' }
)
$nativeSource = Join-Path $projectRoot 'NativeSource'
foreach ($source in $sources) {
    $path = Join-Path $nativeSource $source.Name
    if (-not (Test-Path -LiteralPath $path)) { Invoke-WebRequest -Uri $source.Url -OutFile $path -TimeoutSec 120 }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $source.Hash) { throw ('Native source checksum failed: ' + $source.Name) }
}
$portableFolder = Join-Path $outputPath 'Aurora-Translate-Portable'
New-Item -ItemType Directory -Path $portableFolder -Force | Out-Null
Copy-Item -LiteralPath $exePath -Destination $portableFolder -Force
foreach ($name in @('README.txt','CUSTOM-VOICES.md','DISCORD-SIGN-IN.md','UPDATES.md','THIRD-PARTY-NOTICES.txt','CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $portableFolder -Force
}
Copy-Item -LiteralPath $nativeSource -Destination $portableFolder -Recurse -Force
Compress-Archive -LiteralPath $portableFolder -DestinationPath (Join-Path $outputPath 'Aurora-Translate-Portable.zip') -Force
Compress-Archive -LiteralPath $nativeSource -DestinationPath (Join-Path $outputPath 'Aurora-Native-Source.zip') -Force
$manifest = [ordered]@{
    version = $Version
    minimumSupportedVersion = $Version
    mandatory = $true
    releaseNotes = 'Updated Aurora workspace, local AI, voice support, and verified GitHub updates. See the release changelog for details.'
    assetName = 'AuroraTranslate.exe'
    sha256 = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
    sizeBytes = (Get-Item -LiteralPath $exePath).Length
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'update.json') -Encoding utf8
Write-Output ('Prepared Aurora ' + $Version + ' in ' + $outputPath)
