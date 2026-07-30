// P3c -- 学习笔记编辑器(右侧抽屉)。
//
// 用户裁定:笔记"可以自己在格式内编辑" —— 所以字段是【固定的】,由档位决定显示哪几个:
//   精简   :译文
//   带读音 :译文 + 读音
//   带例句 :+ 例句(目标语 / 译文 / 读音)
//   详解   :+ 逐词详解(一行一个词:词|词性|释义)
// 这样笔记既能被 AI 按格式填,也能被人按格式改,不会变成一坨自由文本。

using System.Windows;
using System.Windows.Controls;
using LocalAI.Client.Services;

namespace LocalAI.Client.Views;

public static class NoteEditor
{
    static App TheApp => (App)Application.Current;

    public static UIElement Build(StudyNote? existing, TranslationLevel currentLevel, IReadOnlyList<string> poolTargets)
    {
        var level = existing?.Level ?? currentLevel;

        // 目标语言:新建时从目标池里选(池空则给常用语言全表)
        var langs = poolTargets.Count > 0 ? poolTargets.ToList() : TheApp.Settings.TranslationPool.ToList();
        var langBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        foreach (var c in langs) langBox.Items.Add(Languages.NameOf(c));
        var li = existing is null ? 0 : Math.Max(0, langs.IndexOf(existing.Lang));
        langBox.SelectedIndex = li;

        var levelBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        foreach (var (_, name, desc) in TranslationLevels.All) levelBox.Items.Add($"{name} —— {desc}");
        levelBox.SelectedIndex = (int)level;

        var source = Field(existing?.SourceText ?? "");
        var translation = Field(existing?.Translation ?? "");
        var reading = Field(existing?.Reading ?? "");
        var exSrc = Field(existing?.ExampleSource ?? "");
        var exTr = Field(existing?.ExampleTranslation ?? "");
        var exRd = Field(existing?.ExampleReading ?? "");
        var words = Field(existing?.Words is { Count: > 0 }
            ? string.Join("\n", existing.Words.Select(w => $"{w.Word}|{w.Pos}|{w.Meaning}"))
            : "");
        words.AcceptsReturn = true;
        words.TextWrapping = TextWrapping.Wrap;
        words.MinHeight = 60;

        // 按档位显示/隐藏字段 —— 档位就是格式契约
        var readingBox = Ui.Stack(Ui.Caption("读音"), reading);
        var exampleBox = Ui.Stack(Ui.Caption("例句(目标语)"), exSrc, Ui.Caption("例句译文"), exTr, Ui.Caption("例句读音"), exRd);
        var wordsBox = Ui.Stack(Ui.Caption("逐词详解(一行一个:词|词性|释义)"), words);
        void SyncLevel()
        {
            var lv = (TranslationLevel)Math.Max(0, levelBox.SelectedIndex);
            readingBox.Visibility = lv >= TranslationLevel.Pronunciation ? Visibility.Visible : Visibility.Collapsed;
            exampleBox.Visibility = lv >= TranslationLevel.Example ? Visibility.Visible : Visibility.Collapsed;
            wordsBox.Visibility = lv >= TranslationLevel.Detailed ? Visibility.Visible : Visibility.Collapsed;
        }
        levelBox.SelectionChanged += (_, _) => SyncLevel();
        SyncLevel();

        var save = Ui.Primary(existing is null ? "保存笔记" : "保存", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(translation.Text)) { translation.Focus(); return; }
            var lv = (TranslationLevel)Math.Max(0, levelBox.SelectedIndex);
            var glosses = words.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('|'))
                .Where(p => p.Length >= 1 && !string.IsNullOrWhiteSpace(p[0]))
                .Select(p => new WordGloss(p[0].Trim(),
                                           "",
                                           p.Length > 1 ? p[1].Trim() : "",
                                           p.Length > 2 ? p[2].Trim() : ""))
                .ToList();

            var note = new StudyNote(
                Id: existing?.Id ?? "",
                Lang: langs[Math.Max(0, langBox.SelectedIndex)],
                SourceText: source.Text.Trim(),
                SourceLang: existing?.SourceLang ?? Languages.Detect(source.Text),
                Translation: translation.Text.Trim(),
                Level: lv,
                Reading: Empty(reading.Text),
                ExampleSource: Empty(exSrc.Text),
                ExampleTranslation: Empty(exTr.Text),
                ExampleReading: Empty(exRd.Text),
                Words: lv >= TranslationLevel.Detailed && glosses.Count > 0 ? glosses : null,
                CreatedAt: existing?.CreatedAt,
                CreatedByAi: existing?.CreatedByAi ?? false);   // 手改不改变"是不是 AI 建的"这个出处

            if (existing is null) TheApp.Notes.Add(note); else TheApp.Notes.Update(note);
            Overlay.CloseActive();
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(save);
        if (existing is not null)
        {
            var del = Ui.Danger("删除", (_, _) =>
            {
                if (!ConfirmDialog.Show("删除笔记", "删除这条学习笔记?此操作不可撤销。", confirmText: "删除", danger: true)) return;
                TheApp.Notes.Remove(existing.Id);
                Overlay.CloseActive();
            });
            del.Margin = new Thickness(10, 0, 0, 0);
            buttons.Children.Add(del);
        }

        return new ScrollViewer
        {
            Content = Ui.Stack(
                Ui.Caption("目标语言"), langBox,
                Ui.Caption("格式(档位)"), levelBox,
                Ui.Caption("原文"), source,
                Ui.Caption("译文"), translation,
                readingBox, exampleBox, wordsBox,
                buttons,
                Ui.Caption("★ 字段由档位决定 —— AI 按同一套格式填,你也按同一套格式改。")),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        }.PassThrough();
    }

    static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static TextBox Field(string text) => new()
    {
        Text = text, Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(8, 5, 8, 5),
    };
}
