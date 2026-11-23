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

## Atualizações do aplicativo

O fluxo de atualização usa o Supabase apenas para consultar os metadados da versão publicada e o GitHub Releases para hospedar os instaladores.

- A versão instalada é lida dos metadados do assembly, definidos no `AssinaPDF.MacPrototype.csproj`.
- O cliente identifica Windows x64, macOS Intel ou macOS Apple Silicon.
- O download aceita somente links HTTPS de assets do GitHub Releases e valida a extensão esperada por plataforma.
- O SHA-256 é conferido antes de liberar o instalador. Pacotes acima de 1,5 GB são rejeitados.
- Windows inicia o MSI com elevação UAC; o pacote WiX usa `MajorUpgrade` para atualizar a instalação existente.
- macOS abre o DMG; o usuário conclui a substituição arrastando o app para Aplicativos.
- Os scripts de build usam a mesma versão do projeto para os metadados do app e do instalador.

A próxima compilação do cliente com o atualizador está versionada como **0.4.3**. O instalador aprovado **0.4.2** permanece preservado. Para homologar uma atualização real, primeiro instalar manualmente o pacote 0.4.3 e, em seguida, publicar um pacote 0.4.4 para testar a detecção e a instalação sobre a versão anterior.

Não publicar uma release enquanto o instalador real, a URL pública e o SHA-256 do arquivo final não estiverem conferidos. Para distribuição pública, o MSI precisa de assinatura Authenticode e o app macOS precisa de Developer ID e notarização.

## Assinatura dos instaladores

O MSI ainda precisa de certificado Authenticode para distribuição pública. O pacote macOS gerado pelo script recebe assinatura ad-hoc apenas para homologação local; distribuição pública exige Developer ID e notarização da Apple. Não distribuir os artefatos de teste como releases públicos antes dessas etapas.
