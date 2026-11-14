using MoaVcs.App.Services;
using MoaVcs.Core;

namespace MoaVcs.Tests;

internal static class CheckoutPathTests
{
    public static void Run()
    {
        Assert(CheckoutPath.SuggestFolderName("https://host.invalid/my%20repo/.git", VcsKind.Git) == "my repo",
            "Git metadata URL uses the decoded parent repository name");
        Assert(CheckoutPath.SuggestFolderName("https://host.invalid/team/repo.git?branch=main#readme", VcsKind.Git) == "repo",
            "HTTPS final component ignores query and fragment and strips Git suffix");
        Assert(CheckoutPath.SuggestFolderName("https://host.invalid/team/repo.git/", VcsKind.Git) == "repo",
            "HTTPS trailing slash");
        Assert(CheckoutPath.SuggestFolderName("git@host.invalid:team/repo.GIT", VcsKind.Git) == "repo",
            "SSH scp-style address and case-insensitive Git suffix");
        Assert(CheckoutPath.SuggestFolderName("ssh://git@host.invalid/team/my%20repo.git?ref=main", VcsKind.Git) == "my repo",
            "SSH URI decodes one final path component and ignores query");
        Assert(CheckoutPath.SuggestFolderName("file:///C:/repositories/local%20repo.git", VcsKind.Git) == "local repo",
            "file URI");
        Assert(CheckoutPath.SuggestFolderName("lore://user:secret@host.invalid/team/repo?ref=main#changes", VcsKind.Lore) == "repo",
            "Lore URI ignores credentials, query and fragment");
        Assert(CheckoutPath.SuggestFolderName("lores://user:secret@host.invalid/team/my%20repo?ref=main", VcsKind.Lore) == "my repo",
            "secure Lore URI");
        Assert(CheckoutPath.SuggestFolderName("svn+ssh://user@host.invalid/team/project/trunk?rev=4", VcsKind.Svn) == "trunk",
            "SVN over SSH URI");
        Assert(CheckoutPath.SuggestFolderName("https://user:secret@host.invalid/team/repo.git?ref=main", VcsKind.Git) == "repo",
            "HTTPS credentials and query are excluded from the name");
        Assert(CheckoutPath.SuggestFolderName("https://host.invalid/svn/project/trunk", VcsKind.Svn) == "trunk",
            "SVN trunk address");
        Assert(CheckoutPath.SuggestFolderName("https://host.invalid/team/repo.git", VcsKind.Lore) == "repo.git",
            "only Git removes the .git suffix");
        Assert(CheckoutPath.SuggestFolderName("C:\\source\\repo.git", VcsKind.Git) == "repo",
            "Windows drive path");
        Assert(CheckoutPath.SuggestFolderName("\\\\server\\share\\repo.git", VcsKind.Git) == "repo",
            "UNC path");
        Assert(CheckoutPath.SuggestFolderName("../local/repo.git", VcsKind.Git) == "repo",
            "relative local path");
        Assert(CheckoutPath.SuggestFolderName("../local/percent%2Fname", VcsKind.Git) == "percent%2Fname",
            "percent escapes in local paths remain literal");
        Assert(CheckoutPath.SuggestFolderName("https://host.invalid/team/.git", VcsKind.Git) == "team",
            "Git repository URL ending in /.git uses its parent name");

        foreach (var address in new[]
        {
            "https://host.invalid/", "https://", "https://host.invalid/%2E%2E", "https://host.invalid/a%2Fb",
            "https://host.invalid/CON.txt", "https://host.invalid/LPT9", "https://host.invalid/name.",
            "https://host.invalid/name%20", "https://host.invalid/bad%3Fname", "https://host.invalid/bad%ZZ",
            "https://host.invalid/" + new string('x', 256), "../local/.", "../local/..",
            "-https://host.invalid/team/repo", "https://host.invalid/team/repo\n"
        })
            Assert(CheckoutPath.SuggestFolderName(address, VcsKind.Git).Length == 0,
                $"unsafe or missing leaf is not suggested: {address}");

        var root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert(CheckoutPath.GetDestination(root, "safe-folder") == Path.GetFullPath(Path.Combine(root, "safe-folder")),
            "filesystem root is an allowed parent");
        foreach (var leaf in new[] { "", ".", "..", "bad/name", "bad\\name", "bad:name", "bad?name", "name.", "name ", "CON", "NUL.txt", "COM¹.log", new string('x', 256) })
            Throws(() => CheckoutPath.GetDestination(root, leaf), $"manual destination leaf rejected: {leaf}");
        Throws(() => CheckoutPath.GetDestination("relative-parent", "repo"), "parent must be absolute");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Checkout path test failed: " + message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (VcsException) { return; }
        throw new InvalidOperationException("Checkout path test failed: " + message);
    }
}
