param([switch]$Publish, [switch]$SkipUiSmoke)

$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    # Keep build caches in the workspace, including in restricted development environments.
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.artifacts/dotnet'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.artifacts/packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot '.artifacts/nuget-http'
    $env:NUGET_SCRATCH = Join-Path $PSScriptRoot '.artifacts/nuget-scratch'

    dotnet restore SimpleShiftTracker.slnx --disable-parallel -m:1 --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    dotnet build SimpleShiftTracker.slnx -c Release --no-restore -m:1 --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test tests/Tracker.Tests/Tracker.Tests.csproj -c Release --no-build --no-restore -m:1 --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

    if (-not $SkipUiSmoke) {
        $smokeRoot = Join-Path $PSScriptRoot ('.artifacts/ui-smoke/' + [Guid]::NewGuid().ToString('N'))
        foreach ($theme in @('light', 'dark')) {
            $smokeData = Join-Path $smokeRoot $theme
            dotnet tools/Tracker.UiSmoke/bin/Release/net10.0-windows/Tracker.UiSmoke.dll --data-dir $smokeData --theme $theme
            if ($LASTEXITCODE -ne 0) { throw "UI smoke tests failed for $theme. See $smokeData" }
        }
    }

    if ($Publish) {
        $releaseDirectory = Join-Path $PSScriptRoot 'artifacts/publish/win-x64'
        dotnet publish src/Tracker.App/Tracker.App.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -m:1 -o $releaseDirectory --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $releaseDirectory
        $archivePath = Join-Path $PSScriptRoot 'artifacts/SimpleShiftTracker-win-x64.zip'
        Compress-Archive -Path (Join-Path $releaseDirectory '*') -DestinationPath $archivePath -Force
        Write-Output "Ready: $archivePath"
    }
}
finally { Pop-Location }
