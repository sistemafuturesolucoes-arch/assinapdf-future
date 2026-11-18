# AssinaPDF CrossPlatform - distribuição Windows e macOS

O aplicativo CrossPlatform é independente do projeto Windows legado. Os instaladores são gerados manualmente, sem GitHub Actions, na branch de desenvolvimento dedicada.

## Requisitos

- .NET 10 SDK.
- Windows x64 para gerar e testar o MSI.
- macOS com Xcode Command Line Tools (lipo, sips, iconutil, codesign, hdiutil) para montar o pacote universal.
- WiX Toolset SDK 5 é restaurado pelo projeto do instalador.

## Windows x64

Na raiz do repositório, execute no PowerShell:

```powershell
.\scripts\build-assinapdf-windows.ps1
```

O script publica o aplicativo self-contained, gera o MSI WiX e salva o instalador e o SHA-256 em uma nova pasta artifacts/release-assinapdf/<data-hora>/. As saídas anteriores não são apagadas.

## macOS Universal (Apple Silicon + Intel)

Em um Mac, execute na raiz do repositório:

```bash
chmod +x ./scripts/build-assinapdf-macos-universal.sh
./scripts/build-assinapdf-macos-universal.sh
```

O script publica osx-arm64 e osx-x64, mescla os binários Mach-O correspondentes com lipo, monta AssinaPDF.app e cria um DMG universal. A saída fica em uma nova pasta artifacts/release-assinapdf/<data-hora>/.

## Homologação obrigatória

Antes de distribuir, testar em máquinas limpas:

- Instalação, primeira abertura e desinstalação no Windows 10/11 x64.
- Abertura de PDF e PDFium em Windows e macOS Intel/Apple Silicon.
- Certificado A1, assinatura PAdES e validação independente.
- Cadastro/login, restauração de sessão, ativação de chave manual e expiração.
- Premium: bloqueio sem licença, ativação de dispositivo, limite de dispositivos e revogação pelo CRM.
- Asaas Sandbox: checkout recorrente, confirmação, atraso, estorno, cancelamento e webhooks duplicados.
- Atualização sobre versão anterior e preservação dos dados do usuário.

## Assinatura dos instaladores

O MSI ainda precisa de certificado Authenticode para distribuição pública. O pacote macOS gerado pelo script recebe assinatura ad-hoc apenas para homologação local; distribuição pública exige Developer ID e notarização da Apple. Não distribuir os artefatos de teste como releases públicos antes dessas etapas.
