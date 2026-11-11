# NFe.Danfe.PdfSharp

Gerador de **DANFE NF-e modelo 55** em PDF, **sem SixLabors.ImageSharp**.

Fork do [Danfe.NFe.Core](https://github.com/bsmoreno2910/Danfe.NFe.Core) 2.0.1 portado de PdfSharpCore para
[PDFsharp](https://github.com/empira/PDFsharp) 6. Mesmos namespaces e mesma API pública: serve de substituto direto.

Mantido no melhor esforço.

## Por que existe

O `Danfe.NFe.Core` 2.0.1 depende do `PdfSharpCore` 1.3.65, que puxa o `SixLabors.ImageSharp` 1.0.4 (8 advisories
no NuGetAudit, 3 de severidade alta). Subir o ImageSharp para a 2.1.13, a última da linha 2.x, ainda deixa 5 sem
correção:

- [GHSA-j3p4-wp97-rph4](https://github.com/advisories/GHSA-j3p4-wp97-rph4)
- [GHSA-j9gm-c75j-xc9q](https://github.com/advisories/GHSA-j9gm-c75j-xc9q)
- [GHSA-jjfr-hcj7-qf5w](https://github.com/advisories/GHSA-jjfr-hcj7-qf5w)
- [GHSA-gwg2-r3hj-4w44](https://github.com/advisories/GHSA-gwg2-r3hj-4w44)
- [GHSA-wmxv-xphr-5c9g](https://github.com/advisories/GHSA-wmxv-xphr-5c9g)

A correção só existe a partir da 4.1.2, que exige a licença Six Labors Split e removeu APIs que o PdfSharpCore usa.
Este pacote troca o PdfSharpCore pelo PDFsharp 6, que não depende do ImageSharp nem de outra biblioteca de imagem.

## Créditos

[SilverCard (DanfeSharp)](https://github.com/SilverCard/DanfeSharp) →
[Alexandre Laranjeiras (Zion.NFe.Danfe)](https://github.com/Laranjeiras/Zion.NFe.Danfe) →
[Bruno Moreno (Danfe.NFe.Core)](https://github.com/bsmoreno2910/Danfe.NFe.Core) →
este fork.

## Migração

Troque o `PackageReference`. Os namespaces (`Danfe.NFe.Core`, `Danfe.NFe.Core.Modelo` etc.) e o nome do assembly
continuam os mesmos.

```xml
<!-- antes -->
<PackageReference Include="Danfe.NFe.Core" Version="2.0.1" />
<!-- depois -->
<PackageReference Include="NFe.Danfe.PdfSharp" Version="1.0.0" />
```

Diferenças em relação ao 2.0.1:

| | Danfe.NFe.Core 2.0.1 | NFe.Danfe.PdfSharp |
|---|---|---|
| `DanfeDoc.PdfDocument` | `PdfSharpCore.Pdf.PdfDocument` | `PdfSharp.Pdf.PdfDocument`. Só afeta quem usa a propriedade com tipos do PdfSharpCore. |
| Logo GIF ou TGA | aceito | `NotSupportedException` (ver [formatos](#formatos-de-logo)) |
| `AdicionarLogoPdf(Stream)` | lançava `UnknownImageFormatException` | funciona |
| Logo JPEG | recomprimido pelo ImageSharp | embutido como está, sem perda |
| Logo PNG com transparência | canal alfa descartado | transparência preservada |
| Texto de rodapé padrão | "Desenvolvido por Bruno Moreno / github.com/bsmoreno2910" | vazio (defina com `DefinirTextoCreditos`) |
| Sistema sem nenhuma fonte (ex.: imagem Docker do .NET) | `FileNotFoundException: No Fonts installed on this device!` | usa a Liberation Serif embutida |
| Targets | `netstandard2.0`, `net8.0` | `netstandard2.0`, `net8.0`, `net10.0` |

O layout é o mesmo: em 57 cenários comparados pixel a pixel no Windows (com o mesmo texto de rodapé nos dois),
todos têm o mesmo número de páginas e os mesmos glifos nas mesmas posições. A única diferença no conteúdo é a precisão das coordenadas dos traços (o PDFsharp
grava 4 casas decimais, o PdfSharpCore gravava 3), o que muda o antisserrilhado das linhas em no máximo 3/255.

## Uso

```csharp
using Danfe.NFe.Core;
using Danfe.NFe.Core.Modelo;

var modelo = DanfeViewModelCreator.CriarDeArquivoXml("nfe.xml");

using (var danfe = new DanfeDoc(modelo))
{
    danfe.AdicionarLogoImagem("logo.png"); // opcional
    danfe.Gerar();
    danfe.Salvar("danfe.pdf");
}

// Ou os bytes em memória:
using (var ms = new MemoryStream())
using (var danfe = new DanfeDoc(modelo))
{
    danfe.Gerar();
    byte[] bytes = danfe.ObterPdfBytes(ms);
}
```

O rodapé de cada página sai vazio por padrão. Para escrever um texto ali, use
`modelo.DefinirTextoCreditos("...")` (o valor é global para o processo).

## Formatos de logo

`AdicionarLogoImagem(string)` / `AdicionarLogoImagem(Stream)`:

- **JPEG**
- **PNG**: RGB, RGBA e tons de cinza (com ou sem alfa) com 8 bits por canal; paleta de 1, 4 ou 8 bits
- **BMP** sem compressão

`AdicionarLogoPdf(string)` / `AdicionarLogoPdf(Stream)`: primeira página de um PDF (logo vetorial).

Qualquer outro formato lança `NotSupportedException` na própria chamada de `AdicionarLogo*`, com o motivo na
mensagem. Entre eles: GIF e TGA (que o 2.0.1 aceitava via ImageSharp), TIFF, WebP, PNG de 16 bits por canal, PNG em
tons de cinza com menos de 8 bits e PNG com paleta de 2 bits. Converta o logo para PNG de 8 bits ou JPEG antes.

O stream pode ser descartado logo depois de `AdicionarLogo*`: o conteúdo é copiado.

## Fontes

O DANFE usa Times New Roman. O PDFsharp 6 não procura fontes no sistema sozinho, então o pacote traz um resolver
próprio que tenta, nesta ordem:

1. **Times New Roman do sistema** (Windows, macOS e Linux com `ttf-mscorefonts-installer`). O PDF sai igual ao do
   Danfe.NFe.Core no Windows.
2. **Liberation Serif do sistema** (pacote `fonts-liberation`), com as mesmas métricas da Times New Roman.
3. **Liberation Serif embutida** no assembly. É o piso: o DANFE é gerado mesmo numa imagem sem fonte nenhuma,
   como `mcr.microsoft.com/dotnet/aspnet`, sem instalar nada.

Como as métricas são as mesmas, a paginação e as posições não mudam entre as três opções. Só o desenho dos glifos
muda um pouco.

### Usando o seu próprio resolver

O resolver do pacote é registrado em `GlobalFontSettings.FallbackFontResolver` no primeiro `new DanfeDoc(...)`.
O `GlobalFontSettings.FontResolver` fica livre para você:

- Se você definir `GlobalFontSettings.FontResolver`, ele é consultado primeiro. O do pacote só entra quando o seu
  devolver `null` para a família pedida.
- Se você já tiver definido um `FallbackFontResolver`, o pacote não o substitui. Nesse caso, os seus resolvers
  precisam resolver "Times New Roman".
- O PDFsharp não deixa trocar resolver depois que alguma fonte foi usada. Configure o seu antes de criar o primeiro
  `DanfeDoc` (ou de usar o PDFsharp para qualquer outra coisa).

```csharp
PdfSharp.Fonts.GlobalFontSettings.FontResolver = new MeuFontResolver();
```

## Dependências

- `PDFsharp` **6.2.4**, a última versão sob MIT. A partir da 6.5.0 o PDFsharp passou a ser AGPL-3.0; não suba a
  versão sem avaliar a licença.
- `System.Security.Cryptography.Pkcs` 8.0.2, referenciado direto para substituir a 8.0.1 que o PDFsharp puxa
  ([GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx),
  [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7)).

## Build e testes

```bash
dotnet build Danfe.NFe.Core.sln
dotnet test Danfe.NFe.Core.sln
```

Os XMLs de teste são sintéticos: todos os CNPJs, nomes e endereços são fictícios.

| Projeto | Framework | Descrição |
|---------|-----------|-----------|
| `Danfe.NFe.Core` | netstandard2.0 / net8.0 / net10.0 | Biblioteca (pacote `NFe.Danfe.PdfSharp`). |
| `Danfe.NFe.Core.WebService` | net8.0 | API REST de exemplo (`POST /api/xml/pdf/gerar`). |
| `Danfe.NFe.Core.Tests` | net8.0 / net10.0 | Testes MSTest. |

## Licença

Código: MIT (ver [LICENSE](LICENSE)), com os copyrights de SilverCard, Alexandre Laranjeiras, Bruno Moreno e deste
fork.

A fonte Liberation Serif embutida é distribuída sob a SIL Open Font License 1.1
(ver [LICENSE-LiberationFonts.txt](Danfe.NFe.Core/Fonts/LICENSE-LiberationFonts.txt)). Copyright (c) 2010 Google
Corporation, Copyright (c) 2012 Red Hat, Inc. "Liberation" é nome de fonte reservado.
