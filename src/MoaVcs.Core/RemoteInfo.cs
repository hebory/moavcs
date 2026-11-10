namespace MoaVcs.Core;

/// <summary>Configured and effective URL lists for a repository remote.</summary>
public sealed record RemoteInfo(
    string Name,
    bool Exists,
    IReadOnlyList<string> FetchUrls,
    IReadOnlyList<string> PushUrls,
    IReadOnlyList<string> EffectiveFetchUrls,
    IReadOnlyList<string> EffectivePushUrls,
    string? ReadOnlyReason = null);

/// <summary>Shared validation for remote URL input.</summary>
public static class RemoteUrl
{
    public static string Validate(string url)
    {
        if (url is null) throw new VcsException("원격 주소를 입력하세요.");
        if (url.Any(char.IsControl)) throw new VcsException("원격 주소에는 줄바꿈이나 제어 문자를 사용할 수 없습니다.");
        var value = url.Trim();
        if (value.Length == 0) throw new VcsException("원격 주소를 입력하세요.");
        if (value.StartsWith("-", StringComparison.Ordinal))
            throw new VcsException("하이픈(-)으로 시작하는 원격 주소는 사용할 수 없습니다. 로컬 상대 경로라면 ./를 앞에 붙이세요.");
        return value;
    }
}
