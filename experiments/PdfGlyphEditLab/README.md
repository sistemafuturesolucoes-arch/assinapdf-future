# PDF Glyph Edit Lab

This project is intentionally isolated from `src/AssinaPDF.CrossPlatform` and is not referenced by the production application. It does not change the original Windows project or the current editor.

## What this first experiment validates

- Mapping a drag rectangle to individual PDFium glyph boxes.
- Selecting a phrase even when its characters are represented by separate text runs.
- Avoiding adjacent glyphs when the selection only touches a small portion of them.
- Stable ordering by PDFium character index.\n- Explicit glyph-to-source provenance (content stream, operator, string operand, encoded byte span, and font resource).\n- Adjacent encoded bytes are grouped only when they belong to the same source operand.\n- Missing, ambiguous, invalid, or overlapping source mappings fail closed.\n- Byte-range preservation planner keeps unselected encoded bytes, including around multi-byte glyph spans.\n- Out-of-bounds and overlapping removals are rejected.\n- Structural tokenizer recognizes literal strings, hex strings, arrays, names, numbers, comments, and operator words in decoded content-stream text.\n- Unterminated literal/hex strings fail closed.

Run from the repository root:

```powershell
dotnet run --project experiments/PdfGlyphEditLab/PdfGlyphEditLab.csproj
```

Expected result: `PDF glyph lab: 11/11 checks passed.`

## Explicit limitation

This is still a planning stage. The source mapper consumes explicit byte spans supplied by a future PDF content interpreter, and the byte planner identifies untouched operand ranges. It does **not** decode compressed PDF streams, parse indirect objects/resources, decode font character codes, discover the operator behind a PDFium glyph, emit rewritten `Tj/TJ` operators, preserve text advance/kerning, or save a modified PDF. A passing result is not proof of PDF editing correctness.

## Native PDFium export probe

A separate diagnostic project now checks whether the bundled native PDFium binary exports `FPDFText_GetTextObject` and the core text APIs. It is intentionally isolated and is not referenced by the application. Run it on each target OS using `dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj`. See [the probe README](../PdfiumExportProbe/README.md). Export presence alone is not proof that the function can be safely invoked or that it maps characters to source bytes.

## Next acceptance gates

1. Adapter from the existing PDFium character inspection to `GlyphBox`, preserving page and character indices.
2. Integrate a standards-compliant PDF parser to obtain decoded page content streams and resource dictionaries.\n3. Connect a real content-stream interpreter to this provenance model and prove its character-to-byte mapping for simple and composite fonts.\n4. Implement a writer that preserves text advance, spacing adjustments, graphics/text state, and font encoding while rewriting `Tj/TJ`. Do not infer operator membership from visual adjacency alone.
3. Remove selected glyphs while retaining all unselected glyphs, text positioning, kerning, and graphics state.
4. Test embedded TrueType/OpenType, CID/Type0, Type3, ligatures, rotated text, vertical writing, clipping, and transformed content streams.
5. Save, reopen, re-render, and re-extract text. Confirm unselected text and artwork are unchanged.
6. Only after those checks, prototype replacement and movement. If a selection cannot be mapped unambiguously to source operators, abort without changing the working copy.

## Licensing

No third-party engine is included in this experiment. The reDACT project remains a behavior/reference candidate only; its GPL-3.0 terms are not being introduced into the AssinaPDF product by this lab.


## Finding from the current AssinaPDF integration

The current CrossPlatform project already extracts PDFium characters using
`FPDFText_GetUnicode`, `FPDFText_GetCharBox`, and font metadata. It also enumerates
page objects with `FPDFPage_GetObject` and reads text-object text.

PDFium exposes an additional experimental API, `FPDFText_GetTextObject`, which
returns the text page object associated with a character index. This can establish
a useful intermediate mapping:

`PDFium character index -> PDF text object`

It does **not** expose the original content-stream operator index, string operand
index, or encoded byte offset. It therefore cannot, by itself, authorize a glyph-level
rewrite. Multiple characters may belong to the same text object, and the object API
does not reveal the exact source byte sequence for an individual glyph.

Next proof should validate the exported API in the bundled Windows and macOS PDFium
runtimes, map every extracted character to a stable page-object identity, and compare
the text-object output with PDFium's page character sequence. Only after that should
a real content-stream parser be connected to recover operator/operand/byte provenance.
The production editor remains unchanged until this is proven on real PDFs.
