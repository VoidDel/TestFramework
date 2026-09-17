# TestFramework

TestFramework 是一个基于 .NET 和 Avalonia 的测试序列编辑与执行框架。它用于用 YAML 描述测试序列，通过插件扩展测试步骤，并在桌面应用中编辑、保存和运行测试流程。

## 功能概览

- 可视化测试序列编辑器
- 树形测试项与测试步管理
- 测试项支持 Pass/Fail、数值上下限、字符串精确/正则判定
- 数值判定支持常用单位自动换算
- YAML 序列文件加载与保存
- 变量系统，支持 `${name}` 参数引用和步骤输出写入变量
- 插件化测试步骤和运行时资源接口
- 内置基础步骤插件和单元测试

## 项目结构

- `TestFramework.Abstractions`：序列模型、执行结果模型、插件契约。
- `TestFramework.Core`：插件注册、插件加载、序列运行器、变量解析。
- `TestFramework.SequenceYaml`：YAML 读写和序列校验。
- `TestFramework.Plugin.Abstractions.UI`：测试步骤设置编辑器 UI 契约。
- `TestFramework.Plugins.BasicSteps`：内置基础测试步骤插件。
- `TestFramework.App`：Avalonia 桌面序列编辑器和运行入口。
- `TestFramework.Tests`：核心逻辑单元测试。

## 环境要求

- .NET SDK 10.0.300 功能带或更高的 .NET 10 功能带（以 `global.json` 为准）
- Windows 桌面环境（用于运行 Avalonia 桌面应用）

## 构建与测试

```powershell
dotnet build TestFramework.sln
dotnet test TestFramework.sln
```

## 运行应用

```powershell
dotnet run --project TestFramework.App\TestFramework.App.csproj
```

## 测试序列文件

桌面编辑器会从程序运行目录下的 `config/sequence` 文件夹识别测试序列文件：

```text
config/sequence/*.yaml
config/sequence/*.yml
```

在编辑器中：

- `新建` 会创建一个未保存的新测试序列。
- `保存` 会将当前序列保存到 `config/sequence`。
- `另存` 会在 `config/sequence` 中生成当前序列的副本。
- `删除` 会删除当前序列对应的文件。
- `运行` 会执行当前序列，并将完整 JSON 结果写入程序运行目录的 `results` 文件夹。
- 运行时点击 `停止` 或按 F5 会请求取消，已完成步骤及中断状态仍会保存到 JSON。
- 每个序列分别记录未保存修改；关闭窗口时可选择保存全部、放弃修改或取消关闭。保存失败时窗口保持打开。

编辑器会拒绝包含未知字段、注释、锚点或别名的 YAML，避免打开后保存时静默丢失这些内容。
目前仅支持 `schemaVersion: 1`；超时必须为正整数或留空。未定义变量、无效数值参数和缺失判定输出会报告错误，不会回退为通过。

步骤超时或取消后，如果插件没有及时退出，序列会中止并保留运行资源，待后台插件退出后再释放；此期间不能再次运行或正常关闭窗口。进程内插件无法被安全强杀，插件应响应 `CancellationToken` 并为设备操作设置自身超时。资源清理失败会记录到结果，并阻止后续运行，需检查设备后重启应用。

## 插件说明

测试步骤通过 `ITestStepPlugin` 扩展。运行时插件应保持 UI 无关；如果插件需要自定义设置界面，可以在 Avalonia 应用中通过 `ITestStepSettingsEditorProvider` 注册编辑器。

更多架构说明见 [ARCHITECTURE.md](ARCHITECTURE.md)。
