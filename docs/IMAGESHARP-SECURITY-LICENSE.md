# ImageSharp: atualização de segurança e licença de build

## Por que esta dependência existe

O projeto CrossPlatform usa `Econet.DanfeDacte`, que depende de `PdfSharpCore`. O pacote `PdfSharpCore 1.3.67` declara ImageSharp a partir de `1.0.4`, sem um limite superior. Sem uma fixação explícita, o restore pode selecionar uma versão antiga e vulnerável.

O projeto fixa `SixLabors.ImageSharp 4.1.3`, linha compatível com o alvo `.NET 10` e que contém as correções de segurança publicadas em setembro/outubro de 2026 para os avisos que afetavam a versão 2.1.11.

## Requisito de licença

A referência explícita a ImageSharp 4.x ativa a validação de licença da Six Labors durante o build. Portanto, o build local precisa de uma chave válida. A chave **não** deve ser enviada pelo chat, incluída no repositório, gravada no projeto ou compartilhada em logs.

A Six Labors informa que organizações com fins lucrativos e receita bruta anual inferior a USD 1 milhão podem solicitar uma licença Community gratuita, sujeita à avaliação/aprovação. Acima desse limite, o uso direto em software fechado com fins lucrativos exige licença comercial conforme os termos do fornecedor.

Solicitação e critérios: https://licensing.sixlabors.com/
Termos e planos: https://sixlabors.com/pricing/
Documentação de configuração: https://docs.sixlabors.com/articles/imagesharp/

## Configuração no Windows (PowerShell)

1. Solicite e receba sua licença diretamente da Six Labors.
2. Abra um PowerShell e defina a variável apenas na sessão atual, usando o conteúdo completo fornecido pelo fornecedor:

```powershell
$env:SIXLABORS_LICENSE_KEY = 'COLE_AQUI_O_CONTEUDO_COMPLETO_DA_LICENCA'
```

3. Execute restore, testes e build:

```powershell
dotnet restore .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj
dotnet test .\tests\AssinaPDF.CrossPlatform.Tests\AssinaPDF.CrossPlatform.Tests.csproj -c Release
dotnet build .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj -c Release
```

Os scripts de publicação do projeto herdam a variável de ambiente da sessão PowerShell. Não use o texto de exemplo como se fosse uma chave real.

## Regras de segurança

- Não adicionar `sixlabors.lic` ao Git.
- Não inserir chaves em `.csproj`, scripts, documentação, commits, issues ou logs.
- Não desativar o NuGet Audit para ocultar avisos.
- Não voltar para ImageSharp 2.1.11 apenas para evitar a validação de licença: essa versão apresentou os avisos de segurança reportados pelo NuGet.
- Não considerar a atualização concluída até que o restore, build e testes sejam executados localmente com uma licença válida e o comando de auditoria de pacotes seja repetido.

## Verificação de segurança

Após o restore, execute:

```powershell
dotnet list .\src\AssinaPDF.CrossPlatform\AssinaPDF.MacPrototype.csproj package --vulnerable --include-transitive
```

A auditoria deve confirmar que os avisos associados à versão antiga do ImageSharp deixaram de aparecer. Registre e avalie separadamente qualquer novo aviso transitivo.
