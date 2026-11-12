# PDFium Character/Object Probe

Isolated diagnostic for the PDFium native binaries used by the CrossPlatform app.
It does not modify or get referenced by the production application.

## 1. Check native exports

Run on each target machine (Windows x64, macOS Intel, macOS Apple Silicon):

```powershell
dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj
```

## 2. Test a real PDF page

Pass a local PDF path. The optional `--page` value is a zero-based page index:

```powershell
dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj -- "C:\\PDFs\\amostra.pdf"
dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj -- "C:\\PDFs\\amostra.pdf" --page 1
```

The probe opens the PDF using PDFium and, for every PDFium text character, calls
`FPDFText_GetTextObject`. It then compares the returned native pointer with the
objects enumerated by `FPDFPage_GetObject`, and groups characters by pointer identity.
Where possible, it also prints the text reported by `FPDFTextObj_GetText`.

## Interpretation and limitations

- `AVAILABLE` / `FOUND` means only that the native export exists.
- A non-null character-to-object pointer that matches an enumerated page object is
  evidence of a character-to-text-object association for that specific PDF/page.
- A null mapping or pointer not found in the page object list must be investigated;
  it is not silently guessed.
- Text reported for a text object may contain several characters. This is not a
  mapping from each character to its encoded byte offset.
- The probe does not identify content-stream operator indices, string operand
  indices, encoded byte offsets, or prove that rewriting Tj/TJ is safe.
- It does not save or modify the input PDF.

The program loads the native library dynamically and resolves the experimental
function only after confirming that the export exists. The production application
is not changed by this diagnostic.
