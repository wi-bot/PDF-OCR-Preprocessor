using System.Text.Json;
using PdfOcrPreprocessor.Core;
using PdfOcrPreprocessor.Desktop;
using Xunit;

namespace PdfOcrPreprocessor.Tests;

public sealed class RunnerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PdfOcrPhase0Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SyntheticRunnerReportsInvalidAndMissingProvidersWithoutDroppingOriginal()
    {
        var original = Path.Combine(directory, "original");
        var invalid = Path.Combine(directory, "invalid");
        var absent = Path.Combine(directory, "absent");
        var file = SyntheticPdf.Write(original);
        Directory.CreateDirectory(invalid);
        Directory.CreateDirectory(absent);
        File.WriteAllText(Path.Combine(invalid, Path.GetFileName(file)), "SYNTHETIC invalid PDF");
        var options = new ProofOptions([new("Original", original), new("SyntheticInvalid", invalid), new("SyntheticMissing", absent)],
            Path.Combine(directory, "analysis"), Path.Combine(directory, "repository"), null, true, false);
        var before = ProofRunner.Hash(file);
        var output = ProofRunner.Run(options, new InlineProgress(_ => { }), default);
        var results = JsonSerializer.Deserialize<PageResult[]>(File.ReadAllText(Path.Combine(output, "page-results.json")))!;
        Assert.Contains(results, result => result.Source == "Original" && result.Rendering == "Passed" && result.Unchanged == true);
        Assert.Contains(results, result => result.Source == "SyntheticInvalid" && result.Extraction == "Failed");
        using var comparisons = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "comparisons.json")));
        Assert.Equal("NotComparable: InaccessibleOrInvalid/Missing", comparisons.RootElement[0].GetProperty("Result").GetProperty("State").GetString());
        Assert.Equal(before, ProofRunner.Hash(file));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "summary.json")));
        Assert.False(string.IsNullOrWhiteSpace(summary.RootElement.GetProperty("SqliteEngineVersion").GetString()));
    }

    [Fact]
    public void ExplicitRepositoryArgumentDoesNotDependOnWorkingDirectory()
    {
        var options = ProofOptions.Parse(["--root", Path.Combine(directory, "source"), "--workspace", Path.Combine(directory, "analysis"),
            "--repository", Path.Combine(directory, "repository"), "--inventory-only"]);
        Assert.True(options.InventoryOnly);
    }

    [Fact]
    public void SyntheticViewerLoadsPageAndPaintsSamePageOverlay()
    {
        var original = Path.Combine(directory, "viewer-original");
        SyntheticPdf.Write(original, text: "SYNTHETIC geometry proof $1,985,420.00");
        var options = new ProofOptions([new("Original", original)], Path.Combine(directory, "viewer-analysis"),
            Path.Combine(directory, "repository"), null, false, false);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                using var form = new ProofForm(options);
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                var started = System.Diagnostics.Stopwatch.StartNew();
                var clicked = false;
                IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control parent) =>
                    parent.Controls.Cast<System.Windows.Forms.Control>().SelectMany(child => Descendants(child).Prepend(child));
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), "Viewer did not finish within smoke-test budget.");
                        var controls = Descendants(form).ToArray();
                        var load = controls.OfType<System.Windows.Forms.Button>().Single(button => button.Text == "Load page");
                        var status = controls.OfType<System.Windows.Forms.Label>().Single();
                        if (!clicked && load.Enabled && status.Text.Contains("PDFs")) { clicked = true; load.PerformClick(); }
                        if (!status.Text.StartsWith("Rendered")) return;
                        controls.OfType<System.Windows.Forms.CheckBox>().Single().Checked = true;
                        var canvas = controls.OfType<PageCanvas>().Single();
                        Assert.True(canvas.Width >= form.ClientSize.Width / 2, "Document inspection area is too narrow.");
                        using var picture = new System.Drawing.Bitmap(canvas.Width, canvas.Height);
                        canvas.DrawToBitmap(picture, canvas.ClientRectangle);
                        var redPixels = 0;
                        for (var row = 0; row < picture.Height; row++)
                        for (var column = 0; column < picture.Width; column++)
                        {
                            var pixel = picture.GetPixel(column, row);
                            if (pixel.R > pixel.G + 30 && pixel.R > pixel.B + 30) redPixels++;
                        }
                        Assert.True(redPixels > 20, "Same-page overlay was not painted.");
                        var screenshotDirectory = Environment.GetEnvironmentVariable("PHASE0_SCREENSHOT_DIR");
                        if (screenshotDirectory != null)
                        {
                            Directory.CreateDirectory(screenshotDirectory);
                            using var screenshot = new System.Drawing.Bitmap(form.Width, form.Height);
                            form.DrawToBitmap(screenshot, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                            screenshot.Save(Path.Combine(screenshotDirectory, "synthetic-viewer.png"));
                        }
                        timer.Stop();
                        form.Close();
                    }
                    catch (Exception exception) { failure = exception; timer.Stop(); form.Close(); }
                };
                form.Shown += (_, _) => timer.Start();
                System.Windows.Forms.Application.Run(form);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Viewer UI thread did not exit.");
        Assert.Null(failure);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}