using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoaVcs.Core;

namespace MoaVcs.App;

/// <summary>Edits the first configured fetch URL for the Git origin remote.</summary>
public sealed class RemoteSettingsWindow : Window
{
    private readonly TextBlock _notice = new()
    {
        Name = "RemoteValidationNotice",
        Foreground = Brushes.Firebrick,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 10, 0, 0)
    };
    private readonly Button _save;

    public TextBox RemoteUrlTextBox { get; }
    public string Url => RemoteUrl.Validate(RemoteUrlTextBox.Text);

    public RemoteSettingsWindow(string repositoryPath, RemoteInfo remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        NameScope.SetNameScope(this, new NameScope());
        Width = 680;
        Height = 610;
        var panel = DialogLayout.Create(this, "원격 주소", "현재 저장소의 origin fetch 주소를 확인하고 변경합니다.");

        panel.Children.Add(new TextBlock
        {
            Text = repositoryPath,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = repositoryPath,
            Foreground = Brushes.SlateGray,
            Margin = new Thickness(0, 0, 0, 16)
        });

        DialogLayout.Label(panel, remote.Exists ? "origin 기본 fetch 주소" : "새 origin fetch 주소");
        RemoteUrlTextBox = new TextBox
        {
            Name = "RemoteUrlInput",
            Text = remote.FetchUrls.FirstOrDefault() ?? "",
            IsReadOnly = !string.IsNullOrWhiteSpace(remote.ReadOnlyReason),
            Height = 40,
            Padding = new Thickness(8, 3, 8, 3),
            ToolTip = "HTTPS, SSH 또는 로컬 저장소 경로"
        };
        RegisterName(RemoteUrlTextBox.Name, RemoteUrlTextBox);
        panel.Children.Add(RemoteUrlTextBox);

        panel.Children.Add(new TextBlock
        {
            Text = remote.Exists
                ? "저장하면 기본 fetch 주소만 변경합니다. 추가 fetch 주소와 별도 push 주소는 그대로 유지됩니다."
                : "origin이 아직 없습니다. 저장하면 이 주소로 origin을 추가합니다.",
            FontSize = 11,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 4)
        });

        AddReadOnlyList(panel, "추가 fetch 주소", remote.FetchUrls.Skip(1));
        AddReadOnlyList(panel, "별도 push 주소", remote.PushUrls);
        AddRewriteNotice(panel, "실제 적용 fetch 주소", remote.FetchUrls, remote.EffectiveFetchUrls);
        AddRewriteNotice(panel, "실제 적용 push 주소", remote.PushUrls, remote.EffectivePushUrls);

        if (!string.IsNullOrWhiteSpace(remote.ReadOnlyReason))
        {
            panel.Children.Add(new TextBlock
            {
                Text = remote.ReadOnlyReason,
                Foreground = Brushes.Firebrick,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0)
            });
        }

        panel.Children.Add(_notice);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _save = DialogLayout.Button("저장", Save_Click, accent: true);
        _save.Name = "SaveRemoteButton";
        RegisterName(_save.Name, _save);
        _save.IsDefault = true;
        _save.IsEnabled = string.IsNullOrWhiteSpace(remote.ReadOnlyReason);
        var cancel = DialogLayout.Button("취소", (_, _) => DialogResult = false);
        cancel.Name = "CancelRemoteButton";
        RegisterName(cancel.Name, cancel);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(10, 8, 0, 0);
        buttons.Children.Add(_save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = Url;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _notice.Text = ex.Message;
        }
    }

    private static void AddReadOnlyList(Panel panel, string label, IEnumerable<string> values)
    {
        var items = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (items.Length == 0) return;
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, items),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.SlateGray
        });
    }

    private static void AddRewriteNotice(Panel panel, string label, IReadOnlyList<string> configured, IReadOnlyList<string> effective)
    {
        if (effective.Count == 0 || configured.SequenceEqual(effective, StringComparer.Ordinal)) return;
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, effective),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.SlateGray
        });
    }
}
