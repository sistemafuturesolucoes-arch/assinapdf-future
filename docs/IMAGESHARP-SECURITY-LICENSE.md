# ImageSharp: resolução da dependência vulnerável

## Decisão aplicada

O CrossPlatform não utiliza diretamente as APIs de `Econet.DanfeDacte` nem de `PdfSharpCore`. A geração fiscal está implementada por componentes já presentes no projeto:

- NF-e / DANFE: `Danfe.NFe.Core` (projeto ThirdParty, baseado em PDFsharp 6).
- CT-e / DACTE: `Zion.NFe.Danfe`.
- NFS-e / DANFSe: `PDFsharp` e `ZXing.Net`.

As referências redundantes a `Econet.DanfeDacte` e `PdfSharpCore` foram removidas do projeto CrossPlatform. O `PdfSharpCore 1.3.67` era a origem transitiva de `SixLabors.ImageSharp 1.0.4` e dos avisos de segurança.

Não substituímos o pacote por ImageSharp 3.2/4.x nem adicionamos chave/licença Six Labors. A remoção da dependência sem uso elimina a cadeia vulnerável sem alterar os geradores atualmente utilizados.

## Verificações obrigatórias

Após restore, a validação do GitHub Actions verifica que `SixLabors.ImageSharp` não aparece no grafo transitivo e executa os testes do projeto, incluindo o teste de geração de DANFE a partir do XML sintético.

Auditoria local:

```powershell
dotnet list .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj package --include-transitive
dotnet list .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj package --vulnerable --include-transitive
```

## Regras para futuras alterações

- Não reintroduzir `Econet.DanfeDacte` ou `PdfSharpCore` sem confirmar uso real no código e testes de geração.
- Não adicionar `SixLabors.ImageSharp` como dependência direta.
- Não desativar o NuGet Audit nem suprimir alertas.
- Se outro componente fiscal passar a exigir PdfSharpCore, avaliar a substituição do componente e testar DANFE/DACTE antes de restaurar essa cadeia.
