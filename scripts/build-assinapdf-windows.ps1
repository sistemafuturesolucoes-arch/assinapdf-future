$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src/AssinaPDF.CrossPlatform/AssinaPDF.MacPrototype.csproj"
$Installer = Join-Path $Root "installer-crossplatform/AssinaPDF.CrossPlatform.wixproj"
$PublishDir = Join-Path $Root "artifacts/publish/win-x64"
$ArtifactDir = Join-Path $Root "artifacts/installers"

New-Item -ItemType Directory -Force -Path $PublishDir, $ArtifactDir | Out-Null

Write-Host "Publicando AssinaPDF Windows x64 (self-contained)..."
dotnet restore $Project
if ($LASTEXITCODE -ne 0) { throw "Falha no restore do AssinaPDF." }

dotnet publish $Project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None `
  -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar o AssinaPDF Windows x64." }

Write-Host "Gerando instalador MSI..."
$PublishProperty = $PublishDir.TrimEnd('\') + '\'
dotnet build $Installer -c Release -p:InstallerPlatform=x64 "-p:PublishDir=$PublishProperty"
if ($LASTEXITCODE -ne 0) { throw "Falha ao gerar o MSI." }

$Msi = Get-ChildItem (Join-Path $Root "installer-crossplatform/bin/Release") -Filter "AssinaPDF-CrossPlatform-Setup-x64.msi" -Recurse |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $Msi) { throw "O MSI não foi encontrado na saída do WiX." }

$Destination = Join-Path $ArtifactDir $Msi.Name
Copy-Item $Msi.FullName $Destination -Force
$Hash = (Get-FileHash $Destination -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "Instalador Windows gerado:"
Write-Host $Destination
Write-Host "SHA-256: $Hash"
