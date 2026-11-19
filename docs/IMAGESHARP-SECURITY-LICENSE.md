# ImageSharp: resolução da dependência vulnerável

## Decisão aplicada

O AssinaPDF CrossPlatform utiliza `Econet.DanfeDacte` para gerar DACTE. `PdfSharpCore` não deve ser referenciado diretamente porque a versão 1.3.67 introduz a dependência vulnerável `SixLabors.ImageSharp` 1.0.4. A geração fiscal está implementada por:

- NF-e / DANFE: `Danfe.NFe.Core` (projeto ThirdParty, baseado em PDFsharp 6).
- CT-e / DACTE: `Econet.DanfeDacte` 2.2.0.1 (inclui os modelos CT-e 57 e CT-e OS 67; a API utilizada pelo gerador está no namespace `Zion.NFe.Danfe`).
- NFS-e / DANFSe: `PDFsharp` e `ZXing.Net`.

`Econet.DanfeDacte` 2.2.0.1 foi mantido/restaurado como dependência do gerador de CT-e. O cadastro dessa versão no NuGet informa que ela não declara dependências de pacote; a compilação e a geração real do DACTE devem confirmar que o assembly funciona sem `PdfSharpCore` no grafo.

Não adicionamos chave/licença Six Labors, não usamos ImageSharp 3.2/4.x e não devemos aceitar a reintrodução de `PdfSharpCore` 1.3.67 sem nova avaliação de segurança.

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
