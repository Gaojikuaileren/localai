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

    /// <summary>existing == null = 新建(建在 workspaceKey 空间下);否则 = 编辑那一个。onDone 在保存/取消后回调。</summary>
    public static UIElement Build(Project? existing, Action onDone, string workspaceKey = "chat")
    {
        var name = new TextBox { Text = existing?.Title ?? "", Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(8, 5, 8, 5) };

        string? folder = existing?.FolderPath;
        var folderLabel = FieldLabel(folder ?? "未选择(必填)");
        var pickFolder = Ui.Secondary("选择文件夹", (_, _) =>
        {
            var f = ProjectUi.PickFolder("选择项目的文件夹");
            if (f is not null) { folder = f; folderLabel.Text = f; }
        });

        // ---- 附件文件夹:一开始不显示,靠"+ 添加附件文件夹"逐个加,可多个(用户裁定)----
        //   每个槽:选择按钮 + 路径标签 + 移除;有【空槽】时再按 + 会震荡提醒,不重复加空槽。
        var attachSlots = new List<(FrameworkElement Row, Func<string?> Get)>();
        var attachHost = new StackPanel();

        FrameworkElement AddAttachSlot(string? initial)
        {
            string? path = initial;
            var lbl = FieldLabel(path ?? "未选择");
            var pick = Ui.Secondary("选择附件文件夹", (_, _) =>
            {
                var f = ProjectUi.PickFolder("选择附件文件夹");
                if (f is not null) { path = f; lbl.Text = f; }
            });
            var remove = new TextBlock { Text = "移除", Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            remove.SetResourceReference(TextBlock.ForegroundProperty, "RiskDanger");
            remove.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
            var top = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(remove, Dock.Right);
            top.Children.Add(remove);
            top.Children.Add(pick);
            var slotBox = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
            slotBox.Children.Add(top);
            slotBox.Children.Add(lbl);

            (FrameworkElement, Func<string?>) entry = (slotBox, () => path);
            attachSlots.Add(entry);
            remove.MouseLeftButtonUp += (_, _) => { attachHost.Children.Remove(slotBox); attachSlots.Remove(entry); };
            attachHost.Children.Add(slotBox);
            return slotBox;
        }

        foreach (var a in existing?.Attachments ?? Array.Empty<string>()) AddAttachSlot(a);

        var addAttach = Ui.Secondary("＋ 添加附件文件夹", (_, _) =>
        {
            // 已有【未选择的空槽】就震荡提醒,不再加新空槽
            var empty = attachSlots.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Get()));
            if (empty.Row is not null) { Ui.Shake(empty.Row); return; }
            AddAttachSlot(null);
        });

        var aiPerm = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var perm in new[] { AiPermission.ReadOnly, AiPermission.Ask, AiPermission.Edit }) aiPerm.Items.Add(ProjectUi.AiLabel(perm));
        aiPerm.SelectedIndex = (int)(existing?.Ai ?? AiPermission.Ask);
        var aiHint = FieldLabel(ProjectUi.AiHint((AiPermission)aiPerm.SelectedIndex));
        aiPerm.SelectionChanged += (_, _) => aiHint.Text = ProjectUi.AiHint((AiPermission)Math.Max(0, aiPerm.SelectedIndex));

        var save = Ui.Primary(existing is null ? "创建项目" : "保存", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            if (folder is null) { ConfirmDialog.Show("还没选文件夹", "请先为项目选择一个文件夹。", confirmText: "好", cancelText: "关闭"); return; }
            var ai = (AiPermission)Math.Max(0, aiPerm.SelectedIndex);
            var atts = attachSlots.Select(x => x.Get()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
            if (existing is null)
            {
                var p = TheApp.Projects.Create(name.Text.Trim(), folder, atts, ProjectScope.Personal, workspaceKey);
                TheApp.Projects.SetAiPermission(p.ProjectId, ai);
            }
            else
            {
                TheApp.Projects.Update(existing with { Title = name.Text.Trim(), FolderPath = folder, Attachments = atts, Ai = ai });
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
            Ui.Caption("附件文件夹(可选,可多个)"), attachHost, addAttach,
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
