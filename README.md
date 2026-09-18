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
- `TestFramework.Plugins.BasicSteps`：基础测试步骤插件，按插件方式构建，不编译进任何宿主。
- `TestFramework.Plugins.BasicSteps.UI`：上述插件的设置界面。
- `TestFramework.App`：Avalonia 桌面序列编辑器和运行入口。
- `TestFramework.Tests`：核心逻辑单元测试。
- `TestFramework.App.Tests`：桌面编辑器与运行流程的界面级测试。

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
- `删除` 会在确认后永久删除当前序列对应的文件（不进入回收站，且不可撤销）。
- `运行` 会执行当前序列，并将完整 JSON 结果写入程序运行目录的 `results` 文件夹。
- 运行时点击 `停止` 或按 F5 会请求取消，已完成步骤及中断状态仍会保存到 JSON。
- 取消后仍会执行当前测试项的 `cleanup` 步骤，避免被测设备停留在中间状态；该清理有 30 秒宽限时间，超时则放弃并照常记为已取消。若此前已有插件被隔离（未及时退出），则跳过清理。
- 每个序列分别记录未保存修改；关闭窗口时可选择保存全部、放弃修改或取消关闭。保存失败时窗口保持打开。

编辑器会拒绝包含未知字段、注释、锚点或别名的 YAML，避免打开后保存时静默丢失这些内容。
同样会拒绝重复键（避免后值静默覆盖前值）和嵌套超过 64 层的文档（解析深层嵌套会直接导致进程栈溢出，无法被捕获）。
`"null"`、`"~"` 等会被 YAML 解析成空值的字符串在保存时会自动加引号，重新加载后仍是原字符串。
目前仅支持 `schemaVersion: 1`；超时必须为正整数或留空。未定义变量、无效数值参数和缺失判定输出会报告错误，不会回退为通过。

步骤超时或取消后，如果插件没有及时退出，序列会中止并保留运行资源，待后台插件退出后再释放；此期间不能再次运行或正常关闭窗口。进程内插件无法被安全强杀，插件应响应 `CancellationToken` 并为设备操作设置自身超时。资源清理失败会记录到结果，并阻止后续运行，需检查设备后重启应用。

## 插件说明

测试步骤通过 `ITestStepPlugin` 扩展。运行时插件应保持 UI 无关；如果插件需要自定义设置界面，可以在 Avalonia 应用中通过 `ITestStepSettingsEditorProvider` 注册编辑器。

### 插件生命周期

- 插件程序集加载后**无法卸载**：更新或替换插件 DLL 必须重启应用，运行期间该文件也处于占用状态。
- 插件版本**精确优先、同主版本向前兼容**：步骤写 `pluginVersion: 1.0.0` 时，装有 1.0.0 就用 1.0.0；没有则取已装的最高 `1.x.y`，并在编辑器校验中给出警告、在运行日志中记录实际使用的版本。主版本不同或只有更低版本一律报错——两者都可能改变序列依赖的行为，静默测错比拒绝运行更糟。校验、运行、设置编辑器三处共用同一条规则，配置界面不会再因版本不符而静默消失。
- `TestFramework.Abstractions`、`TestFramework.Plugin.Abstractions.UI` 与 `Avalonia.*` 由宿主共享，插件目录中的同名副本会被忽略，插件必须针对宿主的这些程序集版本编译；其余依赖在插件自身的加载上下文中解析，同目录被共同依赖的文件全进程只加载一份。
- 加载失败按程序集和类型分别隔离：单个损坏的插件类型不会导致同一程序集内其它插件丢失，失败信息会记录到加载报告而不是抛出。目录中的原生 DLL 和不含插件类型的程序集会被静默跳过。
- 整个插件目录**只扫描一次**，步骤插件与资源插件共用同一遍加载；按类别各扫一遍会重复解析每个 DLL，还会把"只含资源插件"的程序集误判为空而卸载后重新加载。
- **资源插件契约只读**：`IInstrumentDriverPlugin` 等在创建资源时拿到的是 `IResourceScope`，只能查询已创建的仪器/传输/服务，不能注册或释放宿主容器——容器由宿主持有，供整个运行期所有资源共享。
- **信任边界**：插件加载不做签名或哈希校验，插件代码以宿主权限在同一进程内运行。因此对插件目录的写权限等同于以当前用户身份执行代码，部署时需要相应保护该目录。只读契约防的是误用，不是沙箱。

### 框架契约版本

- 插件程序集用 `[assembly: TestFrameworkPlugin("1.0")]` 声明所需的**最低框架契约版本**。宿主在实例化任何类型之前读取该声明，版本高于宿主能提供的直接拒绝整个程序集并给出明确原因，插件代码一行都不会执行。未声明的按基线 1.0 处理。
- 契约版本与包版本无关：包按自己的节奏发布，契约版本只在插件所面对的接口变化时才动。
- 框架承诺**高版本兼容低版本插件**：已有插件接口新增成员必须带默认实现，已有成员签名与语义永不改变；做不到的变更即为新的主契约版本。

### 交付与解耦

- 框架发布 4 个 NuGet 包：`Abstractions`（插件契约）、`Plugin.Abstractions.UI`（界面契约）、`Core`（运行器与加载器）、`SequenceYaml`（序列文件与校验）。宿主引用 `Core` 与 `SequenceYaml`，插件仓库只引用 `Abstractions`（提供配置界面时再加 `Plugin.Abstractions.UI`）。
- 插件本身不发包，交付物是投放到宿主 `Plugins/` 目录的 DLL；宿主对插件没有任何编译期依赖。`TestFramework.App` 对内置插件使用 `ReferenceOutputAssembly="false"`，只保证先构建再复制到 `Plugins/BasicSteps/`，App 内无法引用插件类型 —— 内置步骤因此走的是与第三方插件完全相同的加载路径。
- 插件的设置界面单独成一个程序集（`MyPlugin.UI.dll`），运行时插件保持无 Avalonia 依赖，无头/CI 宿主只需投放运行时 DLL。

插件开发详见 [docs/plugin-development.md](docs/plugin-development.md)，更多架构说明见 [ARCHITECTURE.md](ARCHITECTURE.md)。
