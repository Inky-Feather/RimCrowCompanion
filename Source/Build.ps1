param(
    [ValidateSet('All', 'Legacy', 'Waylith')]
    [string]$Target = 'All',
    [string]$DotNet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$root = Split-Path -Parent $source
Push-Location $source
try {
    $artifacts = @()
    if ($Target -ne 'Waylith') {
        foreach ($version in @('1.5', '1.6')) {
            & $DotNet build Legacy/RimCrowCompanion.Legacy.csproj -c Release "-p:RimWorldVersion=$version" --nologo
            if ($LASTEXITCODE -ne 0) { throw "Legacy $version build failed." }
            $artifacts += @{ Version = $version; Path = "Legacy/bin/Release/$version/RimCrowCompanion.dll" }
        }
    }
    if ($Target -ne 'Legacy') {
        & $DotNet build Waylith/RimCrowCompanion.Waylith.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Waylith 1.7 build failed.' }
        $artifacts += @{ Version = '1.7'; Path = 'Waylith/bin/Release/RimCrowCompanion.dll' }
    }
    $versions = ($artifacts | ForEach-Object { $_.Version }) -join ','
    & $DotNet run --project Tests/Verify.csproj -c Release -- $root --build-output $versions
    if ($LASTEXITCODE -ne 0) { throw 'Version isolation checks failed; packaged DLLs were not replaced.' }
    # 所有请求的构建成功后才更新发布目录，不将游戏和 Harmony 依赖带进模组。
    foreach ($artifact in $artifacts) {
        $destination = Join-Path $root "$($artifact.Version)/Assemblies"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $artifact.Path) -Destination $destination
    }
    & $DotNet run --project Tests/Verify.csproj -c Release --no-build -- $root --packaged $versions
    if ($LASTEXITCODE -ne 0) { throw 'Packaged DLL checks failed.' }
} finally {
    Pop-Location
}
