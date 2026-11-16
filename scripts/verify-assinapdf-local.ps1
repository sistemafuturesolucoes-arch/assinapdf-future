[CmdletBinding()]
param(
    [string]$ProjectPath = "src/AssinaPDF.CrossPlatform/AssinaPDF.MacPrototype.csproj",
    [string]$TestsPath = "tests/AssinaPDF.CrossPlatform.Tests/AssinaPDF.CrossPlatform.Tests.csproj"
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
Write-Host "== 2/4 Auditoria da arvore de dependencias =="

$packageGraph = & dotnet list $ProjectPath package --include-transitive 2>&1
$graphExitCode = $LASTEXITCODE
$packageGraph | ForEach-Object { Write-Host $_ }
if ($graphExitCode -ne 0) {
    throw "A leitura da arvore de pacotes falhou (exit code $graphExitCode)."
}

$legacyPackagePattern = "(?im)^\s*>\s+(?:Econet\.DanfeDacte(?:\s|$)|PdfSharpCore(?:\s|$)|SixLabors\.(?:ImageSharp|Fonts)(?:\s|$))"
$legacyPackages = ($packageGraph | Out-String) -match $legacyPackagePattern
if ($legacyPackages) {
    throw "Gate reprovado: a arvore ainda contem Econet.DanfeDacte/PdfSharpCore/SixLabors. Verifique o submodulo e as referencias do projeto."
}

Write-Host "Arvore limpa: Econet.DanfeDacte, PdfSharpCore e SixLabors nao aparecem." -ForegroundColor Green

Write-Host ""
Write-Host "Auditoria de avisos de seguranca =="
$vulnerabilityReport = & dotnet list $ProjectPath package --vulnerable --include-transitive 2>&1
$vulnerabilityExitCode = $LASTEXITCODE
$vulnerabilityReport | ForEach-Object { Write-Host $_ }
if ($vulnerabilityExitCode -ne 0) {
    throw "A auditoria de vulnerabilidades falhou (exit code $vulnerabilityExitCode)."
}
if (($vulnerabilityReport | Out-String) -match "(?i)SixLabors\.ImageSharp") {
    throw "Gate de seguranca reprovado: ImageSharp ainda aparece no relatorio de vulnerabilidades."
}

Write-Host ""
Write-Host "== 3/4 Compilar Release =="
Invoke-DotNet @("build", $ProjectPath, "--configuration", "Release", "--no-restore")

Write-Host ""
Write-Host "== 4/4 Executar testes automatizados =="
Invoke-DotNet @("test", $TestsPath, "--configuration", "Release")

Write-Host ""
Write-Host "Verificacao local concluida: arvore sem a cadeia antiga, build Release e testes aprovados." -ForegroundColor Green
