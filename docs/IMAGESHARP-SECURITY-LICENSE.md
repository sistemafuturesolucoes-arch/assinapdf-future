# ImageSharp: dependência transitiva e política de software gratuito

## Decisão do projeto

O AssinaPDF não deve exigir uma chave paga nem uma licença adicional para compilar ou executar suas funções.

Por isso, o projeto **não referencia SixLabors.ImageSharp diretamente**. A biblioteca permanece apenas como dependência transitiva de `PdfSharpCore`, utilizado pelo pacote de documentos fiscais `Econet.DanfeDacte`.

A página do NuGet de PdfSharpCore informa que ImageSharp e SixLabors.Fonts são licenciados sob Apache 2.0 quando distribuídos como parte do PdfSharpCore. Essa é a modalidade de uso que estamos preservando: dependência transitiva, sem adicionar ImageSharp como pacote direto do AssinaPDF.

Referência: https://www.nuget.org/packages/PdfSharpCore/1.3.67

## Segurança

Em setembro de 2026 foram publicados avisos de segurança que afetam versões antigas do ImageSharp. Não devemos esconder esses avisos, desativar NuGet Audit ou declarar que a dependência está corrigida sem verificar a versão efetivamente resolvida no restore.

O pacote PdfSharpCore declara ImageSharp com versão mínima `1.0.4`, sem limite superior. Ao manter a dependência transitiva, a versão efetivamente selecionada pelo NuGet deve ser conferida no ambiente local após o restore.

Auditoria:

```powershell
dotnet list .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj package --include-transitive
dotnet list .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj package --vulnerable --include-transitive
```

Se o NuGet selecionar uma versão vulnerável, a correção deve ser feita sem introduzir uma licença comercial obrigatória ao produto. As alternativas são substituir a dependência que traz ImageSharp por uma biblioteca PDF/DANFE com licença permissiva adequada, ou avaliar uma versão transitiva corrigida cuja licença permita essa distribuição sem chave. Não adicionar uma referência direta a ImageSharp 4.x ou 3.2.x como solução automática.

## Regras

- Não exigir chave `SIXLABORS_LICENSE_KEY`.
- Não armazenar licença ou credencial de fornecedor no repositório.
- Não adicionar `SixLabors.ImageSharp` como PackageReference direto.
- Não desativar o NuGet Audit.
- Não ocultar alertas de segurança.
- Manter a proposta de software gratuito e dependências compatíveis com a distribuição planejada.
- Validar a versão resolvida, build e testes localmente antes de considerar a alteração concluída.
