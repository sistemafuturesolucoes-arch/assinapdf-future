# AssinaPDF Desktop

Aplicativo desktop da Future Soluções para leitura e assinatura digital de PDFs. Este projeto vive em uma pasta isolada do CRM Vision para que a compilação e as dependências do aplicativo não interfiram no site.

## Fase atual: fundação

- Projeto Windows x64 com .NET 10 / Windows Forms.
- Estrutura isolada em `apps/assinapdf-desktop`.
- Fluxo de CI separado para compilar e publicar um artefato de teste.
- Requisitos de segurança, arquitetura e validação documentados.

**Ainda não é uma versão pronta para uso ou distribuição.** A visualização real de PDF, assinatura PAdES, instalador assinado e validação formal serão implementados e testados em etapas posteriores.

## Requisitos de desenvolvimento

- Windows 11 x64 recomendado.
- .NET 10 SDK.
- PowerShell ou terminal com `dotnet`.

## Compilar

Na raiz do repositório:

```powershell
dotnet restore apps/assinapdf-desktop/src/AssinaPDF.Desktop/AssinaPDF.Desktop.csproj
dotnet build apps/assinapdf-desktop/src/AssinaPDF.Desktop/AssinaPDF.Desktop.csproj -c Release
dotnet publish apps/assinapdf-desktop/src/AssinaPDF.Desktop/AssinaPDF.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/assinapdf/win-x64
```

## Próximas etapas

Consulte [ROADMAP.md](docs/ROADMAP.md), [ARCHITECTURE.md](docs/ARCHITECTURE.md) e [SECURITY.md](SECURITY.md).
