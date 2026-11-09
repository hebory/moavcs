namespace MoaVcs.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            TextDiffTests.Run();
            Console.WriteLine("PASS: line comparison alignment, bounded fallback, Unicode, cancellation and newline labels");
            await ProcessTests.RunAsync();
            Console.WriteLine("PASS: process capture, cancellation, timeout, bounded output");
            await GitTests.RunAsync();
            Console.WriteLine("PASS: Git status, diff, index, commit, history, clone, Unicode and literal paths");
            await ProviderTests.RunAsync();
            Console.WriteLine("PASS: SVN, Perforce and Lore adapter contracts");
            await ShellTests.RunAsync();
            Console.WriteLine("PASS: Explorer command quoting and settings persistence");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
