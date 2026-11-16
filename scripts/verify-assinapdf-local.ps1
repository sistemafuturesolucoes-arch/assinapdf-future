[CmdletBinding()]
param(
    [string]$ProjectPath = "src/AssinaPDF.CrossPlatform/AssinaPDF.MacPrototype.csproj",
    [string]$TestsPath = "tests/AssinaPDF.CrossPlatform.Tests/AssinaPDF.CrossPlatform.Tests.csproj",
    [switch]$RequireNoImageSharp
)

$ErrorActionPreference = "Stop"

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Comando dotnet falhou (exit code $LASTEXITCODE): dotnet $($Arguments -join ' ')"
    }
}

Write-Host "== AssinaPDF CrossPlatform: verificacao local ==" -ForegroundColor Cyan
Write-Host "Projeto: $ProjectPath"
Write-Host ""

Write-Host "== 1/4 Restaurar dependencias =="
Invoke-DotNet @("restore", $ProjectPath)

Write-Host ""
Write-Host "== 2/4 Auditoria de pacotes vulneraveis =="
$vulnerabilityReport = & dotnet list $ProjectPath package --vulnerable --include-transitive 2>&1
$vulnerabilityExitCode = $LASTEXITCODE
$vulnerabilityReport | ForEach-Object { Write-Host $_ }
if ($vulnerabilityExitCode -ne 0) {
    throw "A auditoria de pacotes falhou (exit code $vulnerabilityExitCode)."
}

$imageSharpFound = ($vulnerabilityReport | Out-String) -match "(?i)SixLabors\.ImageSharp"
if ($imageSharpFound) {
    Write-Warning "SixLabors.ImageSharp ainda aparece na arvore de dependencias. A migracao DACTE ainda nao pode ser considerada concluida."
    if ($RequireNoImageSharp) {
        throw "Gate de seguranca reprovado: ImageSharp ainda esta presente."
    }
} else {
    Write-Host "ImageSharp nao apareceu no relatorio de vulnerabilidades." -ForegroundColor Green
}

Write-Host ""
Write-Host "== 3/4 Compilar Release =="
Invoke-DotNet @("build", $ProjectPath, "--configuration", "Release", "--no-restore")

Write-Host ""
Write-Host "== 4/4 Executar testes automatizados =="
Invoke-DotNet @("test", $TestsPath, "--configuration", "Release")

Write-Host ""
Write-Host "Verificacao local concluida. A ausencia de ImageSharp deve ser confirmada com -RequireNoImageSharp." -ForegroundColor Green
