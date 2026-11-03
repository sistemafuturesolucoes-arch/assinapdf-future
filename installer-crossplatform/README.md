# AssinaPDF CrossPlatform - Instalador Windows x64

## Estado

O workflow de distribuição gera um instalador MSI x64 autocontido. O pacote inclui o runtime .NET necessário para executar o aplicativo e não exige instalação prévia do .NET Desktop Runtime no computador do usuário.

O projeto e o instalador Windows legado permanecem independentes e não devem ser alterados por este pacote.

## Requisitos do computador

- Windows 10 ou Windows 11 de 64 bits.
- Permissão de administrador para instalar em Program Files.
- Não é necessário instalar .NET separadamente.

## Arquivos publicados

- Executável: `AssinaPDF.MacPrototype.exe`
- Biblioteca nativa PDFium: `pdfium.dll` e dependências correspondentes.
- Logo da Future: `logo-future.png`
- Runtime .NET 10 x64 autocontido.

## Instalação

O artefato do GitHub Actions chamado `AssinaPDF-Windows-x64-Installer` contém:

`AssinaPDF-CrossPlatform-Setup-x64.msi`

O MSI instala em:

`C:\Program Files\Future Soluções\AssinaPDF`

Também cria atalhos no Menu Iniciar e na Área de Trabalho. A desinstalação é feita em Configurações do Windows > Aplicativos > Aplicativos instalados.

## Compilação manual do MSI

Na raiz do repositório, após publicar o aplicativo:

```powershell
dotnet publish ./src/AssinaPDF.CrossPlatform/AssinaPDF.MacPrototype.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugSymbols=false -p:DebugType=None -o publish/win-x64

dotnet build ./installer-crossplatform/AssinaPDF.CrossPlatform.wixproj -c Release -p:InstallerPlatform=x64 -p:PublishDir="$PWD/publish/win-x64/"
```

O ícone Windows é gerado a partir do PNG oficial antes da publicação:

```powershell
python -m pip install Pillow
python scripts/generate_windows_icon.py
```

## Verificações obrigatórias antes de distribuir

- Instalação em Windows limpo x64.
- Inicialização pelo atalho do Menu Iniciar e da Área de Trabalho.
- Abertura e renderização de PDF com PDFium.
- Assinatura e validação com certificado A1.
- Atualização por cima de uma versão anterior.
- Desinstalação e remoção dos atalhos.
- Conferência de que documentos, certificados e dados do usuário não são removidos indevidamente.

## Segurança e distribuição

A assinatura de código Authenticode ainda deve ser configurada antes da distribuição pública. O MSI deve ser testado em uma máquina Windows limpa antes de ser considerado instalador final.
