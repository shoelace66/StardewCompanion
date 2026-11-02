# 贡献与验证

请从最新 `main` 创建分支，并在 PR 中说明具体问题、修改后的行为、实际运行的检查和未验证范围。不要提交 `.local/`、`.tools/`、API 密钥、玩家存档或未经脱敏的运行日志。仓库的许可状态见 [README](README.md#许可与贡献)。

## 无需安装游戏的检查

在 Windows 安装 [global.json](global.json) 指定的 SDK 后，从仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-core-checks.ps1
```

脚本也接受 `-DotnetPath` 指定已有 SDK 的 `dotnet.exe`。它运行工具契约、Agent 行为、权限与记忆、上下文、分组短计划、事件反馈、季节工具及算法检查，使用本地假模型和假游戏桥接。默认不访问真实模型 API。CI 在 `windows-latest` 上运行相同检查，并构建 WPF 桌面端和诊断服务。

增加或修改工具时，请同步模型声明、参数校验、父分组和执行入口，并在 `tools/ToolChecks` 或 `tools/CoreChecks` 添加能复现问题的回归。分组计划只能包含同组技能；`execute_plan` 已不向模型提供。参数非法时应返回可修正的工具拒绝结果，且不能先写入计划或派发动作。

## 完整工程与游戏验收

具有本地游戏和 SMAPI 程序集时，按 README 引导本地工具，并运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/run-checks.ps1 `
  -GamePath 'D:\SteamLibrary\steamapps\common\Stardew Valley'
```

该命令构建模组和开发夹具，执行桥接/寻路检查，再运行核心与工具检查。执行前需退出 SMAPI AI 客户端并释放诊断端口 `27841`。不要向仓库或 CI 上传游戏程序集。

编译、契约检查和假桥接回归不能证明工具的原生成功路径，也不能替代联机、换日或连续无人干预验收。涉及这些行为的 PR 请分别列出自动检查和实际游戏证据；缺少游戏环境时明确标注未验证，不提升能力的实机验证状态。
