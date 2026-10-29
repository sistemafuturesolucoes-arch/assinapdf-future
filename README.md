# AssinaPDF Desktop

Aplicativo desktop da Future Soluções para leitura local e futura assinatura digital de PDFs. O projeto fica isolado em `apps/assinapdf-desktop` para que suas dependências não interfiram no CRM Vision.

## Estado atual

- Aplicativo Windows x64 com .NET 10 / Windows Forms.
- Visualizador integrado baseado no Microsoft Edge WebView2 para abrir PDFs localmente e usar os controles de leitura fornecidos pelo visualizador.
- Abrir por seletor de arquivo ou arrastar um PDF para a janela.
- Consulta local ao repositório Pessoal de certificados do usuário atual do Windows, mostrando titular, emissor, validade e presença de chave privada.
- Workflow independente de build/publicação.
- A assinatura criptográfica PAdES ainda não está habilitada. O botão de assinatura informa explicitamente essa limitação e não altera o PDF.

**Esta é uma versão de desenvolvimento, não um instalador final para distribuição.** A listagem de certificados não equivale a validar a cadeia, revogação, carimbo do tempo ou conformidade ICP-Brasil.

## Requisitos

- Windows 10/11 x64 atualizado; Windows 11 recomendado.
- .NET 10 SDK para desenvolvimento.
- Microsoft Edge WebView2 Runtime. Muitos Windows atuais já o possuem, mas o instalador final deverá detectar/verificar sua presença.
- PowerShell ou terminal com `dotnet`.

## Compilar

Na raiz do repositório:

```powershell
dotnet restore apps/assinapdf-desktop/src/AssinaPDF.Desktop/AssinaPDF.Desktop.csproj
dotnet build apps/assinapdf-desktop/src/AssinaPDF.Desktop/AssinaPDF.Desktop.csproj -c Release
dotnet publish apps/assinapdf-desktop/src/AssinaPDF.Desktop/AssinaPDF.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/assinapdf/win-x64
```

## Dependência de visualização

- `Microsoft.Web.WebView2` — pacote Microsoft para hospedar WebView2 em Windows Forms. O renderizador PDF é fornecido pelo runtime Microsoft Edge; não é uma biblioteca de assinatura PDF.
- A assinatura deverá usar uma biblioteca PAdES cuja licença e condições de redistribuição sejam verificadas antes de adoção. A chave privada deve permanecer no Windows ou em token/cartão e ser usada sem exportação.

Consulte [ROADMAP.md](docs/ROADMAP.md), [ARCHITECTURE.md](docs/ARCHITECTURE.md) e [SECURITY.md](SECURITY.md).
