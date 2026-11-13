using System.Globalization;
using System.Windows.Data;

namespace MoaVcs.App;

/// <summary>Friendly display labels without changing the provider's status values.</summary>
public sealed class FileStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string)?.ToLowerInvariant() switch
        {
            "untracked" or "unversioned" => "미등록",
            "modified" or "edit" or "m" => "수정",
            "added" or "add" or "a" => "추가",
            "deleted" or "delete" or "d" => "삭제",
            "renamed" => "이름 변경",
            "moved" or "v" or "move/add" or "move/delete" => "이동",
            "copied" or "c" => "복사",
            "conflict" or "conflicted" => "충돌",
            "ignored" => "제외됨",
            "missing" => "누락",
            "replaced" => "교체",
            "type changed" => "유형 변경",
            "purged" => "내용 제거됨",
            "property modified" => "속성 수정",
            "obstructed" => "경로 충돌",
            "changed" => "변경",
            "unchanged" or "normal" => "변경 없음",
            _ => value
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
