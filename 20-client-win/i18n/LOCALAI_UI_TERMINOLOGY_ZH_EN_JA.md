# LocalAI 三语 UI 术语表

> 版本：2026-07-28  
> 语言：简体中文 `zh-CN` · 英语 `en-US` · 日语 `ja-JP`  
> 读取基线：`.localAI` `main @ 48356f9`  
> 依据：D36、D37、D42，以及 `PROJECT_PLAN_v3.0.md` 中已明确的客户端、记忆、组件、权限与资产术语  
> 性质：仓库外的 UI 文案交接稿；不修改项目决议，不代表要求提前实现未来阶段

## 1. Claude 使用说明

1. UI 文案优先使用本表中的同一个 key，不在不同页面自行发明同义词。
2. 本表只统一名称和提示语，不改变权限、阶段、接口或实现方案。
3. `预留`、`未来`项目只能用于占位或“尚未提供”提示，不能据此提前实现。
4. 内部枚举、组件 ID、能力别名和协议名保持原文，不翻译：
   - `component_id`
   - `assistant.fast`
   - `assistant.deep`
   - `trade.research`
   - `not_provisioned`
   - `contract_changed`
   - `requires_swap`
   - `DEGRADED_SAFE`
5. 占位符必须原样保留，例如 `{device}`、`{component}`、`{count}`、`{time}`、`{size}`。
6. 若本表与中央决议冲突，以当前 `DECISIONS.md` 为准，并先向用户指出冲突，不静默改词。

## 2. 语言与文风规则

| 项目 | `zh-CN` | `en-US` | `ja-JP` |
|---|---|---|---|
| 按钮 | 简短动词，如“生成”“另存” | Sentence case，如 “Save as” | 简短动作，如「生成」「別名で保存」 |
| 标题 | 不加句号 | Sentence case | 不加句点 |
| 进行状态 | “正在……” | “…ing” 或 “In progress” | 「〜しています」 |
| 错误信息 | 先说发生了什么，再说怎么办 | State the problem, then the remedy | 原因の後に対処方法を書く |
| 确认信息 | 明确将改变的对象 | Name the exact affected object | 変更対象を具体的に示す |
| 不可用状态 | 必须说明原因 | Always include the reason | 必ず理由を表示する |
| 技术缩写 | AI、GPU、VRAM、MCP、HTML、PDF 保留 | 保留 | 保留 |

### 2.1 固定用词规则

- 个人长期信息统一称“记忆 / Memory / 記憶”，不要在日语中用容易与 RAM 混淆的「メモリ」。
- GPU 显存统一称“显存 / VRAM / VRAM”，不要用笼统的“内存”。
- 可恢复删除统一写“移到回收站 / Move to recycle bin / ごみ箱に移動”，不要写“永久删除”。
- 只有真正不可恢复的操作才使用“永久删除 / Delete permanently / 完全に削除”。
- 结构上不存在的能力写“仅可在主机电脑上更改”，不要误写成“权限不足”。
- 资源拒绝不得只写“显存不足”，必须区分“超过桌面预留限制”和“此刻实际可用显存不足”。
- `MCP` 只出现在技术状态或设置中；面向普通用户的主导航使用“投资研究”。
- “PPT / 课程生成”的实际产物不是 `.pptx`。英文和日文界面统一使用 Slides，不暗示 Microsoft Office。

## 3. 主导航与全局入口

| Key | 简体中文 | English | 日本語 | 阶段/说明 |
|---|---|---|---|---|
| `nav.chat` | 聊天 | Chat | チャット | 六个主界面 |
| `nav.assets` | 资产生成 | Asset generation | アセット生成 | 六个主界面 |
| `nav.investment` | 投资研究 | Investment research | 投資リサーチ | D42 的“投资 MCP”面向用户的显示名；当前仅预留 |
| `nav.translation` | 翻译 | Translation | 翻訳 | 六个主界面 |
| `nav.courses` | PPT / 课程生成 | Slides & courses | スライド・教材作成 | 六个主界面；不生成 `.pptx` |
| `nav.computer_control` | 电脑操控 | PC control | PC操作 | 六个主界面 |
| `global.memory` | 记忆 | Memory | 記憶 | 贯穿入口 |
| `global.components` | 组件 | Components | コンポーネント | 贯穿入口 |
| `global.voice` | 语音 | Voice | 音声 | 贯穿能力 |
| `global.pet` | 桌面宠物 | Desktop companion | デスクトップ・コンパニオン | P8；不要提前实现 |
| `global.notifications` | 通知 | Notifications | 通知 | 通用 |
| `global.settings` | 设置 | Settings | 設定 | 通用 |
| `global.help` | 帮助 | Help | ヘルプ | 通用 |
| `global.host_admin` | 主机管理 | Host administration | ホスト管理 | 仅主机本地，不属于普通客户端能力 |

## 4. 通用动作

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `action.new` | 新建 | New | 新規作成 |  |
| `action.open` | 打开 | Open | 開く |  |
| `action.edit` | 编辑 | Edit | 編集 |  |
| `action.rename` | 重命名 | Rename | 名前を変更 |  |
| `action.duplicate` | 复制一份 | Duplicate | 複製 |  |
| `action.preview` | 预览 | Preview | プレビュー |  |
| `action.generate` | 生成 | Generate | 生成 |  |
| `action.regenerate` | 重新生成 | Generate again | 再生成 |  |
| `action.save` | 保存 | Save | 保存 |  |
| `action.save_as` | 另存 | Save as | 別名で保存 |  |
| `action.export` | 导出 | Export | 書き出し |  |
| `action.import` | 导入 | Import | 読み込み |  |
| `action.restore` | 恢复 | Restore | 復元 |  |
| `action.move_to_recycle_bin` | 移到回收站 | Move to recycle bin | ごみ箱に移動 | 默认删除方式 |
| `action.delete_permanently` | 永久删除 | Delete permanently | 完全に削除 | 仅不可恢复操作 |
| `action.refresh` | 刷新 | Refresh | 更新 |  |
| `action.retry` | 重试 | Try again | 再試行 |  |
| `action.cancel` | 取消 | Cancel | キャンセル |  |
| `action.close` | 关闭 | Close | 閉じる |  |
| `action.confirm` | 确认 | Confirm | 確認 | 普通确认 |
| `action.approve` | 批准 | Approve | 承認 | 有权限含义 |
| `action.reject` | 拒绝 | Reject | 却下 | 有权限含义 |
| `action.allow_once` | 本次放行 | Allow once | 今回のみ許可 | 单任务、单次出境 |
| `action.stop` | 停止 | Stop | 停止 | 普通停止 |
| `action.take_over` | 立即接管 | Take over now | 今すぐ操作を引き継ぐ | 电脑操控 |

## 5. 设备、连接与配对

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `device.host_pc` | 主机电脑 | Host PC | ホストPC | 持有后端与唯一真实状态 |
| `device.this_pc` | 这台电脑 | This PC | このPC |  |
| `device.lan_device` | 局域网设备 | LAN device | LANデバイス |  |
| `device.name` | 设备名称 | Device name | デバイス名 |  |
| `device.last_seen` | 上次在线 | Last seen | 最終接続 |  |
| `connection.connected` | 已连接 | Connected | 接続済み |  |
| `connection.connecting` | 正在连接… | Connecting… | 接続しています… |  |
| `connection.reconnecting` | 正在重新连接… | Reconnecting… | 再接続しています… |  |
| `connection.offline` | 主机电脑离线 | Host PC offline | ホストPCはオフラインです |  |
| `connection.disconnected` | 连接已断开 | Disconnected | 接続が切れました |  |
| `connection.revoked` | 此设备的访问权限已被撤销 | Access for this device has been revoked | このデバイスのアクセスは取り消されました |  |
| `pairing.title` | 配对此设备 | Pair this device | このデバイスをペアリング |  |
| `pairing.request` | 申请配对 | Request pairing | ペアリングを申請 |  |
| `pairing.request_sent` | 配对申请已发送 | Pairing request sent | ペアリング申請を送信しました |  |
| `pairing.fingerprint` | 设备短指纹 | Device short fingerprint | デバイスの短縮フィンガープリント | 不简写成“验证码” |
| `pairing.compare` | 请确认两台电脑显示的指纹完全一致 | Make sure both PCs show the same fingerprint | 2台のPCに同じフィンガープリントが表示されていることを確認してください | 带外比对 |
| `pairing.waiting_approval` | 等待主机电脑批准 | Waiting for approval on the host PC | ホストPCの承認を待っています |  |
| `pairing.approve_device` | 批准此设备 | Approve this device | このデバイスを承認 |  |
| `pairing.reject_device` | 拒绝此设备 | Reject this device | このデバイスを却下 |  |
| `pairing.expired` | 配对申请已过期，请重新申请 | Pairing request expired. Start again. | ペアリング申請の有効期限が切れました。もう一度申請してください |  |
| `protocol.mismatch` | 主机版本为 {host_version}，客户端版本为 {client_version}。请更新客户端。 | The host is on version {host_version}, but this client is on {client_version}. Update the client to continue. | ホストはバージョン {host_version}、このクライアントは {client_version} です。続行するにはクライアントを更新してください | 必须明确拒绝原因 |

## 6. 聊天、语音与记忆

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `chat.new_chat` | 新对话 | New chat | 新しいチャット |  |
| `chat.placeholder` | 输入消息… | Type a message… | メッセージを入力… |  |
| `chat.send` | 发送 | Send | 送信 |  |
| `chat.stop_response` | 停止生成 | Stop generating | 生成を停止 |  |
| `chat.local_model` | 本地模型 | Local model | ローカルモデル |  |
| `chat.current_model` | 当前模型 | Current model | 現在のモデル |  |
| `voice.start` | 开始语音 | Start voice | 音声を開始 |  |
| `voice.stop` | 结束语音 | End voice | 音声を終了 |  |
| `voice.listening` | 正在听… | Listening… | 聞き取っています… |  |
| `voice.transcribing` | 正在识别语音… | Transcribing… | 音声を認識しています… |  |
| `voice.speaking` | 正在朗读… | Speaking… | 読み上げています… |  |
| `memory.panel` | 记忆面板 | Memory | 記憶 | 面板标题可省略“面板” |
| `memory.browse` | 浏览记忆 | Browse memory | 記憶を見る |  |
| `memory.pending_review` | 待审核 | Awaiting review | 要確認 | 对应 pending review |
| `memory.source` | 来源 | Source | 出典 |  |
| `memory.source_device` | 记录设备 | Recorded on | 記録元デバイス |  |
| `memory.provenance` | 来源详情 | Source details | 出典の詳細 | UI 不直接暴露术语 provenance |
| `memory.remember_this` | 记住这条 | Remember this | これを記憶する |  |
| `memory.not_saved` | 未保存到记忆 | Not saved to memory | 記憶には保存されていません |  |
| `memory.value_not_here` | 值不在这里 | The value is not stored here | 値はここに保存されていません | 凭证引用固定提示 |
| `memory.retrieval_method_manual` | 取值方式：由你手动填写 | How to retrieve it: enter it yourself | 取得方法：自分で入力 |  |

## 7. 翻译工作室

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `translation.title` | 翻译 | Translation | 翻訳 |  |
| `translation.source_language` | 原文语言 | Source language | 翻訳元の言語 |  |
| `translation.target_language` | 译文语言 | Target language | 翻訳先の言語 |  |
| `translation.swap_languages` | 交换语言 | Swap languages | 言語を入れ替える |  |
| `translation.source_text` | 输入要翻译的内容 | Enter text to translate | 翻訳する内容を入力 |  |
| `translation.result` | 翻译结果 | Translation | 翻訳結果 |  |
| `translation.quick` | 快速翻译 | Quick translation | かんたん翻訳 | 只输出译文 |
| `translation.detailed` | 详细解释 | Detailed explanation | くわしい解説 | 含语法、用法、例句 |
| `translation.grammar` | 语法 | Grammar | 文法 |  |
| `translation.usage` | 用法 | Usage | 使い方 |  |
| `translation.examples` | 例句 | Examples | 例文 |  |
| `translation.translate` | 翻译 | Translate | 翻訳 | 按钮 |
| `translation.loading_model` | 正在加载翻译模型… | Loading the translation model… | 翻訳モデルを読み込んでいます… | D42 固定情形 |
| `translation.ephemeral_notice` | 翻译内容默认不会保存到记忆 | Translations are not saved to memory by default | 翻訳内容は初期設定では記憶に保存されません | 当前规则 |
| `translation.remember_word` | 记住我查过的词 | Remember words I look up | 調べた単語を記憶する | 未来功能；当前不显示为可用 |

### 7.1 语言名称

| Key | 简体中文 | English | 日本語 |
|---|---|---|---|
| `language.chinese` | 中文 | Chinese | 中国語 |
| `language.japanese` | 日语 | Japanese | 日本語 |
| `language.german` | 德语 | German | ドイツ語 |
| `language.english` | 英语 | English | 英語 |
| `language.auto_detect` | 自动检测 | Detect automatically | 自動検出 |

> UI 语言只有中、英、日；德语只是翻译内容语言。不要把德语加入客户端界面语言选择器。

## 8. PPT / 课程生成工作室

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `course.title` | PPT / 课程生成 | Slides & courses | スライド・教材作成 | 不暗示 `.pptx` |
| `course.new_project` | 新建课程项目 | New course project | 新しい教材プロジェクト |  |
| `course.preset` | 课程预设 | Course preset | 教材プリセット |  |
| `course.preset_list` | 课程预设列表 | Course presets | 教材プリセット一覧 |  |
| `course.create_preset` | 新建预设 | New preset | プリセットを作成 |  |
| `course.duplicate_preset` | 复制预设 | Duplicate preset | プリセットを複製 |  |
| `course.edit_preset` | 编辑预设 | Edit preset | プリセットを編集 |  |
| `course.recycle_preset` | 将预设移到回收站 | Move preset to recycle bin | プリセットをごみ箱に移動 | 删除可逆 |
| `course.course_name` | 课程名称 | Course name | 教材名 |  |
| `course.audience` | 目标听众 | Audience | 対象者 |  |
| `course.layout_colors` | 版式与配色 | Layout and colors | レイアウトと配色 |  |
| `course.section_structure` | 章节结构 | Section structure | セクション構成 |  |
| `course.glossary` | 术语表 | Glossary | 用語集 |  |
| `course.standing_instructions` | 固定生成要求 | Standing instructions | 常設の生成指示 |  |
| `course.outline` | 大纲 | Outline | アウトライン |  |
| `course.slide_structure` | 幻灯结构 | Slide structure | スライド構成 |  |
| `course.generate_outline` | 生成大纲 | Generate outline | アウトラインを生成 |  |
| `course.generate_slides` | 生成幻灯 | Generate slides | スライドを生成 |  |
| `course.app_preview` | 应用内预览 | In-app preview | アプリ内プレビュー |  |
| `course.self_contained_html` | 自包含 HTML 幻灯 | Self-contained HTML slides | 単体で開けるHTMLスライド | 浏览器直接打开 |
| `course.markdown_source` | Markdown 源文件 | Markdown source | Markdownソース |  |
| `course.export_pdf` | 导出 PDF | Export PDF | PDFに書き出す |  |
| `course.save_to_folder` | 保存到课程文件夹 | Save to course folder | 教材フォルダーに保存 | P6 就绪后 |
| `course.save_yourself` | 由我另存 | I’ll save it myself | 自分で保存する | P6 前默认路径 |
| `course.write_directly` | 由 AI 直接写入文件 | Let AI write the file | AIがファイルに直接保存 | 需要 P6 与批准 |
| `course.not_memory` | 课程预设是文件，不会保存到记忆 | Course presets are files and are not saved to memory | 教材プリセットはファイルとして保存され、記憶には入りません | 固定说明 |
| `course.office_notice` | 输出为 HTML 幻灯，可在浏览器中打开；不需要 Microsoft Office。 | The output is an HTML slide deck that opens in a browser. Microsoft Office is not required. | 出力はブラウザーで開けるHTMLスライドです。Microsoft Officeは必要ありません。 | 防止误解 |

## 9. 电脑操控与文件操作

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `control.mode` | 操控模式 | Control mode | 操作モード |  |
| `control.guided_mode` | 引导模式 | Guided mode | ガイドモード | 先交付 |
| `control.guided_description` | AI 只移动鼠标并告诉你在哪里点击；点击由你完成。 | AI moves the pointer and shows you where to click. You make the click. | AIはポインターを移動してクリック位置を案内します。クリックはあなたが行います。 | 不可缩成“半自动” |
| `control.direct_mode` | 直接模式 | Direct mode | ダイレクトモード | P6 安全前置完成后 |
| `control.direct_description` | AI 可以直接点击和输入；敏感操作仍需逐次确认。 | AI can click and type directly. Sensitive actions still require confirmation each time. | AIが直接クリックや入力を行います。重要な操作は毎回確認が必要です。 |  |
| `control.move_pointer` | 将鼠标移到目标位置 | Move pointer to target | ポインターを対象位置へ移動 |  |
| `control.action_preview` | 动作预览 | Action preview | 操作プレビュー |  |
| `control.confirm_each_action` | 每次操作前确认 | Confirm before each action | 操作のたびに確認 |  |
| `control.emergency_stop` | 紧急停止 | Emergency stop | 緊急停止 | 不使用模糊的“暂停” |
| `control.emergency_stop_hint` | 按 {shortcut} 立即停止 AI 的所有电脑操控 | Press {shortcut} to stop all AI control immediately | {shortcut} を押すと、AIによるPC操作を直ちに停止します | 全局热键 |
| `control.stopped_by_user` | 你已停止电脑操控 | You stopped PC control | PC操作を停止しました |  |
| `control.user_in_control` | 现在由你操作 | You are in control | あなたが操作しています |  |
| `control.permission_zone` | 允许访问的位置 | Allowed locations | アクセスを許可した場所 | UI 显示名；技术名仍为 permission zone |
| `control.read_only_location` | 只读位置 | Read-only location | 読み取り専用の場所 |  |
| `control.read_write_location` | 可读写位置 | Read and write location | 読み書きできる場所 |  |
| `control.outside_allowed_area` | 此位置不在允许访问的范围内 | This location is outside the allowed area | この場所はアクセス許可の範囲外です |  |
| `control.dry_run` | 仅预览，不执行 | Preview only | プレビューのみ | 文件整理 |
| `control.rollback` | 撤销本次更改 | Undo these changes | この変更を元に戻す |  |
| `control.isolation_area` | 隔离区 | Quarantine | 隔離領域 | 不等同系统回收站 |
| `control.isolated_browser` | 独立浏览器环境 | Separate browser environment | 分離されたブラウザー環境 | 不写“隐身模式” |

## 10. 组件、模型与显存

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `component.selector` | 组件选择器 | Component selector | コンポーネント選択 |  |
| `component.current` | 当前组件 | Current components | 現在のコンポーネント | P3c 先显示 |
| `component.selected` | 已选组件 | Selected components | 選択中のコンポーネント |  |
| `component.recommended` | 推荐组合 | Recommended sets | おすすめ構成 |  |
| `component.manual_override` | 手动选择 | Choose manually | 手動で選択 | 自动跟随的逃生口 |
| `component.auto_follow` | 自动跟随当前界面 | Follow the current screen automatically | 現在の画面に自動で合わせる | P4 后 |
| `component.loaded` | 已加载 | Loaded | 読み込み済み |  |
| `component.loading` | 正在加载… | Loading… | 読み込んでいます… |  |
| `component.unloading` | 正在卸载… | Unloading… | 解放しています… | 日语避免直译アンロード |
| `component.queued` | 等待中 | Waiting | 待機中 |  |
| `component.in_use` | 正在使用 | In use | 使用中 |  |
| `component.idle` | 空闲 | Idle | アイドル |  |
| `component.available` | 可用 | Available | 利用可能 |  |
| `component.unavailable` | 不可用 | Unavailable | 利用できません | 必须附原因 |
| `component.needs_swap` | 需要切换组件 | Component change required | コンポーネントの切り替えが必要です | 对应 `requires_swap` |
| `component.contract_changed` | 当前能力与请求不同 | Current capability differs from the request | 現在の機能構成はリクエストと異なります | 对应 `contract_changed`；同时显示真实契约 |
| `component.degraded_safe` | 已进入安全受限状态 | Running in safe limited mode | 安全な制限モードで動作しています | 对应 `DEGRADED_SAFE` |
| `component.not_provisioned` | 此功能尚未安装 | This feature is not installed yet | この機能はまだインストールされていません | 对应 `not_provisioned` |
| `component.measured` | 实测 {date} | Measured {date} | 実測 {date} |  |
| `component.estimated` | 估算 | Estimate | 推定 | 不加判定色 |
| `component.sampled_at` | 采样时间：{time} | Sampled at {time} | 計測時刻：{time} |  |
| `component.desktop_reserve` | 至少为桌面保留 | Reserved for desktop | デスクトップ用に最低限確保 | 对应 `desktop_floor` |
| `component.ai_maximum` | AI 最多可用 | Maximum for AI | AIが使用できる上限 | 导出值，不单独设置 |
| `component.available_now` | 此刻实际可用 | Available now | 現在の空き容量 |  |
| `component.on_demand` | 按需组件 | On-demand components | オンデマンド・コンポーネント |  |
| `component.on_demand_notice` | 勾上表示需要时可以申请；不代表现在占用，也不保证申请时一定成功。 | Selecting this allows the component to be requested when needed. It does not use VRAM now and does not guarantee that a future request will succeed. | 選択すると、必要なときに利用を申請できます。今すぐVRAMを使用するわけではなく、申請時に必ず利用できるとは限りません。 | 固定说明 |
| `component.will_load` | 将加载 | Will load | 読み込む項目 | diff |
| `component.will_unload` | 将卸载 | Will unload | 解放する項目 | diff |
| `component.affected_tasks` | 受影响的任务 | Affected tasks | 影響を受けるタスク | diff |
| `component.apply` | 应用更改 | Apply changes | 変更を適用 |  |
| `component.power_on` | 开启 | On | オン | 主界面二态 |
| `component.power_off` | 关闭 | Off | オフ | AI GPU 归零，但基础服务仍在 |
| `component.full_stop` | 完全停止 | Fully stop | 完全停止 | 托盘紧急全停 |

### 10.1 资源冲突固定文案

| Key | 简体中文 | English | 日本語 |
|---|---|---|---|
| `resource.static_limit` | 这组组件需要 {requested} GiB，但按当前桌面预留，AI 最多可用 {limit} GiB。请取消部分组件，或调整桌面预留。 | This set needs {requested} GiB, but the current desktop reserve allows AI to use at most {limit} GiB. Remove a component or adjust the desktop reserve. | この構成には {requested} GiB 必要ですが、現在のデスクトップ予約ではAIが使用できる上限は {limit} GiBです。コンポーネントを減らすか、デスクトップ予約を調整してください。 |
| `resource.realtime_limit` | 此刻只有 {available} GiB 可用。调整桌面预留不会解决这个问题；请关闭占用显存的程序，或选择更小的组件。 | Only {available} GiB is available right now. Changing the desktop reserve will not fix this. Close a program using VRAM or choose a smaller component. | 現在利用できるのは {available} GiBです。デスクトップ予約を変更しても解決しません。VRAMを使用しているアプリを閉じるか、より小さいコンポーネントを選んでください。 |
| `resource.other_device_conflict` | {device} 正在运行“{task}”。切换到“{target}”需要卸载它正在使用的组件。 | {device} is running “{task}”. Switching to “{target}” requires unloading a component it is using. | {device} で「{task}」を実行中です。「{target}」へ切り替えるには、使用中のコンポーネントを解放する必要があります。 |
| `resource.switch_now` | 现在切换 | Switch now | 今すぐ切り替える |
| `resource.wait_until_done` | 等它完成 | Wait until it finishes | 完了まで待つ |
| `resource.use_current` | 使用当前模型继续 | Continue with the current model | 現在のモデルで続ける |
| `resource.read_only_reason` | 这些设置只能在主机电脑上更改。这是系统结构限制，不是权限不足。 | These settings can only be changed on the host PC. This is a system design restriction, not a permission issue. | この設定はホストPCでのみ変更できます。権限不足ではなく、システム構成上の制限です。 |

## 11. 资产生成

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `asset.title` | 资产生成 | Asset generation | アセット生成 |  |
| `asset.description` | 描述你想要的资产 | Describe the asset you want | 作りたいアセットを説明 | 不要求硬核 prompt |
| `asset.reference_image` | 参考图 | Reference image | 参考画像 |  |
| `asset.add_reference` | 添加参考图 | Add reference image | 参考画像を追加 |  |
| `asset.remove_reference` | 移除参考图 | Remove reference image | 参考画像を外す | 只移出任务，不删源文件 |
| `asset.asset_spec` | 资产规格 | Asset spec | アセット仕様 | 内部概念仍写 `Asset Spec` |
| `asset.prompt_translator` | 提示词转译 | Prompt translation | プロンプト変換 |  |
| `asset.model_curator` | 模型管家 | Model curator | モデル管理 |  |
| `asset.style` | 风格 | Style | スタイル |  |
| `asset.purpose` | 用途 | Intended use | 用途 |  |
| `asset.license` | 许可证 | License | ライセンス |  |
| `asset.seed` | 随机种子 | Seed | シード値 | 技术高级项 |
| `asset.variations` | 生成数量 | Number of variations | 生成数 |  |
| `asset.local_generation` | 本地生成 | Generate locally | ローカルで生成 | 不出境 |
| `asset.cloud_generation` | 云端生成 | Generate in the cloud | クラウドで生成 | 需要出境闸 |
| `asset.job_queued` | 任务已排队 | Job queued | タスクをキューに追加しました |  |
| `asset.generating` | 正在生成… | Generating… | 生成しています… |  |
| `asset.quality_check` | 质量检查 | Quality check | 品質チェック | VLM QA |
| `asset.draft` | 草稿 | Draft | 下書き | 三池 `draft` |
| `asset.adopted` | 已采用 | Kept | 採用済み | 三池 `adopted`；用户词不使用生硬 Adopted |
| `asset.exported` | 已导出 | Exported | 書き出し済み | 三池 `exported` |
| `asset.keep_result` | 保留此结果 | Keep this result | この結果を残す | 进入 adopted |
| `asset.open_output_folder` | 打开输出文件夹 | Open output folder | 出力フォルダーを開く |  |
| `asset.3d_future` | 3D 资产生成尚未提供 | 3D asset generation is not available yet | 3Dアセット生成はまだ利用できません | 3D 本期不做 |

## 12. 投资研究

> 本区仅供界面占位和未来兼容。当前决议明确“不做行情管道、回测、风控、IBKR 适配器或下单”。

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `investment.title` | 投资研究 | Investment research | 投資リサーチ | 主导航 |
| `investment.mcp_status` | 投资 MCP 状态 | Investment MCP status | 投資MCPの状態 | 技术状态中才显示 MCP |
| `investment.not_ready` | 投资研究功能尚未启用 | Investment research is not enabled yet | 投資リサーチ機能はまだ有効になっていません | 当前主状态 |
| `investment.reserved_notice` | 当前版本只保留接口，不提供行情、回测或下单。 | This version only reserves the interface. Market data, backtesting, and order placement are not available. | 現在のバージョンではインターフェースのみ予約されています。市場データ、バックテスト、注文機能は利用できません。 | 固定说明 |
| `investment.trade_proposal` | 交易提案 | Trade proposal | 取引提案 | 未来 |
| `investment.paper_trading` | 模拟交易 | Paper trading | ペーパートレード | 未来 |
| `investment.backtest` | 回测 | Backtest | バックテスト | 未来 |
| `investment.risk_controls` | 风控 | Risk controls | リスク管理 | 未来 |
| `investment.order_placement` | 下单 | Place order | 注文を出す | 未来；不能显示为当前可用 |

## 13. 安全确认、出境与状态

| Key | 简体中文 | English | 日本語 | 备注 |
|---|---|---|---|---|
| `approval.confirm_each_time` | 每次都确认 | Confirm every time | 毎回確認 |  |
| `approval.exact_version` | 批准当前这份内容 | Approve this exact version | この内容を承認 | “哈希绑定批准”的用户显示名 |
| `approval.version_changed` | 内容在批准后发生了变化，因此没有执行。请重新检查并批准。 | The content changed after approval, so nothing was executed. Review and approve it again. | 承認後に内容が変更されたため、実行しませんでした。内容を確認して、もう一度承認してください。 | 哈希不一致 |
| `approval.action_preview` | 将要执行的操作 | Actions to be performed | 実行する操作 |  |
| `approval.no_action_taken` | 未执行任何操作 | No action was taken | 操作は実行されませんでした |  |
| `network.access_request` | 此任务需要连接互联网 | This task needs internet access | このタスクにはインターネット接続が必要です |  |
| `network.allow_once_detail` | 仅允许本次任务联网；任务结束后自动失效。 | Allow internet access for this task only. Access ends automatically when the task finishes. | このタスクに限ってインターネット接続を許可します。タスク終了後に自動で無効になります。 | 单任务放行 |
| `network.items_to_send` | 将发送以下内容 | The following items will be sent | 次の内容を送信します |  |
| `network.nothing_from_memory` | 不会发送记忆库内容 | Nothing from memory will be sent | 記憶の内容は送信されません | 仅在出境检查确实得出该结论时显示，不能作为默认安慰文案 |
| `network.search_reserved` | 联网搜索方案尚未确定，当前不可用 | Online search is not available because its data-handling rules have not been finalized | データ取扱ルールが未確定のため、オンライン検索は現在利用できません | D42.5 预留 |
| `status.ready` | 就绪 | Ready | 準備完了 |  |
| `status.in_progress` | 进行中 | In progress | 実行中 |  |
| `status.waiting` | 等待中 | Waiting | 待機中 |  |
| `status.paused` | 已暂停 | Paused | 一時停止 |  |
| `status.completed` | 已完成 | Completed | 完了 |  |
| `status.failed` | 失败 | Failed | 失敗 | 必须附原因与下一步 |
| `status.cancelled` | 已取消 | Cancelled | キャンセル済み |  |
| `status.read_only` | 只读 | Read-only | 読み取り専用 | 必须附原因 |
| `status.unavailable` | 当前不可用 | Currently unavailable | 現在利用できません | 必须附原因 |

## 14. 不允许出现的模糊文案

| 不要写 | 原因 | 应改成 |
|---|---|---|
| 显存不足 | 无法判断撞到静态限制还是实时物理限制 | 使用 `resource.static_limit` 或 `resource.realtime_limit` |
| 权限不足 | 可能实际是端点根本不存在 | 使用 `resource.read_only_reason` |
| 删除 | 无法判断是否可恢复 | “移到回收站”或“永久删除” |
| 模型不可用 | 没有原因和解决办法 | 说明是未安装、需切换、正在使用或资源不足 |
| 已降级 | 没说降成什么 | 显示当前真实模型与能力契约 |
| 正在处理 | 不知道在加载、生成、识别还是等待 | 使用具体进行状态 |
| PPT 文件 | 会误导用户以为输出 `.pptx` | “HTML 幻灯”或“Slides” |
| 记忆体（日语） | 日语中容易被理解为硬件内存 | 使用「記憶」 |
| 常开联网 | 与每任务人工放行决议冲突 | 使用“本次放行 / Allow once / 今回のみ許可” |
| AI 自动批准 | 与带外人工批准冲突 | 明确显示由谁、对什么内容进行确认 |

## 15. 交给实现层的最小约束

- 建议 locale 文件使用本表 Key，采用扁平或嵌套结构均可，但 Key 语义不得改变。
- 不把整句资源错误在客户端拼接；服务端返回结构化原因和数字，客户端用本表模板渲染。
- 不把组件说明、推荐组合说明或安全提示交给模型临时生成；它们是随代码版本管理的静态文案。
- UI 测试至少覆盖：
  - 三种语言均无缺失 Key；
  - 占位符集合在三种语言中完全一致；
  - 按钮短文案不溢出；
  - 日语不用「メモリ」指代个人记忆；
  - 可恢复操作不出现“永久删除”；
  - 所有 `unavailable`、`read_only`、`failed` 状态都带原因；
  - 当前阶段未实现的项目不会因术语存在而变成可点击功能。

## 16. 阶段边界速查

| 内容 | 术语可用性 | 实现含义 |
|---|---|---|
| 六个主导航名称 | 已定 | 可作为 P3c 信息架构词汇 |
| 翻译两种模式 | 已定 | P3c 客户端功能 |
| 配对与连接 | 已定方向 | 具体实现仍以 P3b 最终采纳决议为准 |
| 当前组件显示与手动选择 | 已定 | P3c 先留入口，完整仲裁属于 P4 |
| 自动跟随界面切换组件 | 未来 | P4，不因本表提前实现 |
| 电脑引导模式 | 已定 | P6 先交付 |
| 电脑直接模式与紧急停止 | 已定但有硬前置 | P6 权限区域与急停就绪后才开放 |
| HTML 幻灯生成与预览 | 已定 | 可先生成并由用户另存 |
| AI 直接写课程文件 | 有硬前置 | P6 文件区与精确版本批准就绪后 |
| 资产工作室 | 后续 | P9 |
| 3D 资产生成 | 未来 | 当前明确不做 |
| 投资研究 | 仅预留 | 当前不得实现行情、回测、风控、IBKR 或下单 |
| 联网搜索 | 待定 | 当前只保留不可用提示，不实现 |
