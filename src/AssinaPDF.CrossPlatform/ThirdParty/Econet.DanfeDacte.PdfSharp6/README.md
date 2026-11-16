# Econet DACTE — port para PDFsharp 6

Este projeto compila o código-fonte upstream do Econet.DanfeDacte, fixado no commit
`20d3537ae7453561047ce9efee1b70ae3cbe340b`, por meio do submódulo
`Econet.DanfeDacte.Source`.

A camada de compatibilidade `PdfClownCompat.PdfSharp6.cs` substitui a implementação
baseada em PdfSharpCore pela API PDFsharp 6.2.4. O projeto não referencia
PdfSharpCore, SixLabors.Fonts nem SixLabors.ImageSharp.

## Preparar o clone local

Depois de atualizar a branch, inicialize o submódulo:

```powershell
git submodule update --init --recursive
```

O projeto mantém o assembly name e os namespaces públicos `Zion.NFe.Danfe` para
preservar a integração existente do AssinaPDF. Os dois modelos DACTE 57 e DACTE OS
67 continuam usando a API upstream `GeradorPdf.GerarDactePdfDeXml`.

## Validação obrigatória local

Execute `scripts/verify-assinapdf-local.ps1` e, depois, gere DACTE com XMLs
autorizados reais dos modelos 57 e 67. Confira QR Code, chave, protocolo, dados
do emitente/tomador, impostos, documentos originários/referenciados e paginação.
Não considerar a migração aprovada até validar os dois PDFs visualmente.

Código upstream sob licença MIT. As fontes Lato são distribuídas sob SIL OFL 1.1;
consulte os arquivos de licença mantidos no submódulo e em ThirdPartyLicenses.
