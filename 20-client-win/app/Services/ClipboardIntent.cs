// P3c -- 粘贴到输入框时,剪贴板里的东西该当成什么。
//
// 抽成纯函数是为了【能单测】—— 真剪贴板在无头自检里没法可靠构造,但这条规则本身必须钉死:
//   · 有文件 -> 当附件(即便同时有文本 —— 资源管理器复制文件时会附带路径文本,
//     那串路径文本不是用户想要的内容);
//   · 只有图片 -> 当附件(截图的典型情形);
//   · 图片 + 文本 -> 走文本(比如从网页复制的富文本,用户多半要的是字);
//   · 只有文本 -> 走文本。
//
// ★ 另一半的坑在调用处:WPF 的 TextBox 在剪贴板【没有文本格式】时会认为
//   Paste 命令不可执行,于是 DataObject.Pasting 根本不触发 —— 所以 Ctrl+V 必须在
//   按键层自己处理,不能只挂粘贴处理器(用户实测"截图粘不进去"就是这个原因)。

namespace LocalAI.Client.Services;

public static class ClipboardIntent
{
    public enum Kind { Text, Image, Files }

    public static Kind Decide(bool hasFiles, bool hasImage, bool hasText)
    {
        if (hasFiles) return Kind.Files;          // 文件优先:附带的路径文本不是用户要的内容
        if (hasImage && !hasText) return Kind.Image;
        return Kind.Text;
    }
}
