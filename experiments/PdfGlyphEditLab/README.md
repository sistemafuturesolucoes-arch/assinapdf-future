# PDF Glyph Edit Lab

This project is intentionally isolated from `src/AssinaPDF.CrossPlatform` and is not referenced by the production application. It does not change the original Windows project or the current editor.

## What this first experiment validates

- Mapping a drag rectangle to individual PDFium glyph boxes.
- Selecting a phrase even when its characters are represented by separate text runs.
- Avoiding adjacent glyphs when the selection only touches a small portion of them.
- Stable ordering by PDFium character index.

Run from the repository root:

```powershell
dotnet run --project experiments/PdfGlyphEditLab/PdfGlyphEditLab.csproj
```

Expected result: `PDF glyph lab: 3/3 checks passed.`

## Explicit limitation

This is only the geometry/selection stage. It does **not** yet remove or replace glyphs in a PDF content stream, preserve font encodings, rewrite `Tj/TJ` operators, or save a modified PDF. A passing result here is not proof of PDF editing correctness.

## Next acceptance gates

1. Adapter from the existing PDFium character inspection to `GlyphBox`, preserving page and character indices.
2. Map selected glyphs to their actual PDF content operators and font resources. Do not infer operator membership from visual adjacency alone.
3. Remove selected glyphs while retaining all unselected glyphs, text positioning, kerning, and graphics state.
4. Test embedded TrueType/OpenType, CID/Type0, Type3, ligatures, rotated text, vertical writing, clipping, and transformed content streams.
5. Save, reopen, re-render, and re-extract text. Confirm unselected text and artwork are unchanged.
6. Only after those checks, prototype replacement and movement. If a selection cannot be mapped unambiguously to source operators, abort without changing the working copy.

## Licensing

No third-party engine is included in this experiment. The reDACT project remains a behavior/reference candidate only; its GPL-3.0 terms are not being introduced into the AssinaPDF product by this lab.
