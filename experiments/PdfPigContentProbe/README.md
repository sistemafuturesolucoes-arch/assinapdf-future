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

This is still read-only. It does not modify stream bytes, serialize a PDF, or establish a glyph-to-byte mapping. The next validation must correlate the parsed operands with exact string-token offsets in the decoded content stream, including multi-stream pages and `TJ` arrays.
