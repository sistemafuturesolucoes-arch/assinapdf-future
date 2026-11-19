# ImageSharp: resolução da dependência vulnerável

## Decisão aplicada

O AssinaPDF CrossPlatform não utiliza diretamente as APIs de `Econet.DanfeDacte` nem de `PdfSharpCore`. A geração fiscal está implementada por:

- NF-e / DANFE: `Danfe.NFe.Core` (projeto ThirdParty, baseado em PDFsharp 6).
- CT-e / DACTE: `Econet.DanfeDacte` 2.2.0.1 (inclui os modelos CT-e 57 e CT-e OS 67; a API utilizada pelo gerador está no namespace `Zion.NFe.Danfe`).
- NFS-e / DANFSe: `PDFsharp` e `ZXing.Net`.

As referências redundantes a `Econet.DanfeDacte` e `PdfSharpCore` foram removidas. O `PdfSharpCore 1.3.67` era a origem transitiva de `SixLabors.ImageSharp 1.0.4` e dos avisos de segurança.

Não substituímos o pacote por ImageSharp 3.2/4.x nem adicionamos chave/licença Six Labors. A remoção da dependência sem uso elimina essa cadeia sem alterar os geradores atualmente utilizados.

## Verificação local obrigatória

Executar no PowerShell, a partir da raiz do repositório:

```powershell
dotnet restore .\tests\AssinaPDF.CrossPlatform.Tests\AssinaPDF.CrossPlatform.Tests.csproj
dotnet list .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj package --include-transitive
dotnet list .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj package --vulnerable --include-transitive
dotnet test .\tests\AssinaPDF.CrossPlatform.Tests -c Release --no-restore
dotnet publish .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\artifacts\publish\win-x64
```

Confirmar que `SixLabors.ImageSharp` não aparece na lista transitiva, revisar alertas NuGet e verificar o executável em `artifacts\publish\win-x64`.

## Regras para futuras alterações

- Não reintroduzir `PdfSharpCore` 1.3.67 nem `SixLabors.ImageSharp` 1.0.4. `Econet.DanfeDacte` 2.2.0.1 é a dependência usada pelo gerador de CT-e e deve permanecer coberta por testes de compilação e geração.
- Não adicionar `SixLabors.ImageSharp` como dependência direta.
- Não desativar o NuGet Audit nem suprimir alertas.
- Se outro componente fiscal exigir PdfSharpCore, avaliar a substituição e testar DANFE/DACTE antes de restaurar essa cadeia.
