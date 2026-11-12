# PDFium Character/Object Probe

Isolated diagnostic for the PDFium native binaries used by the CrossPlatform app.
It does not modify or get referenced by the production application.

## Use a PDF already opened in AssinaPDF

The probe now reads the AssinaPDF editor history at:

- Windows: `%LOCALAPPDATA%\\FutureSolucoes\\AssinaPDF\\DocumentosEditados\\historico-edicao.json`
- macOS: `~/Library/Application Support/FutureSolucoes/AssinaPDF/DocumentosEditados/historico-edicao.json`

With no PDF argument, it automatically selects the most recently modified existing
working copy recorded by the editor. It does not ask for a file picker and does not
change the source or working copy.

Run:

```powershell
dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj
```

To inspect a particular page, the optional `--page` value is a zero-based page index:

```powershell
dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj -- --page 1
```

An explicit PDF path can still be supplied when needed:

```powershell
dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj -- "C:\\PDFs\\amostra.pdf"
```

The probe opens the selected PDF using PDFium and, for every PDFium text character,
calls `FPDFText_GetTextObject`. It compares the returned native pointer with the
objects enumerated by `FPDFPage_GetObject`, groups characters by pointer identity,
and prints the text reported by `FPDFTextObj_GetText` where possible.

## Interpretation and limitations

- A non-null character-to-object pointer that matches an enumerated page object is
  evidence of a character-to-text-object association for that specific PDF/page.
- A null mapping or pointer not found in the page object list must be investigated;
  it is not silently guessed.
- Text reported for a text object may contain several characters. This is not a
  mapping from each character to its encoded byte offset.
- The probe does not identify content-stream operator indices, string operand
  indices, encoded byte offsets, or prove that rewriting Tj/TJ is safe.
- It does not save or modify the input PDF.
- The program loads the native library dynamically and resolves the experimental
  function only after confirming that the export exists. The production application
  is not changed by this diagnostic.
