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
            if (args.SequenceEqual(["--conflicts"])) { await RunConflictChecks(); return 0; }
            if (args.SequenceEqual(["--history-providers"]))
            {
                await ProviderTests.RunAsync();
                await HistoryProviderTests.RunAsync();
                Console.WriteLine("PASS: SVN, Perforce and Lore provider/history fixtures (no server connection)");
                return 0;
            }
            if (args.SequenceEqual(["--history-git"])) { await HistoryGitTests.RunAsync(); Console.WriteLine("PASS: actual Git commit details and revision diffs"); return 0; }
            if (args.SequenceEqual(["--checkout"])) { CheckoutPathTests.Run(); Console.WriteLine("PASS: checkout repository names and safe destination paths"); return 0; }
            if (args.SequenceEqual(["--repositories"])) { RepositoryHistoryTests.Run(); Console.WriteLine("PASS: repository history pinning, removal, migration and settings preservation"); return 0; }
            if (args.SequenceEqual(["--overlay"]))
            {
                await ExplorerOverlayTests.RunAsync();
                await ExplorerOverlayWorkerTests.RunAsync();
                Console.WriteLine("PASS: Explorer overlay status snapshot, worker scan, cache and settings");
                return 0;
            }
            if (args.Length != 0) throw new ArgumentException("Unknown test option. Use --lore, --shell, --remote, --push, --conflicts, --overlay, --history-git, --history-providers, --checkout, --repositories or no arguments.");
            RepositoryHistoryTests.Run();
            Console.WriteLine("PASS: repository history pinning, removal, migration and settings preservation");
            CheckoutPathTests.Run();
            Console.WriteLine("PASS: checkout repository names and safe destination paths");
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
            await RunConflictChecks();
            await ProviderTests.RunAsync();
            Console.WriteLine("PASS: SVN, Perforce and Lore adapter contracts");
            await HistoryProviderTests.RunAsync();
            Console.WriteLine("PASS: SVN and Perforce committed history detail fixtures (no server connection)");
            await HistoryGitTests.RunAsync();
            Console.WriteLine("PASS: actual Git commit details and revision diffs");
            await RunLoreChecks();
            await ShellTests.RunAsync();
            Console.WriteLine("PASS: Explorer command quoting and settings persistence");
            await ExplorerOverlayTests.RunAsync();
            await ExplorerOverlayWorkerTests.RunAsync();
            Console.WriteLine("PASS: Explorer overlay status snapshot, worker scan, cache and settings");
            await ExternalToolTests.RunAsync();
            Console.WriteLine("PASS: external tool process arguments, snapshots, cleanup and settings persistence");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task RunLoreChecks()
    {
        await HistoryLoreTests.RunAsync();
        await LoreComparisonTests.RunAsync();
        Console.WriteLine("PASS: Lore comparison snapshots, pinned revisions, JSON validation and cleanup");
        await LoreBlameTests.RunAsync();
        Console.WriteLine("PASS: Lore audit ranges, attribution metadata, bounds and malformed output");
        await LoreIntegrationTests.RunAsync();
    }

    private static async Task RunConflictChecks()
    {
        await ConflictTextTests.RunAsync();
        Console.WriteLine("PASS: merge markers, Unicode, BOM/newlines, bounded atomic saves and stale file rejection");
        await GitConflictTests.RunAsync();
        Console.WriteLine("PASS: actual Git conflict snapshots, staged resolution and stale conflict rejection");
        await SvnConflictTests.RunAsync();
        Console.WriteLine("PASS: SVN text conflict resolution fixtures (no SVN server connection)");
    }
}
