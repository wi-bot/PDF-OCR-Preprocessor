# PDF-OCR-Preprocessor: Phase 0

An in-process PDF technology proof, not the full application. Reads existing PDFs; does not run OCR, modify originals, infer a winning provider, or perform cross-file registration. GLM-OCR is not integrated.

## Projects

- [Core](src/PdfOcrPreprocessor.Core/PdfOcrPreprocessor.Core.csproj): application-owned evidence, coordinate DTOs, discovery/matching, comparison, `IPdfTextExtractor` and `IPageRenderer`. No vendor or UI references.
- [Desktop](src/PdfOcrPreprocessor.Desktop/PdfOcrPreprocessor.Desktop.csproj): WinForms proof viewer, PdfPig/PDFium adapters, sequential runner and SQLite smoke store.
- [Tests](tests/PdfOcrPreprocessor.Tests/PdfOcrPreprocessor.Tests.csproj): synthetic PDFs, geometry/pixel checks, comparisons, discovery, persistence, runner errors and STA viewer smoke test.

## Prerequisites

Windows x64 and .NET SDK **10.0.401**, pinned in [global.json](global.json). Windows 11 x64 is the primary supported deployment target. Windows 10 22H2 x64 is an application-compatibility target only, not a Microsoft-supported .NET deployment claim. Its actual machine test is pending.

The current machine also has a local SDK at `%LOCALAPPDATA%\PDF-OCR-Preprocessor\tools\dotnet`; the system SDK was not replaced. Commands below are PowerShell, from this repository. Use `dotnet` instead of `$dotnet` when the pinned SDK is installed normally.

```powershell
$dotnet = "$env:LOCALAPPDATA\PDF-OCR-Preprocessor\tools\dotnet\dotnet.exe"
& $dotnet restore PDF-OCR-Preprocessor.slnx --locked-mode
& $dotnet build PDF-OCR-Preprocessor.slnx -c Release --no-restore
& $dotnet test tests/PdfOcrPreprocessor.Tests -c Release --no-restore --logger trx --results-directory C:\EnronAnalysis\Phase0\tests
& $dotnet run --project src/PdfOcrPreprocessor.Desktop -c Release --no-build
```

The viewer discovers PDFs off the UI thread. Choose a document, available variant and 1-based page, then load. The geometry checkbox displays only that loaded variant's glyphs. A source change requires rediscovery. The proof UI is fit-to-window, not a production inspection workstation.

## Corpus And Evidence

Default source root: `C:\EnronDataset`. Default analysis workspace: `C:\EnronAnalysis\Phase0`. The five batch folders are source batches, never provider identities. Discovery recursively accepts `.pdf` case-insensitively, ignores non-PDF files, reports errors/collisions and skips reparse points. It never executes download scripts.

```powershell
& $dotnet run --project src/PdfOcrPreprocessor.Desktop -c Release --no-build -- --inventory-only
& $dotnet run --project src/PdfOcrPreprocessor.Desktop -c Release --no-build -- --proof
& $dotnet run --project src/PdfOcrPreprocessor.Desktop -c Release --no-build -- --proof --samples C:\EnronAnalysis\Phase0\reviewed-samples.json
```

Optional switches: `--root <original-root>`, `--workspace <external-output-root>`, `--repository <actual-repo-root>`, `--samples <selection-json>`, and repeatable `--provider Name=Root`. No providers are implicitly configured. Supply only confirmed roots of existing parallel OCR outputs, each preserving the full original relative path including batch and PDF folders. Paths with spaces must be quoted. Launching outside the repository requires `--repository`.

Selection JSON is an array of `{ "RelativePath": "batch\\PDF\\document.PDF", "PageNumber": 1, "Reason": "selection rationale" }`. Default selection is only a deterministic batch-stratified candidate set, not a claim of visually representative coverage. Reviewed local selections and findings live outside Git.

Each proof run creates a unique external directory containing inventory, configuration, environment, selected pages, PNG renders/settings, extracted evidence, SQLite smoke database, per-page outcomes, comparisons, hashes and timings. General diagnostics exclude extracted document text; evidence JSON/database deliberately contain it. Treat that entire directory as sensitive. Repository, source and analysis roots must not overlap. The application opens source PDFs read-only.

Inventory open/page-count success is not a full PDF conformance test. Correspondence is complete-relative-path, ordinal-ignore-case matching only; no basename/fuzzy matching. Provider-only, missing, invalid/inaccessible, collisions and page-count differences remain observable. Unconfigured providers are not missing. Incomplete root discovery prevents concluding that an absent variant is definitely missing.

## Interpretation

- Extractor quads preserve **PdfPig transformed page space**, not original content-stream operands. Display quads use top-left physical points. PdfPig's CropBox is its effective clipped box. Glyph indices retain membership in derived words/lines; derived reading order is heuristic.
- Crop offset, quarter-turn rotation and UserUnit fixtures are synthetic. PDFium's size query ignored fixture UserUnit; the adapter explicitly scales output dimensions. PDF rotation metadata is not scan orientation. Display normalization is not cross-file alignment.
- An empty text layer is no textual evidence, not proof of a blank page. Invisible synthetic text is not a real OCR-provider output. Real provider validation remains separate from successful original-image rendering.
- Comparison uses NFC and collapsed whitespace, preserves case/punctuation, calculates Unicode-scalar edit agreement and multiset token agreement, and reports exact unpaired numeric/date candidate-count differences. Signs, currency, separators and leading zeroes are retained. It does not parse financial tables or assign OCR confidence. Equal page counts give provisional ordinal correspondence only; count mismatch blocks comparison, not extraction.
- Equal text or file hashes are not independent corroboration. Empty/empty agreement is unavailable. Numeric/date differences remain visible even with high overall similarity. Raw extracted evidence is kept separately from normalized metrics.
- Rendering/extraction run sequentially in process. Cancellation is cooperative before/after native calls and between jobs. It cannot terminate a native hang or contain a crash; a task timeout is not isolation. Renderer limits are 36-300 DPI, 20 million pixels and 80 MB encoded output; extraction caps glyphs after parsing, and edit distance is capped at 25 million cell updates. These are not complete native-memory bounds.
- Rendering currently also uses PdfPig to obtain UserUnit, so it is not a parser-independent fallback. The interfaces preserve the option of later process isolation. Complex font mappings, broken/invisible real OCR layers, diverse forms/transparency and unusual boxes require more corpus validation.
- SQLite is one parameterized smoke table, not a production schema or migration system. Read/write failures are explicit; the runner continues ordinary per-file/page failures. Inspect report states, not just process exit status: exit 0 means the runner completed, not that every page passed.

## Self-Contained Publish

```powershell
& .\scripts\Publish-Phase0.ps1 -DotNet $dotnet
& C:\EnronAnalysis\Phase0\publish\PdfOcrPreprocessor.Desktop.exe --proof --samples C:\EnronAnalysis\Phase0\reviewed-samples.json
& C:\EnronAnalysis\Phase0\publish\PdfOcrPreprocessor.Desktop.exe
```

The [publish helper](scripts/Publish-Phase0.ps1) uses locked packages and the Desktop project's `win-x64` target, publishes a folder (not a single-file/trimming experiment), then gathers pinned upstream license notices. Build/setup needs Internet or a prepared package/notice cache; runtime processing has no network service integration. Preserve the entire publish directory, including notices. With additional provider roots, also pass them to the helper's `-SourceRoots` protection list. Use an empty external output folder for a fresh release.

Direct dependencies: PdfPig **0.1.16** (Apache-2.0 plus bundled component notices), PDFtoImage **5.4.0** (MIT), Microsoft.Data.Sqlite **10.0.12** (MIT). Native/transitive dependencies: bblanchon.PDFium.Win32 **152.0.7961** (package Apache-2.0; PDFium and native third-party notices), SkiaSharp/Win32 native assets **4.150.1** (MIT plus third-party notices), SQLitePCLRaw **2.1.12** (Apache-2.0; SQLite engine licensing is separate). Full resolved graphs and content hashes are in the three package lock files. Tests use Microsoft.NET.Test.Sdk **18.10.1**, xUnit **2.9.3** and VS runner **4.0.0**.

The publish helper retrieves the exact PDFium 7961 release archive and verifies its DLL hash against the restored NuGet native DLL before retaining notices. It copies runtime and Skia notices and retrieves pinned managed-library licenses. This is evidence collection, not a legal certification; audit notices when dependencies change or distributing beyond this proof.

## Validation And Stop Point

### Adobe-Only Validation Extension

The Phase 0.5 proof adds per-page Unicode-scalar character counts (grouped text, non-whitespace text and raw glyph strings), word/line counts, finite/nonzero glyph-quad checks, one-point-tolerance page-bound checks, word-to-glyph geometry checks and unassigned text-glyph counts. Selection entries accept optional `"Render": false` for extraction-only pages; omission preserves the Phase 0 render behavior. Every rendered page with extracted evidence also receives a same-file overlay PNG: red glyph quads and blue derived word bounds. The nearby-dark-pixel diagnostic uses a two-pixel bounding-box tolerance; it is only an alignment review aid, never an OCR accuracy/confidence measure. Ruled lines, scan noise and neighboring letters can produce false reassurance.

```powershell
& $dotnet run --project src/PdfOcrPreprocessor.Desktop -c Release --no-build -- --proof --provider Adobe=C:\EnronOCR\ADOBE --workspace C:\EnronAnalysis\Phase0.5-Adobe --samples C:\EnronAnalysis\Phase0.5-Adobe\all-adobe-pages.json
```

The externally generated all-page selection covers the discovered Adobe page ranges and enables renders for the previously reviewed representative pages. Per-page metrics are in each run's page-results JSON; original/provider text availability is reported separately from comparisons between providers. No similarity/accuracy score is assigned when the original has no text. The Adobe-specific report and CSV summaries live under `C:\EnronAnalysis\Phase0.5-Adobe`. Nonempty OCR text and finite geometry do not establish completeness, correct recognition, or visual alignment; consult the recorded exception pages. No OCR engines are executed and neither PDF root is modified.

The complete machine-specific Phase 0 report is `C:\EnronAnalysis\Phase0\PHASE0-VALIDATION.md`; all corpus-derived manifests, renders, databases, hashes and reports stay outside this repository. Synthetic test artifacts use the OS temporary directory; TRX and optional viewer screenshots go to the analysis workspace. Set `PHASE0_SCREENSHOT_DIR` to an external directory to retain the synthetic STA viewer screenshot.

Windows 10 22H2, a clean Windows 11 machine, actual network-disconnected execution and real multi-provider OCR evidence remain separate pending gates unless the external report records an actual test. Stop after Phase 0 findings and obtain approval before building the full application.