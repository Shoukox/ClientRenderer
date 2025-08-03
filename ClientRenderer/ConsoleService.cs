namespace ClientRenderer;

public static class ConsoleService
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