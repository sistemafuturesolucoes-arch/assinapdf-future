# AssinaPDF Desktop

Aplicativo desktop da Future Soluções para leitura local e assinatura digital de PDFs. O projeto fica isolado em `apps/assinapdf-desktop` para que suas dependências não interfiram no CRM Vision.

## Estado atual

- Aplicativo Windows x64 com .NET 10 / Windows Forms.
- Visualizador integrado baseado no Microsoft Edge WebView2.
- Abrir PDF por seletor de arquivo ou arrastar o arquivo para a janela.
- Consulta local ao repositório Pessoal de certificados do usuário atual do Windows.
- Fluxo de assinatura PAdES básico (Baseline-B-B) usando o certificado selecionado, sem exportar a chave privada.
- Configuração de aparência visível por página e coordenadas PDF; a seleção clicando diretamente sobre o preview ainda está pendente.
- Verificação local inicial de integridade e assinatura criptográfica de PDFs assinados, sem alegar validação da cadeia, revogação ou carimbo do tempo.
- O PDF assinado é salvo em um arquivo separado; o arquivo original é preservado.
- Workflow independente de build/publicação, sem deploy do CRM.

**Ainda é uma versão de desenvolvimento, não um instalador final para distribuição.** A assinatura precisa ser testada com certificados reais, validadores independentes e documentos variados antes de uso em produção. O perfil básico não inclui, por si só, carimbo do tempo ou evidência de revogação de longo prazo. O êxito da operação não garante que toda cadeia seja confiável nem confirma automaticamente conformidade ICP-Brasil.

## Requisitos

- Windows 10/11 x64 atualizado; Windows 11 recomendado.
- .NET 10 SDK para desenvolvimento.
- Microsoft Edge WebView2 Runtime para o visualizador.
- Certificado instalado no repositório Pessoal do usuário atual do Windows e acesso à chave privada.
- PowerShell ou terminal com `dotnet`.

## Compilar

Na raiz do repositório:

```powershell
dotnet restore apps/assinapdf-desktop/src/AssinaPDF.Desktop/AssinaPDF.Desktop.csproj
dotnet build apps/assinapdf-desktop/src/AssinaPDF.Desktop/AssinaPDF.Desktop.csproj -c Release
dotnet publish apps/assinapdf-desktop/src/AssinaPDF.Desktop/AssinaPDF.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/assinapdf/win-x64
```

## Dependências

- `Microsoft.Web.WebView2` — hospeda o WebView2 em Windows Forms; o runtime Microsoft Edge fornece o visualizador PDF.
- `SimpleSign.PAdES` 0.9.2 — motor PAdES sob licença MIT. A versão está fixada para builds reproduzíveis; por ser uma dependência recente, deve passar por revisão de código, testes e validação de interoperabilidade antes da distribuição comercial.

Veja [inventário de dependências e licenças](docs/DEPENDENCIES.md), [ROADMAP.md](docs/ROADMAP.md), [ARCHITECTURE.md](docs/ARCHITECTURE.md) e [SECURITY.md](SECURITY.md).
