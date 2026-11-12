# PDFium Native Export Probe

Isolated diagnostic for the PDFium native binaries used by the CrossPlatform app.
It does not modify or get referenced by the production application.

Run on each target machine (Windows x64, macOS Intel, macOS Apple Silicon):

```powershell
dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj
```

The probe loads the native PDFium binary supplied by the same NuGet packages and
checks whether the experimental `FPDFText_GetTextObject` export exists, alongside
the core text APIs already used by AssinaPDF.

Interpretation:

- `AVAILABLE`: the native binary exports the API; a separate real-PDF invocation
  test is still required before using it.
- `NOT EXPORTED`: do not add an unconditional P/Invoke for this API on that runtime.
- `Core PDFium exports: FAIL`: the native asset was found but is not compatible
  with the expected PDFium API surface.

This is only an export-presence test. It does not load a PDF, invoke the experimental
function, prove returned object identity, or establish content-stream byte provenance.
