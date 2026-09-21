# TestFramework

TestFramework 是一个基于 .NET 的自动化测试框架核心。它用 YAML 描述测试序列，通过插件扩展测试步骤与运行时资源，并把序列的加载、校验、执行和结果模型作为库交付给宿主程序。

**框架核心不含界面。** 测试界面属于宿主：它有自己的发布节奏、自己的操作人员，也有自己的布局主张。参考宿主是 `BMS-TEST` 仓库（本仓库的同级目录），它把序列编辑与测试执行拆成两个独立可执行程序，并以 NuGet 包方式引用本仓库的四个库。

## 功能概览

- YAML 序列文件加载与保存，往返保持标量类型不变
- 序列校验：插件版本、资源注册、判定来源、枚举取值、超时格式
- 树形测试项与测试步模型，支持 `init` / `main` / `cleanup` 三段
- 测试项支持 Pass/Fail、数值上下限、字符串精确/正则判定
- 数值判定支持常用单位自动换算
- 变量系统，支持 `${name}` 参数引用和步骤输出写入变量
- 插件化测试步骤与运行时资源（仪器 / 传输 / 服务）接口
- 序列运行器：超时、取消、清理宽限、插件隔离、资源生命周期
- 内置基础步骤插件，按插件方式构建与交付

## 项目结构

- `TestFramework.Abstractions`：序列模型、执行结果模型、插件契约。
- `TestFramework.Core`：插件注册、插件加载、序列运行器、变量解析、运行时资源容器。
- `TestFramework.SequenceYaml`：YAML 读写和序列校验。
- `TestFramework.Plugin.Abstractions.UI`：测试步骤设置编辑器 UI 契约。
- `TestFramework.Plugins.BasicSteps`：基础测试步骤插件，按插件方式构建，不编译进任何宿主。
- `TestFramework.Plugins.BasicSteps.UI`：上述插件的设置界面。
- `TestFramework.Tests`：核心逻辑单元测试。

## 环境要求

- .NET SDK 10.0.300 功能带或更高的 .NET 10 功能带（以 `global.json` 为准）
- 无桌面环境要求：本仓库全部是库、插件和无头测试项目，Windows 与 Linux 均可完整构建与测试

## 构建与测试

```powershell
dotnet build TestFramework.sln
dotnet test TestFramework.sln
```

## 交付物

`dotnet build` 与 `dotnet pack` 会产出两类交付物：

| 交付物 | 位置 | 引用方式 |
|---|---|---|
| `TestFramework.Abstractions` | `artifacts/*.nupkg` | 插件仓库 `PackageReference` |
| `TestFramework.Plugin.Abstractions.UI` | `artifacts/*.nupkg` | 提供配置界面的插件仓库 `PackageReference` |
| `TestFramework.Core` | `artifacts/*.nupkg` | 宿主 `PackageReference` |
| `TestFramework.SequenceYaml` | `artifacts/*.nupkg` | 宿主 `PackageReference` |
| 内置步骤插件 DLL | `artifacts/plugins/BasicSteps/` | 宿主按文件复制到自己的 `Plugins/` 目录 |

内置插件不发包：宿主对插件不能有任何编译期依赖，也就没有可引用的东西。`dotnet build` 会把这两个 DLL 暂存到 `artifacts/plugins/BasicSteps`，发布流水线把同一个目录打成 zip 随 release 发出，宿主从这里取。

宿主如何消费这些交付物，见 `BMS-TEST` 仓库的 `nuget.config`、`Directory.Build.props` 与 `tools/sync-framework.ps1`。

## 测试序列文件

宿主约定把序列文件放在 `config/sequence/*.yaml`（或 `*.yml`）。示例见 [Samples/sample-sequence.yaml](Samples/sample-sequence.yaml)。

`TestSequenceYamlService` 会拒绝包含未知字段、注释、锚点或别名的 YAML，避免打开后保存时静默丢失这些内容。
同样会拒绝重复键（避免后值静默覆盖前值）和嵌套超过 64 层的文档（解析深层嵌套会直接导致进程栈溢出，无法被捕获）。
`"null"`、`"~"` 等会被 YAML 解析成空值的字符串在保存时会自动加引号，重新加载后仍是原字符串。
目前仅支持 `schemaVersion: 1`；超时必须为正整数或留空。未定义变量、无效数值参数和缺失判定输出会报告错误，不会回退为通过。

`variables`、`parameters` 和 `settings` 下的标量按 YAML 类型加载：未加引号的 `5.0` 是 double，`7` 是 int，`true` 是 bool，加引号的是字符串。保存时会给"读回来会变成别的类型"的字符串加引号（`"007"`、`"true"`），并把整数值的 double 写成带小数（`5.0`），因此值在往返中类型不变。

## 运行语义

- 步骤超时或取消后，如果插件没有及时退出，序列会中止并保留运行资源，待后台插件退出后再释放。宿主必须先 `await TestSequenceRunner.PendingStepsCompletion` 再释放资源或复用插件实例，此期间不能再次运行。
- 该闸门是 runner 实例级的，因此**宿主应复用同一个 `TestSequenceRunner`**，用 `RunAsync(sequence, resources, cancellationToken)` 每次传入本次运行的资源作用域。用构造函数传资源意味着作用域一变就得新建 runner（工位作用域每次运行都变），而新 runner 不知道上一个 runner 隔离掉的插件，会从注册表取到同一个插件单例、在它还在驱动硬件时再次调用 `ExecuteAsync`。资源跨运行不变的宿主仍可用构造函数参数。
- 取消后仍会执行当前测试项的 `cleanup` 步骤，避免被测设备停留在中间状态；该清理有 30 秒宽限时间，超时则放弃并照常记为已取消。若此前已有插件被隔离（未及时退出），则跳过清理。
- 进程内插件无法被安全强杀，插件应响应 `CancellationToken` 并为设备操作设置自身超时。
- 资源清理失败会记录到运行结果，宿主应据此停止接受新的运行：此时设备状态未知，下一次运行会对着它测量。

### 数值判定的单位

判定在哪个量纲上比较，由 `unit` 与 `sourceUnit` 两个字段决定：

- `unit` 是**限值所用的单位**，也就是换算目标。留空表示不要求换算，限值按测量值本身的单位理解——这是受支持的用法，不是遗漏。
- `sourceUnit` 是测量值的单位，**仅在测量值自己不带单位时**生效。
- 测量值自带单位（如 `"1234 mV"`）时以它为准，覆盖 `sourceUnit`：仪器比序列文件更清楚自己返回了什么。
- `unit` 已声明且与测量值单位不同时，两端都必须是框架认识的单位且量纲一致。单位不认识、或 `mV` 对 `ms`，判 `Inconclusive`，绝不退回裸数比较——在错误的量纲上静默比较，正是这套换算要防的事。测量值根本不是数值时同理。

`Ω` 归一为 `Ohm`；`µ`（U+00B5）与 `μ`（U+03BC）都归一为 `u`。

## 插件说明

测试步骤通过 `ITestStepPlugin` 扩展。运行时插件应保持 UI 无关；需要自定义设置界面时，单独提供实现 `IStepSettingsEditorPlugin` 的 `MyPlugin.UI.dll`。

### 插件生命周期

- 插件程序集加载后**无法卸载**：更新或替换插件 DLL 必须重启宿主，运行期间该文件也处于占用状态。
- 插件版本**精确优先、同主版本向前兼容**：步骤写 `pluginVersion: 1.0.0` 时，装有 1.0.0 就用 1.0.0；没有则取已装的最高 `1.x.y`，并报告实际使用的版本。主版本不同或只有更低版本一律报错——两者都可能改变序列依赖的行为，静默测错比拒绝运行更糟。校验、运行、设置编辑器三处共用同一条规则（`PluginVersionPolicy`），配置界面不会再因版本不符而静默消失。
- `TestFramework.Abstractions`、`TestFramework.Plugin.Abstractions.UI` 与 `Avalonia.*` 由宿主共享，插件目录中的同名副本会被忽略，插件必须针对宿主的这些程序集版本编译；其余依赖在插件自身的加载上下文中解析，同目录被共同依赖的文件全进程只加载一份。
- 加载失败按程序集和类型分别隔离：单个损坏的插件类型不会导致同一程序集内其它插件丢失，失败信息会记录到加载报告而不是抛出。目录中的原生 DLL 和不含插件类型的程序集会被静默跳过。
- 整个插件目录**只扫描一次**，步骤插件与资源插件共用同一遍加载；按类别各扫一遍会重复解析每个 DLL，还会把"只含资源插件"的程序集误判为空而卸载后重新加载。
- **资源插件契约只读**：`IInstrumentDriverPlugin` 等在创建资源时拿到的是 `IResourceScope`，只能查询已创建的仪器/传输/服务，不能注册或释放宿主容器——容器由宿主持有，供整个运行期所有资源共享。
- **信任边界**：插件加载不做签名或哈希校验，插件代码以宿主权限在同一进程内运行。因此对插件目录的写权限等同于以当前用户身份执行代码，部署时需要相应保护该目录。只读契约防的是误用，不是沙箱。

### 框架契约版本

- 插件程序集用 `[assembly: TestFrameworkPlugin("1.0")]` 声明所需的**最低框架契约版本**。宿主在实例化任何类型之前读取该声明，版本高于宿主能提供的直接拒绝整个程序集并给出明确原因，插件代码一行都不会执行。未声明的按基线 1.0 处理。
- 契约版本与包版本无关：包按自己的节奏发布，契约版本只在插件所面对的接口变化时才动。
- 框架承诺**高版本兼容低版本插件**：已有插件接口新增成员必须带默认实现，已有成员签名与语义永不改变；做不到的变更即为新的主契约版本。

插件开发详见 [docs/plugin-development.md](docs/plugin-development.md)，更多架构说明见 [ARCHITECTURE.md](ARCHITECTURE.md)。
