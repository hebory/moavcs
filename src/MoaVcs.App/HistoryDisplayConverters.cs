using System.Globalization;
using System.Windows.Data;
using MoaVcs.Core;

namespace MoaVcs.App;

public sealed class HistoryReferenceLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is HistoryReference reference
        ? reference.Kind switch
        {
            HistoryReferenceKind.Head => "HEAD",
            HistoryReferenceKind.LocalBranch => reference.Name,
            HistoryReferenceKind.RemoteBranch => reference.Name,
            HistoryReferenceKind.Tag => "tag: " + reference.Name,
            _ => reference.Name
        }
        : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HistoryRelationLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is HistoryRelation relation
        ? relation switch
        {
            HistoryRelation.LocalOnly => "로컬만",
            HistoryRelation.UpstreamOnly => "추적만",
            HistoryRelation.Shared => "공통",
            HistoryRelation.Other => "다른 이력",
            _ => "—"
        }
        : "—";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HistoryShortIdConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string id && id.Length > 12
        ? id[..12]
        : value ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HistoryReferencesDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable<HistoryReference> references
            ? string.Join(Environment.NewLine, references.Select(reference => reference.Kind switch
            {
                HistoryReferenceKind.Head => "HEAD (현재 체크아웃한 커밋)",
                HistoryReferenceKind.LocalBranch => "로컬 브랜치: " + reference.Name,
                HistoryReferenceKind.RemoteBranch => "원격 추적 브랜치: " + reference.Name,
                _ => "태그: " + reference.Name
            })) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HistoryDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : value ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
