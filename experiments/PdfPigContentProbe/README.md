# PdfPig Content Operation Probe

Isolated, read-only experiment. This project is not referenced by AssinaPDF production and does not modify PDFs or the original Windows project.

## Run

From the repository root:

```powershell
dotnet run --project experiments/PdfPigContentProbe/PdfPigContentProbe.csproj -c Release
```

The probe selects the latest existing working copy in the AssinaPDF edit history. To inspect a specific file:

```powershell
dotnet run --project experiments/PdfPigContentProbe/PdfPigContentProbe.csproj -c Release -- "C:\PDFs\sample.pdf"
```

## What it tests

PdfPig exposes parsed page operations through `Page.Operations`. Each extracted `Letter` also exposes `TextSequence`, described by PdfPig as the sequence number of the ShowText operation that printed that letter. This probe counts visible page-level `Tj`/`TJ` operations and associates letters only when their sequence number matches the corresponding text-showing operation in that page's operation list.

For the first 24 text operations on each page, it prints the public operand information exposed by the operation object (literal text, hex bytes, or TJ array token descriptions, when available).

## Important limitations

- This is an experimental cross-check, not proof that the operation list preserves original stream byte offsets.
- Form XObjects and nested content may produce letters whose text sequence does not correspond to a page-level operation in `Page.Operations`; such letters are reported as unmapped rather than guessed.
- PdfPig's public operation model does not, by itself, expose the original byte offset of each operand in the decoded or encoded source stream.
- Literal string serialization, hex strings, font encodings, composite fonts, glyph clusters, ligatures, and TJ numeric adjustments still require separate validation.
- No PDF is rewritten, moved, deleted, or saved.

PdfPig is distributed under Apache-2.0. It is used only in this isolated research project and is not added to the product runtime.

## Original page content stream inspection

The probe also inspects each page's `/Contents` entry through PdfPig's public document structure:
- Resolves indirect stream references and arrays of content streams.
- Reads each stream's original stored bytes and decodes supported PDF filters in memory.
- Reports stream object number, stored/decoded lengths, filter pipeline, and visible `Tj`/`TJ` operator counts.
- Uses Latin-1 only for diagnostic preview so each decoded byte remains represented by one character; it does not claim these preview characters are decoded PDF text.

A second read-only pass tokenizes decoded content-stream bytes and locates literal/hex string operands immediately preceding `Tj`. It reports the operand's starting byte offset within the decoded stream, stream object number, operand length, and a small sample of bytes. It compares the number of located `Tj` operands with PdfPig's page-level `Tj` count. `TJ` arrays are deliberately not treated as single-string operands.

Important: the reported offset is within the decoded stream, not the compressed bytes in the PDF file. This pass does not yet correlate each located operand with a specific `Letter`/glyph or prove that the byte sequence maps one-to-one to Unicode. Literal-string escapes are decoded for diagnostic byte values; unusual inline-image data and malformed content may still require separate handling. No stream bytes are modified, no PDF is serialized, and no file is saved.

The offset pass also compares each located `Tj` string's decoded byte sequence, in stream order, against the corresponding PdfPig `Tj` operation's public `Bytes` value. It reports the exact-match count and a limited set of mismatches per page. This is a validation of operand-byte correlation for the inspected PDF, not yet a glyph-level byte-range map: multi-byte font codes, one-code-to-many-Unicode mappings, `TJ` arrays, and text positioning still require separate treatment.


## Correlação de offsets com glifos

A inspeção também emite linhas `GLIFO-MAP` para correlacionar, na ordem das operações da página, o offset do operando `Tj` no stream decodificado com a sequência `TextSequence` do PdfPig e as `Letter` associadas. São exibidas as primeiras 20 correlações e todas as operações cuja propriedade `Bytes` esteja vazia. A saída inclui texto da operação, texto reconstruído das letras, quantidade de letras e fontes observadas. Essa correlação é diagnóstica: não prova, por si só, a divisão de um operando em bytes por glifo nem o mapeamento de fontes complexas.


A saída também agrupa por fonte a quantidade de operandos em que o número de bytes decodificados coincide com o número de `Letter`, os casos divergentes e os candidatos de um byte/uma letra Unicode. Essa igualdade é apenas uma triagem: não autoriza dividir operandos multibyte nem inferir a codificação completa da fonte.
