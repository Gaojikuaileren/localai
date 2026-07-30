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
    public static UIElement Build(Project? existing, Action onDone, string workspaceKey = "chat", Action<Project>? onJump = null)
    {
        var name = new TextBox { Text = existing?.Title ?? "", Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(8, 5, 8, 5) };

        // ---- 文件夹所在机器:本机 / 其它已配对的电脑 ----
        //   ★ 没有别的已配对电脑时【只显示本机】(用户裁定),不摆一个空下拉装样子。
        var machines = MachineOptions();
        string? hostMachine = existing?.HostMachine;
        var machineBox = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
        foreach (var (_, label) in machines) machineBox.Items.Add(label);
        machineBox.SelectedIndex = Math.Max(0, machines.FindIndex(m => (m.Key ?? "") == (hostMachine ?? "")));

        string? folder = existing?.FolderPath;
        var folderLabel = FieldLabel(folder ?? "未选择(必填)");

        // 同路径提示 + 主按钮在"创建项目 / 转跳至该项目"之间切换
        var dupHint = FieldLabel("");
        dupHint.SetResourceReference(TextBlock.ForegroundProperty, "RiskWarning");
        Project? dup = null;
        Action refreshDup = () => { };   // 下面接上(需要先声明 save 按钮)

        var pickFolder = Ui.Secondary("选择文件夹", (_, _) =>
        {
            var f = ProjectUi.PickFolder("选择项目的文件夹");
            if (f is not null) { folder = f; folderLabel.Text = f; refreshDup(); }
        });
        machineBox.SelectionChanged += (_, _) =>
        {
            hostMachine = machines[Math.Max(0, machineBox.SelectedIndex)].Key;
            refreshDup();
        };

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
            // ★ 同路径已有项目 -> 这颗按钮此刻是【转跳至该项目】,不再建新的
            if (dup is not null) { if (onJump is not null) onJump(dup); else onDone(); return; }

            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            if (folder is null) { ConfirmDialog.Show("还没选文件夹", "请先为项目选择一个文件夹。", confirmText: "好", cancelText: "关闭"); return; }
            var ai = (AiPermission)Math.Max(0, aiPerm.SelectedIndex);
            var atts = attachSlots.Select(x => x.Get()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
            if (existing is null)
            {
                var p = TheApp.Projects.Create(name.Text.Trim(), folder, atts, ProjectScope.Personal, workspaceKey, hostMachine);
                TheApp.Projects.SetAiPermission(p.ProjectId, ai);
            }
            else
            {
                TheApp.Projects.Update(existing with { Title = name.Text.Trim(), FolderPath = folder, Attachments = atts, Ai = ai, HostMachine = hostMachine });
            }
            onDone();
        });

        // 选完路径就查:同一台机器 + 完全相同的路径(子路径不算)是否已经有项目 —— 含已完成/已删除的
        refreshDup = () =>
        {
            dup = TheApp.Projects.FindByFolder(folder, hostMachine, excludeId: existing?.ProjectId);
            if (dup is null)
            {
                dupHint.Text = "";
                save.Content = existing is null ? "创建项目" : "保存";
                return;
            }
            var where = dup.DeletedAt is not null ? "已删除项目"
                      : dup.Status == ProjectStatus.Done ? "已完成项目" : "进行中";
            dupHint.Text = $"该路径已经有项目「{dup.Title}」({where})—— 一个文件夹只对应一个项目。";
            save.Content = "转跳至该项目";
        };
        refreshDup();
        var cancel = Ui.Secondary("取消", (_, _) => onDone());
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(save);
        var cancelWrap = new Border { Child = cancel, Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(cancelWrap);

        return Ui.Stack(
            Ui.Caption("项目名"), name,
            Ui.Caption("文件夹所在机器"), machineBox,
            Ui.Caption("项目文件夹(必选)"), pickFolder, folderLabel, dupHint,
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

    /// <summary>
    /// 文件夹所在机器的可选项:本机 + 其它【已配对】的电脑。
    /// ★ 诚实:没配对(或拿不到设备表)就只有本机 —— 不摆一个假的远程列表。
    ///   远程机器上的项目文件夹要真正可读写,还需要中枢侧的文件访问(P4+),接入前只作标记。
    /// </summary>
    static List<(string? Key, string Label)> MachineOptions()
    {
        var list = new List<(string?, string)> { (ProjectCenter.LocalMachine, $"本机({Environment.MachineName})") };
        foreach (var d in TheApp.Hub.KnownDevices)
            if (!string.IsNullOrWhiteSpace(d.DeviceId)) list.Add((d.DeviceId, d.DisplayName));
        return list;
    }

    static TextBlock FieldLabel(string text)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FontCaption");
        return t;
    }
}
