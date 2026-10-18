param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'MicMini.Desktop/MicMini.Desktop.csproj'
$releasePath = Join-Path $projectRoot 'dist/MicMiniDesktop'
if ($Test) {
    & dotnet run --project (Join-Path $projectRoot 'tests/protocol-selftest/ProtocolSelfTest.csproj')
    if ($LASTEXITCODE -ne 0) { throw '协议测试失败。' }
    & dotnet run --project (Join-Path $projectRoot 'tests/audio-selftest/AudioSelfTest.csproj')
    if ($LASTEXITCODE -ne 0) { throw '音频测试失败。' }
    & dotnet run --project (Join-Path $projectRoot 'tests/capability-selftest/CapabilitySelfTest.csproj')
    if ($LASTEXITCODE -ne 0) { throw '能力解析测试失败。' }
    $fixturePath = Join-Path (Split-Path $projectRoot -Parent) 'analysis/evidence/mic-mini-bulk-readonly.log'
    & dotnet run --project (Join-Path $projectRoot 'tests/v3-state-selftest/V3StateSelfTest.csproj') -- $fixturePath
    if ($LASTEXITCODE -ne 0) { throw '状态回放测试失败。' }
    & dotnet run --project (Join-Path $projectRoot 'tests/session-offline/SessionOffline.csproj') -- $fixturePath
    if ($LASTEXITCODE -ne 0) { throw '通信会话测试失败。' }
    & dotnet run --project (Join-Path $projectRoot 'tests/firmware-protocol-selftest/FirmwareProtocolSelfTest.csproj')
    if ($LASTEXITCODE -ne 0) { throw '固件协议测试失败。' }
    & dotnet run --project (Join-Path $projectRoot 'tests/firmware-container-selftest/FirmwareContainerSelfTest.csproj')
    if ($LASTEXITCODE -ne 0) { throw '固件容器测试失败。' }
    & dotnet run --project (Join-Path $projectRoot 'tests/firmware-session-selftest/FirmwareSessionSelfTest.csproj')
    if ($LASTEXITCODE -ne 0) { throw '固件传输会话测试失败。' }
    & dotnet run --project (Join-Path $projectRoot 'tests/firmware-catalog-selftest/FirmwareCatalogSelfTest.csproj')
    if ($LASTEXITCODE -ne 0) { throw '官方固件目录测试失败。' }
    & dotnet run --project (Join-Path $projectRoot 'tests/firmware-ui-selftest/FirmwareUiSelfTest.csproj')
    if ($LASTEXITCODE -ne 0) { throw '固件窗口保护测试失败。' }
    & dotnet run --project (Join-Path $projectRoot 'tests/tray-ui-selftest/TrayUiSelfTest.csproj')
    if ($LASTEXITCODE -ne 0) { throw '电量与托盘界面测试失败。' }
}
& dotnet publish $projectFile -c Release --self-contained false -o $releasePath --nologo
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $releasePath -Force
Copy-Item -LiteralPath (Join-Path $projectRoot '安装USB参数接口.md') -Destination $releasePath -Force
Copy-Item -LiteralPath (Join-Path $projectRoot '../docs/Mic桌面端0.3.0更新说明.md') -Destination $releasePath -Force
Copy-Item -LiteralPath (Join-Path $projectRoot '../docs/Mic桌面端0.3.1更新说明.md') -Destination $releasePath -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'MicMini.Desktop/Assets/图标来源.md') -Destination $releasePath -Force
Write-Output "程序已生成：$(Join-Path $releasePath 'MicMini.Desktop.exe')"
