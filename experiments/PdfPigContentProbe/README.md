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

## Inspeção das estruturas internas de fonte

O probe percorre as letras extraídas e inspeciona, por reflection e sem invocar métodos de conversão, as propriedades, campos e assinaturas de API do objeto de detalhes da fonte exposto por cada `Letter`. O objetivo é identificar quais estruturas de codificação, CMaps, mapas Unicode ou dicionários estão disponíveis nesta versão do PdfPig para investigar fontes incorporadas/subconjuntos. A inspeção é limitada a metadados e referências de objetos; não tenta adivinhar a codificação nem altera o documento.


## Inspeção dos mapas de fonte e CMaps

A etapa de fontes foi ampliada para resolver, em memória, os streams `/ToUnicode`, decodificar os filtros PDF suportados e imprimir o CMap textual com contagem de estruturas `codespacerange`, `bfchar`, `bfrange`, `cidchar` e `cidrange`. Também inspeciona `/Encoding` nomeado ou em dicionário (incluindo `/BaseEncoding` e `/Differences`), além de metadados essenciais das fontes descendentes CID e estruturas Type3.

A saída é limitada por quantidade de fontes, tamanho do CMap e itens de arrays para evitar relatórios excessivos. O conteúdo decodificado é apenas diagnóstico: a implementação ainda não transforma CMaps em um codificador/decodificador completo, não correlaciona automaticamente cada code space a bytes de glifos e não altera nem salva PDFs. Fontes compostas, CMaps Identity, mapeamentos de vários bytes, ligaturas e diferenças de codificação exigem validação específica antes de qualquer reescrita.

## Deterministic ToUnicode parser self-tests

Run the parser tests without opening a PDF:

```powershell
dotnet run --project experiments/PdfPigContentProbe/PdfPigContentProbe.csproj -c Release -- --self-test
```

The bounded diagnostic parser scans `bfchar` and `bfrange` blocks across arbitrary whitespace and line breaks, including multiple entries on one line and destination arrays spanning lines. It strips PostScript comments, preserves source-code width, validates UTF-16BE (including surrogate pairs), rejects duplicate/invalid mappings, and caps range expansion at 4096 entries. Source codes are limited to the PDF four-byte maximum. This is not a complete PostScript CMap interpreter: it does not resolve inherited/usecmap maps, codespace semantics, CID mapping blocks, or every malformed-token recovery rule. A passing self-test validates this parser only, not a PDF editor or stream rewrite.

The parser also reads `begincodespacerange` blocks and exposes a fail-closed byte splitter. Given raw text-string bytes, it accepts a code only when exactly one declared code-space length matches and that exact code has a ToUnicode mapping. It rejects ambiguous lengths and unmapped codes. This is the first explicit bridge from source bytes to Unicode code units; it still does not prove that a given byte span belongs to a particular PDFium glyph or handle inherited CMaps.

## PDF glyph byte-span mapper

The isolated `PdfGlyphByteMapper` consumes an original text-string operand, a parsed ToUnicode CMap, and the Unicode text independently extracted by PdfPig. It returns exact byte offsets/lengths per PDF character code only if every source byte is decoded unambiguously and the reconstructed Unicode matches PdfPig exactly. A code that maps to multiple Unicode scalar values remains one PDF-code span (for example, a ligature or multi-character mapping); the mapper does not split it into separate glyphs. Any ambiguity or mismatch fails closed.

The mapper is not yet connected to the editor and does not infer geometry, text advances, font encodings without ToUnicode, nested Form XObject offsets, or `TJ` array adjustments. It does not rewrite or save PDFs. The self-test command now runs both the CMap parser suite (25 checks) and the byte-span mapper suite (6 checks).

## Read-only validation against actual PDF text operands

When a PDF is opened by the probe, `PdfGlyphOperandValidationInspector` checks each non-empty `Tj` operand exposed by PdfPig. It resolves the font's `/ToUnicode` stream (including a descendant font dictionary), decodes the stream using PdfPig's supported filters, maps the operand bytes through the declared code spaces, and requires the reconstructed Unicode to match the text extracted for the same `TextSequence` exactly. The report separates exact mappings, missing text association, empty bytes, mixed-font sequences, unavailable CMaps, and ambiguous/mismatching results. It also reports source codes that map to multiple Unicode scalar values without splitting them.

This is still an operand-level cross-check, not proof of original content-stream byte offsets or per-glyph geometry. It currently validates `Tj` only; `TJ` arrays, inherited/usecmap CMaps, fonts without `/ToUnicode`, and Form XObject stream traversal remain outside this validation pass. No PDF bytes are changed or saved.

## Raw PDF string token codec (in-memory only)

`PdfStringOperandCodec` decodes literal and hexadecimal PDF string tokens while recording the raw-token byte interval that produced each decoded byte. It handles nested literal parentheses, standard escapes, octal escapes, line continuations, hexadecimal whitespace, and odd trailing nibbles. Its replacement helper rewrites a selected contiguous decoded-byte range using a same-byte-count replacement, then decodes the result again and verifies every decoded byte outside the selected range is unchanged. Literal replacements use fixed three-digit octal escapes; hex replacements use hexadecimal digits. The helper changes only an in-memory token and does not rewrite a content stream, update stream lengths/filters, adjust text advances, or save a PDF. It must not be used for partial edits of a source code that maps to multiple Unicode characters. Raw CR and CRLF line endings normalize to LF as required for PDF literal strings; escaped line continuations are omitted.

`PdfGlyphTokenRewritePlanner` adds a stricter planning layer for a single Unicode scalar: the selected source code must match the exact bytes in the token, the replacement code must have the same byte length, and its ToUnicode mapping must equal the requested replacement scalar. It rejects multi-scalar source mappings, missing/ambiguous replacement codes, mismatched source bytes, and multi-scalar replacement text. This is still a token-only in-memory plan; it does not locate the token in the original content stream or write a PDF. The self-test runner now includes 25 CMap tests, 6 byte-mapper tests, 11 raw-token codec tests, and 7 glyph-token planner tests (49 total).

## Decoded content-stream rewrite planner (still no PDF output)

`PdfContentStreamTokenScanner` scans decoded stream bytes directly and records exact byte offsets for literal/hex string operands immediately followed by `Tj`. It ignores comments, names, arrays and unrelated strings; it stops and refuses planning if it encounters inline-image syntax (`BI`) or malformed strings. `PdfContentStreamRewritePlanner` accepts the exact stream offset plus the selected byte offset inside that string, verifies the selected source bytes, applies the same-length code replacement, reconstructs the decoded stream, and checks that the prefix and suffix are byte-identical and that a second complete scan finds the same number of `Tj` operands and the expected replacement bytes.

This stage changes only an in-memory **decoded content stream**. It does not recompress or update the PDF stream object, `/Length`, filters, xref, or file; it does not handle `TJ` arrays, inline images, nested Form XObjects, or text advance/kerning compensation. It must not be used to save or ship an edited PDF. The self-test runner now includes 25 CMap tests, 6 byte-mapper tests, 11 raw-token codec tests, 7 glyph-token planner tests, and 8 content-stream planner tests (57 total).


## Real PDF stream glyph dry run (no PDF output)

The probe now attempts an integrated in-memory dry run for the known `CONFIRME` → `BONFIRME` sample. It correlates page `Tj` operations with decoded content-stream operands, resolves the font's ToUnicode CMap, maps the selected source code, requires a unique replacement code of the same byte length, plans the token replacement, and then plans the decoded stream replacement. It checks that the scanner can read the rewritten stream and that all bytes before and after the selected token remain identical.

This remains strictly diagnostic: it does not modify the PDF object, recompress streams, update `/Length`, rebuild xref data, or write a file. It also does not calculate text advance compensation or prove that the replacement glyph has identical visual width. If any correlation, font mapping, or replacement uniqueness check fails, it aborts without producing a plan.


## Type3 Encoding, Differences, CharProcs e larguras

Uma inspeção adicional localiza o trecho de teste `CONFIRME` e examina o dicionário Type3 da primeira letra. Ela tenta ler `/Encoding`, `/BaseEncoding`, a sequência `/Differences`, os nomes presentes em `/CharProcs`, `/FirstChar`, `/Widths`, `/FontMatrix` e `/FontBBox`. O objetivo é verificar se os nomes de glifo `C` e `B` existem estruturalmente e se os códigos associados têm larguras declaradas iguais.

A inspeção não depende de existir um mapeamento ToUnicode para todos os glifos: o ToUnicode é uma camada de extração textual, enquanto Encoding/Differences e CharProcs descrevem a codificação e os programas de desenho da fonte Type3. Ainda assim, encontrar um nome em Differences e CharProcs não comprova que o desenho seja visualmente equivalente, nem basta para editar o PDF. Codificações baseadas em nomes, aliases, ausência de Differences, métricas dos operadores d0/d1, estado de texto e posicionamento dos próximos operadores ainda exigem validação. Nenhum objeto é modificado e nenhum arquivo é salvo.


### Diagnóstico Type3 por código real e referência exata da fonte

A inspeção Type3 agora também:
- lê os bytes do operando da letra selecionada;
- relaciona o código numérico com o nome efetivo em `/Encoding /Differences`;
- verifica se o nome resultante existe em `/CharProcs`;
- mostra a largura declarada para o código quando disponível;
- procura letras `B` usando a mesma referência de objeto do dicionário da fonte, não apenas o mesmo `FontName`;
- para cada amostra encontrada, mostra o código, o nome de glifo e a largura correspondente.

Isso evita confundir fontes distintas que compartilham o mesmo nome exibido pelo PdfPig e evita assumir que o caractere Unicode `B` corresponde a um CharProc chamado `/B`. A inspeção continua somente leitura: não reescreve streams, não salva PDF e não autoriza substituição automática.


A inspeção também lista candidatos Unicode `B` com sua referência de fonte e bytes de origem, explicitando se a referência coincide com a fonte selecionada. Para CharProcs decodificados, tenta extrair a métrica inicial Type3 (`wx wy d0/d1`), que representa o avanço declarado pelo programa. A ausência de candidato com referência idêntica permanece inconclusiva: não se deve inferir um código substituto a partir de outra fonte.
