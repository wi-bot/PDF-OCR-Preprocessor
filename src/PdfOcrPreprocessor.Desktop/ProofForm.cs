using PdfOcrPreprocessor.Core;
using PdfOcrPreprocessor.Desktop.Pdf;

namespace PdfOcrPreprocessor.Desktop;

public sealed class ProofForm : Form
{
    private readonly ProofOptions options;
    private readonly ListBox files = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly ComboBox variants = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown page = new() { Width = 65, Minimum = 1, Maximum = 1 };
    private readonly Button load = new() { Text = "Load page", AutoSize = true };
    private readonly Button scan = new() { Text = "Discover", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel", Enabled = false, AutoSize = true };
    private readonly CheckBox overlay = new() { Text = "Geometry", AutoSize = true };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly Label status = new() { Dock = DockStyle.Bottom, Height = 42, AutoEllipsis = true };
    private readonly PageCanvas canvas = new() { Dock = DockStyle.Fill };
    private Inventory? inventory;
    private CancellationTokenSource? cancellation;

    public ProofForm(ProofOptions options)
    {
        this.options = options;
        Text = "PDF-OCR-Preprocessor | Phase 0";
        Size = new(1300, 900);
        MinimumSize = new(900, 650);
        AutoScaleMode = AutoScaleMode.Dpi;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(6), WrapContents = true };
        toolbar.Controls.AddRange([scan, variants, page, load, overlay, cancel]);
        var content = new SplitContainer { Width = 1200, SplitterDistance = 300, Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1, Panel1MinSize = 220, Panel2MinSize = 400 };
        var inspection = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Height = 800, SplitterDistance = 550 };
        inspection.Panel1.Controls.Add(canvas);
        inspection.Panel2.Controls.Add(details);
        content.Panel1.Controls.Add(files);
        content.Panel2.Controls.Add(inspection);
        Controls.Add(content);
        Controls.Add(toolbar);
        Controls.Add(status);
        scan.Click += async (_, _) => await ScanAsync();
        load.Click += async (_, _) => await LoadAsync();
        cancel.Click += (_, _) => { cancellation?.Cancel(); status.Text = "Cancellation requested; waiting for current native operation to return."; };
        overlay.CheckedChanged += (_, _) => { canvas.ShowGeometry = overlay.Checked; canvas.Invalidate(); };
        files.SelectedIndexChanged += (_, _) => SelectDocument();
        variants.SelectedIndexChanged += (_, _) => SelectVariant();
        Shown += async (_, _) => await ScanAsync();
        FormClosing += (_, eventArgs) =>
        {
            if (cancellation != null) { cancellation.Cancel(); eventArgs.Cancel = true; status.Text = "Waiting for current operation before closing. Native hangs cannot be cancelled safely."; }
        };
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        if (cancellation != null) return;
        cancellation = new();
        scan.Enabled = load.Enabled = files.Enabled = variants.Enabled = page.Enabled = false;
        cancel.Enabled = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { status.Text = "Cancelled."; }
        catch (Exception exception)
        {
            status.Text = $"Failed: {exception.Message}";
            details.Text = exception.ToString();
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            scan.Enabled = load.Enabled = files.Enabled = variants.Enabled = page.Enabled = true;
            cancel.Enabled = false;
        }
    }

    private Task ScanAsync() => ExecuteAsync(async token =>
    {
        var progress = new Progress<string>(message => status.Text = message);
        inventory = await Task.Run(() => ProofRunner.Discover(options, progress, token), token);
        files.DataSource = inventory.Matches;
        files.DisplayMember = nameof(DocumentMatch.RelativePath);
        status.Text = $"{inventory.Files.Length} PDFs; {inventory.Issues.Length} discovery issues; {inventory.Files.Count(file => file.Error != null)} open failures. Providers: {options.Sources.Length - 1}.";
        if (inventory.Issues.Length > 0) details.Text = string.Join(Environment.NewLine, inventory.Issues.Select(issue => $"{issue.Path}: {issue.Error}"));
    });

    private void SelectDocument()
    {
        canvas.Clear();
        if (files.SelectedItem is not DocumentMatch match || inventory == null) return;
        variants.DataSource = inventory.Files.Where(file => file.File.RelativePath.Equals(match.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        variants.Format -= FormatVariant;
        variants.Format += FormatVariant;
        variants.FormattingEnabled = true;
        details.Text = $"Missing configured roots: {string.Join(", ", match.MissingSources)}\r\nProvider only: {match.ProviderOnly}; Collision: {match.Collision}";
    }

    private static void FormatVariant(object? sender, ListControlConvertEventArgs args)
    {
        if (args.ListItem is InventoryFile file) args.Value = $"{file.File.Source}: {file.PageCount?.ToString() ?? "failed"} pages";
    }

    private void SelectVariant()
    {
        canvas.Clear();
        page.Value = 1;
        page.Maximum = Math.Max(1, (variants.SelectedItem as InventoryFile)?.PageCount ?? 1);
    }

    private Task LoadAsync() => ExecuteAsync(async token =>
    {
        if (variants.SelectedItem is not InventoryFile file) return;
        var pageNumber = (int)page.Value;
        status.Text = $"Loading {file.File.Source}, page {pageNumber}";
        var result = await Task.Run(() =>
        {
            var before = ProofRunner.Hash(file.File.FullPath);
            if (before != file.Sha256) throw new InvalidDataException("Source changed since discovery. Discover again.");
            PageEvidence? evidence = null;
            RenderedPage? image = null;
            var errors = new List<string>();
            try { evidence = new PdfPigExtractor().Extract(file.File.FullPath, file.File.RelativePath, file.File.Source, pageNumber, token); }
            catch (Exception exception) when (ProofRunner.Recoverable(exception)) { errors.Add($"Extraction: {exception.GetType().Name}: {exception.Message}"); }
            try { image = new PdfiumRenderer().Render(file.File.FullPath, pageNumber, 144, token); }
            catch (Exception exception) when (ProofRunner.Recoverable(exception)) { errors.Add($"Rendering: {exception.GetType().Name}: {exception.Message}"); }
            var after = ProofRunner.Hash(file.File.FullPath);
            if (before != after) throw new InvalidDataException("Source changed while loading. Evidence discarded.");
            Directory.CreateDirectory(options.Workspace);
            var entry = new { Utc = DateTime.UtcNow, file.File.Source, file.File.RelativePath, Page = pageNumber,
                Before = before, After = after, Extraction = evidence == null ? "Failed" : "Passed", Rendering = image == null ? "Failed" : "Passed" };
            File.AppendAllText(Path.Combine(options.Workspace, "viewer.jsonl"), System.Text.Json.JsonSerializer.Serialize(entry) + Environment.NewLine);
            return (evidence, image, errors);
        }, token);
        canvas.Clear();
        if (result.image != null) canvas.SetPage(result.image, result.evidence);
        var original = inventory?.Files.FirstOrDefault(item => item.File.Source == "Original" && item.File.RelativePath.Equals(file.File.RelativePath, StringComparison.OrdinalIgnoreCase));
        var correspondence = original == null ? "No original" : original.PageCount == file.PageCount ? "Provisional ordinal correspondence" : "Page-count mismatch: comparison blocked";
        details.Text = $"{file.File.FullPath}\r\n{correspondence}\r\n" +
            $"Page {pageNumber}/{file.PageCount}; glyphs={result.evidence?.Glyphs.Length}; images={result.evidence?.ImageCount}\r\n" +
            $"Geometry: {result.evidence?.Geometry}\r\n{result.evidence?.CoordinateConvention}\r\n" +
            $"144 DPI; {result.image?.PixelWidth} x {result.image?.PixelHeight} pixels; overlay belongs to this variant only\r\n" +
            string.Join("\r\n", result.evidence?.Warnings ?? []) + "\r\n" + string.Join("\r\n", result.errors) + "\r\n\r\n" + result.evidence?.Text;
        status.Text = result.errors.Count > 0 ? "Page completed with errors; see details." : result.evidence?.Glyphs.Length == 0
            ? "Rendered; no extracted text. This does not mean the page is blank." : "Rendered with same-page geometry available.";
    });
}

public sealed class PageCanvas : Control
{
    private Bitmap? bitmap;
    private PageEvidence? evidence;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowGeometry { get; set; }

    public PageCanvas() { DoubleBuffered = true; BackColor = Color.FromArgb(225, 228, 230); }
    public void SetPage(RenderedPage image, PageEvidence? page)
    {
        using var stream = new MemoryStream(image.Png);
        using var decoded = Image.FromStream(stream);
        var next = new Bitmap(decoded);
        bitmap?.Dispose();
        bitmap = next;
        evidence = page;
        Invalidate();
    }
    public void Clear() { bitmap?.Dispose(); bitmap = null; evidence = null; Invalidate(); }
    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        if (bitmap == null) return;
        var scale = Math.Min(ClientSize.Width / (double)bitmap.Width, ClientSize.Height / (double)bitmap.Height);
        var width = (float)(bitmap.Width * scale);
        var height = (float)(bitmap.Height * scale);
        var originX = (ClientSize.Width - width) / 2;
        var originY = (ClientSize.Height - height) / 2;
        args.Graphics.DrawImage(bitmap, originX, originY, width, height);
        if (!ShowGeometry || evidence == null) return;
        using var pen = new Pen(Color.FromArgb(180, Color.Red), 1);
        foreach (var glyph in evidence.Glyphs)
        {
            var points = glyph.DisplayQuad.Select(point => new PointF(originX + (float)(point.X / evidence.Geometry.DisplayWidth * width),
                originY + (float)(point.Y / evidence.Geometry.DisplayHeight * height))).ToArray();
            if (points.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y))) args.Graphics.DrawPolygon(pen, points);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) bitmap?.Dispose(); base.Dispose(disposing); }
}