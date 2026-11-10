namespace MoaVcs.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(["--lore"])) { await RunLoreChecks(); return 0; }
            if (args.SequenceEqual(["--shell"])) { await ShellTests.RunAsync(); Console.WriteLine("PASS: Explorer menu layout, launch actions, command quoting and settings persistence"); return 0; }
            if (args.SequenceEqual(["--remote"])) { await RemoteTests.RunAsync(); Console.WriteLine("PASS: Git origin addresses, preservation and validation"); return 0; }
            if (args.SequenceEqual(["--push"])) { await PushTests.RunAsync(); Console.WriteLine("PASS: Git first push, upstream tracking and push configuration preservation"); return 0; }
            if (args.Length != 0) throw new ArgumentException("Unknown test option. Use --lore, --shell, --remote, --push or no arguments.");
            TextDiffTests.Run();
            Console.WriteLine("PASS: line comparison alignment, bounded fallback, Unicode, cancellation and newline labels");
            await ProcessTests.RunAsync();
            Console.WriteLine("PASS: process capture, cancellation, timeout, bounded output");
            await GitTests.RunAsync();
            Console.WriteLine("PASS: Git status, diff, index, commit, history, clone, Unicode and literal paths");
            await RemoteTests.RunAsync();
            Console.WriteLine("PASS: Git origin addresses, preservation and validation");
            await PushTests.RunAsync();
            Console.WriteLine("PASS: Git first push, upstream tracking and push configuration preservation");
            await ProviderTests.RunAsync();
            Console.WriteLine("PASS: SVN, Perforce and Lore adapter contracts");
            await RunLoreChecks();
            await ShellTests.RunAsync();
            Console.WriteLine("PASS: Explorer command quoting and settings persistence");
            await ExternalToolTests.RunAsync();
            Console.WriteLine("PASS: external tool process arguments, snapshots, cleanup and settings persistence");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task RunLoreChecks()
    {
        await LoreComparisonTests.RunAsync();
        Console.WriteLine("PASS: Lore comparison snapshots, pinned revisions, JSON validation and cleanup");
        await LoreBlameTests.RunAsync();
        Console.WriteLine("PASS: Lore audit ranges, attribution metadata, bounds and malformed output");
        await LoreIntegrationTests.RunAsync();
    }
}
