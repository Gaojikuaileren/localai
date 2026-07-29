// P3c -- 项目编辑器(新建 + 编辑共用一套界面)。用户裁定:
//   · 新建项目 与 编辑已有项目(重定向文件夹路径 / 改附件夹 / 改 AI 权限 / 改名)用【同一个界面】;
//   · 建/存完回到调用处(onDone)。
//
// 文件夹是真实本机路径(选择器选)。项目为内存态,增改当场生效(与其它示例数据同)。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public static class ProjectEditor
{
    static App TheApp => (App)Application.Current;

    /// <summary>existing == null = 新建;否则 = 编辑那一个。onDone 在保存/取消后回调(回到网格 / 关抽屉)。</summary>
    public static UIElement Build(Project? existing, Action onDone)
    {
        var name = new TextBox { Text = existing?.Title ?? "", Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(8, 5, 8, 5) };

        string? folder = existing?.FolderPath;
        string? attach = existing?.AttachmentPath;
        var folderLabel = FieldLabel(folder ?? "未选择(必填)");
        var attachLabel = FieldLabel(attach ?? "未选择(可选)");

        var pickFolder = Ui.Secondary("选择文件夹", (_, _) =>
        {
            var f = ProjectUi.PickFolder("选择项目的文件夹");
            if (f is not null) { folder = f; folderLabel.Text = f; }
        });
        var pickAttach = Ui.Secondary("选择附件文件夹", (_, _) =>
        {
            var f = ProjectUi.PickFolder("选择附件文件夹(可选)");
            if (f is not null) { attach = f; attachLabel.Text = f; }
        });

        var aiPerm = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var perm in new[] { AiPermission.ReadOnly, AiPermission.Ask, AiPermission.Edit }) aiPerm.Items.Add(ProjectUi.AiLabel(perm));
        aiPerm.SelectedIndex = (int)(existing?.Ai ?? AiPermission.Ask);
        var aiHint = FieldLabel(ProjectUi.AiHint((AiPermission)aiPerm.SelectedIndex));
        aiPerm.SelectionChanged += (_, _) => aiHint.Text = ProjectUi.AiHint((AiPermission)Math.Max(0, aiPerm.SelectedIndex));

        var save = Ui.Primary(existing is null ? "创建项目" : "保存", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            if (folder is null) { MessageBox.Show("请先为项目选择一个文件夹。", "本地 AI 中枢", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var ai = (AiPermission)Math.Max(0, aiPerm.SelectedIndex);
            if (existing is null)
            {
                var p = TheApp.Projects.Create(name.Text.Trim(), folder, attach, ProjectScope.Personal);
                TheApp.Projects.SetAiPermission(p.ProjectId, ai);
            }
            else
            {
                TheApp.Projects.Update(existing with { Title = name.Text.Trim(), FolderPath = folder, AttachmentPath = attach, Ai = ai });
            }
            onDone();
        });
        var cancel = Ui.Secondary("取消", (_, _) => onDone());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(save);
        var cancelWrap = new Border { Child = cancel, Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(cancelWrap);

        return Ui.Stack(
            Ui.Caption("项目名"), name,
            Ui.Caption("项目文件夹(必选)"), pickFolder, folderLabel,
            new Border { Height = 6 },
            Ui.Caption("附件文件夹(可选)"), pickAttach, attachLabel,
            new Border { Height = 6 },
            Ui.Caption("AI 权限"), aiPerm, aiHint,
            buttons,
            new Border { Height = 4 },
            Ui.Caption(existing is null
                ? "建好后,这个项目下的会话会归到它名下;也可以把普通会话移动过来。"
                : "改路径 = 重定向到新文件夹(不搬动文件)。"));
    }

    static TextBlock FieldLabel(string text)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        return t;
    }
}
