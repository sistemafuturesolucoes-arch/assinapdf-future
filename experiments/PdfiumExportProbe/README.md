# PDFium Character/Object Probe

Isolated diagnostic for the PDFium native binaries used by the CrossPlatform app.
It is not referenced by the production application and does not modify the PDF.

## Run against the latest PDF opened in AssinaPDF

The probe reads the AssinaPDF editor history from:

- Windows: `%LOCALAPPDATA%\FutureSolucoes\AssinaPDF\DocumentosEditados\historico-edicao.json`
- macOS: `~/Library/Application Support/FutureSolucoes/AssinaPDF/DocumentosEditados/historico-edicao.json`

With no PDF argument, it selects the most recently modified existing working copy
recorded by the editor.

From the repository root on Windows:

```powershell
dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj
```

To analyze a specific PDF:

```powershell
dotnet run --project experiments/PdfiumExportProbe/PdfiumExportProbe.csproj -- "C:\PDFs\amostra.pdf"
```

## What this version reports

The probe now visits every page in the selected PDF and prints one compact row
per page:

- page number;
- PDFium text-character count;
- distinct text-object pointers associated with characters;
- character indices for which `FPDFText_GetTextObject` returned NULL;
- mappings whose pointer was not found among the page objects;
- page status (`OK`, `REVISAR`, or `ERRO`).

It prints totals at the end. The object total is the sum of per-page distinct
object counts; an object appearing on more than one page can therefore contribute
once per page.

Exit codes:

- `0`: all pages analyzed and no NULL/unmatched mappings were found;
- `11`: analysis completed, but at least one NULL or unmatched mapping exists;
- `13`: one or more pages could not be analyzed;
- other nonzero codes: library, PDF, or input setup problem.

## Interpretation and limitations

- A non-null pointer matching an object enumerated by `FPDFPage_GetObject` is
  evidence of a character-to-text-object association for that page.
- NULL and unmatched counts are diagnostic signals, not proof by themselves
  that a PDF is corrupt.
- This probe does **not** map a character to its encoded byte offset, content
  stream, `Tj`/`TJ` operator, or string operand.
- It does not rewrite operators, move glyphs, delete text, or save a modified PDF.
- Passing this probe is not evidence that partial text editing is safe.
- The diagnostic remains separate from production and from
  `src/AssinaPDF.Desktop`, the original Windows project.
