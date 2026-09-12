param(
    [string]$ProviderConfig = 'config/providers.local.json',
    [string]$ProviderId = 'compatible'
)

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet restore Niuery.Agent.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw '锁定依赖还原失败。' }
    dotnet build Niuery.Agent.slnx --no-restore
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    dotnet test Niuery.Agent.slnx --no-build --logger 'trx;LogFileName=stage1.trx'
    if ($LASTEXITCODE -ne 0) { throw '离线契约测试未通过。' }
    if (!(Test-Path -LiteralPath $ProviderConfig)) {
        Write-Host '离线验收通过；缺少本地模型配置，真实模型验收尚未通过。请参照 config/providers.example.json 设置。'
        exit 2
    }
    dotnet run --no-build --project src/Niuery.Agent.Diagnostics -- probe $ProviderConfig $ProviderId
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
