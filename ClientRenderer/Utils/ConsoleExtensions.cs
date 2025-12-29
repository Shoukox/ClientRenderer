namespace ClientRenderer.Utils;

public static class ConsoleExtensions
{
    private static readonly CancellationTokenSource Cts = new();

    public static void ConfigureConsoleClose(out CancellationToken token)
    {
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            Console.WriteLine("Cancel event triggered");
            Cts.Cancel();
            eventArgs.Cancel = true;
        };

        token = Cts.Token;
    }
}