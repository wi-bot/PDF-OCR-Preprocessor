namespace PdfOcrPreprocessor.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = ProofOptions.Parse(args);
            if (options.Proof)
            {
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
                var output = ProofRunner.Run(options, new InlineProgress(Console.WriteLine), cancellation.Token);
                Console.WriteLine(output);
                return 0;
            }
            ApplicationConfiguration.Initialize();
            Application.Run(new ProofForm(options));
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 2; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}