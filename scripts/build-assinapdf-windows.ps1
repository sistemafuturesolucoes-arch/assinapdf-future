$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src/AssinaPDF.CrossPlatform/AssinaPDF.MacPrototype.csproj"
$Installer = Join-Path $Root "installer-crossplatform/AssinaPDF.CrossPlatform.wixproj"
$ProjectText = Get-Content $Project -Raw
$VersionMatch = [regex]::Match($ProjectText, '<Version>([^<]+)</Version>')
if (-not $VersionMatch.Success) { throw "A versão do AssinaPDF não foi encontrada no projeto." }
$Version = $VersionMatch.Groups[1].Value
$RunId = Get-Date -Format "yyyyMMdd-HHmmss"
$RunRoot = Join-Path $Root "artifacts/release-assinapdf/$RunId"
$PublishDir = Join-Path $RunRoot "publish/win-x64"
$ArtifactDir = Join-Path $RunRoot "installers"
$WixOutput = Join-Path $RunRoot "wix-bin"
$WixIntermediate = Join-Path $RunRoot "wix-obj"

New-Item -ItemType Directory -Force -Path $PublishDir, $ArtifactDir, $WixOutput, $WixIntermediate | Out-Null

Write-Host "Publicando AssinaPDF Windows x64 (self-contained)..."
dotnet restore $Project
if ($LASTEXITCODE -ne 0) { throw "Falha no restore do AssinaPDF." }

dotnet publish $Project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None `
  "-p:Version=$Version" "-p:AssemblyVersion=$($Version).0" "-p:FileVersion=$($Version).0" "-p:InformationalVersion=$Version" `
  -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar o AssinaPDF Windows x64." }

Write-Host "Gerando instalador MSI..."
$PublishProperty = $PublishDir.TrimEnd('\') + '\'
dotnet build $Installer -c Release -p:InstallerPlatform=x64 "-p:ProductVersion=$Version" "-p:PublishDir=$PublishProperty" "-p:BaseOutputPath=$WixOutput/" "-p:BaseIntermediateOutputPath=$WixIntermediate/"
if ($LASTEXITCODE -ne 0) { throw "Falha ao gerar o MSI." }

$Msi = Get-ChildItem $WixOutput -Filter "AssinaPDF-CrossPlatform-Setup-x64.msi" -File -Recurse |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $Msi) { throw "O MSI não foi encontrado na saída do WiX." }

$Destination = Join-Path $ArtifactDir $Msi.Name
Copy-Item $Msi.FullName $Destination -Force
$Hash = (Get-FileHash $Destination -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "Instalador Windows gerado:"
Write-Host $Destination
Write-Host "SHA-256: $Hash"
