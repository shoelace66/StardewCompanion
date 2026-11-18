# StardewCompanion

[![Core and tool checks](https://github.com/shoelace66/StardewCompanion/actions/workflows/core-checks.yml/badge.svg?branch=main)](https://github.com/shoelace66/StardewCompanion/actions/workflows/core-checks.yml)
[![Application lifecycle checks](https://github.com/shoelace66/StardewCompanion/actions/workflows/app-checks.yml/badge.svg?branch=main)](https://github.com/shoelace66/StardewCompanion/actions/workflows/app-checks.yml)

StardewCompanion 是《星露谷物语》的 Windows AI 陪玩工具。你用原版客户端主持农场，AI 以独立农场手从 SMAPI 客户端加入。桌面端把玩家目标和当前游戏观察发给模型，模组通过原版移动、劳动和菜单交互执行获准的动作，并返回实际结果。

项目处于开发预览阶段。指定存档已有基础联机、切图、浇水、收获、工作箱存取和正常换日的实机记录。商店、建筑、动物照料和工具升级等经营能力的可用性以运行时工具目录为准，完整季节运行和更多联机场景有待实机验收。 [基础实机证据](docs/M0-EVIDENCE.md)

## 玩家快速开始

验证环境为 Windows x64、Stardew Valley 1.6.15 和 SMAPI 4.5.2。需要一个支持工具调用的 OpenAI 兼容 Chat Completions 服务。

1. 启动桌面端，在「设置与工作箱」保存游戏目录、完整 API 地址、模型名称与密钥，再测试模型连通和工具调用。
2. 点击「打开原版房主」，在游戏内主持联机农场。点击「打开 AI 客户端」，在该 SMAPI 窗口手动加入房间。使用预览包时，将桌面程序与包内 `Mods` 目录一同解压。
3. 连接后确认 AI 角色与存档，在「LLM 交互调试 → 长期目标」保存目标和限制，再点击「继续」。聊天用于追加当下的指令。缺少当前季计划时，Agent 会先保存季计划。
4. 需要委托箱子存取时，先让 AI 到达箱子所在地图，再绑定箱子名称、位置、允许物品与保护物品。保护列表优先。

连接或重连后默认暂停。「仅规划」用于查看和调整计划；「自主执行」允许按当前授权派发行动。「暂停」停止 Agent 的后续操作，联机世界继续运行。 [玩家指南](docs/玩家快速开始.md)

## 从源码构建

在仓库根目录运行 PowerShell，替换示例游戏目录。源码构建使用 [global.json](global.json) 指定的 .NET SDK 10.0.403，模组构建从游戏安装目录读取游戏及 SMAPI 程序集。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/bootstrap.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Stardew Valley'
powershell -NoProfile -ExecutionPolicy Bypass -File tools/start-desktop.ps1
```

开发启动器使用 `.local` 保存设置和日志，AI 模组由构建脚本放到 `.local/ai-mods`。维护者可用 `tools/publish-windows.ps1 -GamePath <游戏目录>` 构建 Windows x64 自包含预览包，产物写入 `artifacts`。

## 自主决策与 Token 开销

- 桌面端每次发送玩家目标、季计划、当前观察和必要的近期回执。季计划只出现一次，目标和计划的固定字段排在动态状态之前，为供应商前缀缓存提供稳定输入。
- 日常工具目录按当前工作、观察和能力精简。模型可调用 `list_capabilities` 为下一次请求展开目录。请求日志记录工具数量、Schema 字符数和模型用量。
- 模型可一次提出最多八步短计划。Core 逐步验证参数、权限和新鲜观察，等待每一步的真实终态。有步骤失败或部分完成时停止剩余步骤。至少两步且运行超过 3 秒的批次才发起后台预规划；前置批次结束后可立即开始下一次决策。
- 模型依据回执和新观察更新工作。接单、计划、下单、出货入箱与实际完工或结算是不同状态。

Core 与模组间使用带本机会话认证的回环连接。工作记忆按存档和角色隔离，模型密钥由 Windows DPAPI 加密保存。配置的模型服务会收到当前决策所需的游戏观察与对话。分享日志前请移除会话令牌、密钥、存档和私人对话。 [输入消息架构](docs/输入消息架构.md) · [决策流监控](docs/决策流监控.md)

## 隔离春季测试

通过源码启动器打开桌面端后，点击「一键春季测试」。它使用隔离用户配置和测试模组创建 Standard 春 1 新档，读取测试计划中的初始目标，并尝试运行到春 28 夜结算。夏 1 首个可操作早晨自动暂停，记录现金、NPC 净好感、农牧场经营和已取回的工具升级。开局宠物可由 AI 通过原版命名菜单提交名称，失败时界面会提示手填。

日志、配置摘要和原版检查点写入 `.local/solo5/<runId>`。上一轮在春 10 中止，该轮数据用于复现和比较开销。自动命名、导航、卖货修复和缓存效果待实机复测。 [测试计划与记录口径](docs/春季全流程自主经营测试计划.md)

## 验证

无需游戏文件或真实模型密钥即可运行 Core、工具契约和算法检查。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-core-checks.ps1
```

GitHub Actions 分别运行 Core 与工具检查、应用生命周期检查。完整工程检查使用 `tools/run-checks.ps1 -GamePath <游戏目录>`，需要本机游戏程序集及空闲的回环端口。自动化结果标明代码检查状态，实机能力按原版状态与回执逐项验收。 [贡献与验证](CONTRIBUTING.md) · [实施状态](IMPLEMENTATION_STATUS.md)

## 项目目录

| 路径 | 职责 |
| --- | --- |
| [Desktop](src/StardewCompanion.Desktop) | WPF 界面、游戏启动、长期目标、调试与隔离春季测试 |
| [Core](src/StardewCompanion.Core) | 模型请求、上下文、调度、权限、SQLite 记忆和回执 |
| [Mod](src/StardewCompanion.Mod) | SMAPI 游戏观察、原版行动、寻路和结果核验 |
| [Protocol](src/StardewCompanion.Protocol) | 游戏桥接消息、能力定义与共享参数契约 |
| [Diagnostics](src/StardewCompanion.Diagnostics) | 独立运行的回环调试服务 |

提交问题时附上游戏、SMAPI 与构建版本、复现步骤和脱敏后的回执。仓库目前没有根目录许可证文件，使用或再分发前请向维护者确认授权。
