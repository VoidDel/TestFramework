# TestFramework

TestFramework 是一个基于 .NET 的自动化测试框架核心。它用 YAML 描述测试序列，通过插件扩展测试步骤与运行时资源，并把序列的加载、校验、执行和结果模型作为库交付给宿主程序。

**框架核心不含界面。** 测试界面属于宿主：它有自己的发布节奏、自己的操作人员，也有自己的布局主张。参考宿主是 `BMS-TEST` 仓库（本仓库的同级目录），它把序列编辑与测试执行拆成两个独立可执行程序，并以 NuGet 包方式引用本仓库的四个库。

## 功能概览

- YAML 序列文件加载与保存，往返保持标量类型不变；不用新特性的文件保存后逐字节不变
- 序列校验：插件版本、资源注册、判定来源、枚举取值、超时格式、表达式与流程定义
- 树形测试项：测试项可分组嵌套，每个测试项有 `init` / `main` / `cleanup` 三段步骤，也可调用另一份序列
- 流程控制：条件执行（`runIf`）、失败重试与轮询（`retry` / `until`）、循环（`loop`）、分组、子序列调用
- 表达式：四则运算、比较、逻辑运算与 `min` / `max` / `avg` 等函数，用于条件、循环次数和计算
- 判定：Pass/Fail、TestStand 风格的数值比较（`GELE` / `GT` / `EQ` …）、字符串精确/正则；一个测试项可有多个判定点；列表与字典输出逐元素判定；限值可引用变量
- 数值判定支持常用单位自动换算
- 变量系统，支持 `${name}` 参数引用和步骤输出写入变量
- 插件化测试步骤与运行时资源（仪器 / 传输 / 服务）接口，操作员交互契约
- 序列运行器：超时、取消、清理宽限、插件隔离、资源生命周期；多工位并行时自动为非线程安全插件排队
- 工位级运行编排（`TestRunSession`）：出错后重建工位资源、插件未退出时拒绝下一次运行、清理失败后停机
- 结果追溯：被测设备序列号、操作员、工位、序列版本与文件哈希、框架版本、实际运行的插件版本、每个测量值及其限值
- 带版本号的结果 JSON 格式（`TestResultJson`），可写可读
- 内置基础步骤插件（延时、日志、限值检查、计算、操作员确认等），按插件方式构建与交付

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

这三个块的键**大小写不敏感**，因此其中出现仅大小写不同的两个键会被拒绝——合并等于让文件顺序决定哪个值存活。但它们的**值内部**的字典是插件自己的数据（CAN 信号表、解码出的帧、JSON 载荷），大小写敏感原样保留：那里 `SOC` 和 `soc` 是两个信号。

## 变量引用

- `${name}` 独占整个值时保留变量原本的类型；嵌在文本里则转为 invariant 字符串。
- `$${` 表示字面量 `${`：`$${targetVoltage}` 传给插件的就是 `${targetVoltage}` 这串字符。转义仅在 `$$` **紧邻左花括号**时生效，其它位置的 `$$` 原样保留。
- 含有既不是引用也不是转义的 `${`（`${1stReading}`、`${my var}`、少个括号）会报**警告**：解析器原样放过，插件收到的是那串字符，而这几种形态恰恰是打错变量名时产生的。
- 引用**不会**关掉参数的类型检查。变量的值在运行前可知时（值写在文件里、且没有任何 `variableWrites` 写它），按声明的类型检查，和字面量一视同仁。只查类型不查范围——范围属于运行产生的值，初始值不是。

## 表达式

条件、循环次数、轮询条件和 `basic.calculate` 用同一种表达式，变量照样写 `${name}`：

```text
${soc} >= 95 && ${mode} == 'charge'
(max(${cellVoltages}) - min(${cellVoltages})) * 1000
round(avg(${temps}), 1)
${cells}[0]
```

- 运算：`+ - * / %`、`== != < <= > >=`、`&& || !`、括号、列表下标 `[i]`（从 0 开始）。文字写 `'…'` 或 `"…"`，两个引号连写表示引号本身。
- 函数：`abs`、`round(x[, 位数])`、`min`、`max`、`sum`、`avg`、`count`；后五个接受一个列表变量，也接受多个参数。
- **严格**：条件必须得出 `true`/`false`，数字不会被当成"真"；文字和数字比大小、除以零、变量未定义，一律报错而不是猜。运行时报错的表达式让所在步骤或测试项判 `Error`。数字一律按 double 计算；`"007" == 7` 为真，因为参数框里输入的数字到达时是文字。
- 校验器解析每个表达式，并检查其中的变量在求值处已定义。
- 写在 YAML 里时，以 `!`、`'`、`"` 开头，或含 `: `、` #` 的表达式要整体加引号，否则 YAML 自己会先把它解析掉；框架保存时会自动处理。

## 流程控制

| 写在哪 | 字段 | 作用 |
|---|---|---|
| 步骤、测试项 | `runIf` | 条件为假则记为 `Skipped`，并在 `SkipReason` 里写明原因 |
| 步骤 | `retry` | 判 `Error` 或 `Fail` 时重新执行，或直到 `until` 条件成立（轮询） |
| 测试项 | `retry` | 判 `Fail` 时整个测试项（init/main/cleanup）重跑 |
| 测试项 | `loop` | 按次数执行，下标放在变量里 |
| 测试项 | `items` | 分组：子测试项依次执行，分组结论取子项结论 |
| 测试项 | `call` | 调用另一份序列，作为子项执行 |

```yaml
- id: charge
  name: 充电
  main:
  - id: poll-soc
    name: 读取 SOC
    pluginId: bms.read-soc
    retry:
      maxAttempts: 600
      intervalMs: 1000
      until: ${soc} >= 95
    variableWrites:
    - name: soc
      outputKey: soc
- id: channels
  name: 通道检查
  loop:
    count: ${channelCount}
    variable: channel
  main:
  - id: measure-channel
    name: 测量通道
    pluginId: bms.measure-channel
    parameters:
      Channel: ${channel}
- id: insulation
  name: 绝缘检查
  call:
    path: shared/insulation.yaml
    parameters:
      testVoltage: ${packVoltage}
```

循环的测试项每次迭代产生一条结果，带 `Iteration` 下标。完整可运行的示例（只用内置步骤）见 [Samples/flow-sequence.yaml](Samples/flow-sequence.yaml)。

- **重试只针对能安全重来的情况。** 步骤重试 `Error` 和 `Fail`（CAN 请求超时、电源没稳就读了）；测试项只重试 `Fail`——测试项判 `Error` 说明有步骤做到一半，整项重跑会把故障扩散到硬件上。被隔离的步骤（插件超时未退出）**从不重试**。
- `maxAttempts` 含第一次；`until` 在每次尝试的 `variableWrites` 之后求值，所以能判断这一步刚写入的值。尝试用完 `until` 仍为假，判 `Fail`。
- 失败的尝试保留在结果里：步骤的 `PreviousAttempts` 只记 `Error`/`Fail` 的尝试（轮询中"条件还没满足"的不记，否则十分钟的充电轮询会留下六百条）；测试项的 `PreviousAttempts` 记每次尝试，含当时的测量值。最终结果的 `Attempts` / `Attempt` 是第几次。
- `loop.count` 是表达式，必须得出 0 到 10000 的整数。循环下标在本测试项里可直接用于参数，校验器把它当整数。
- **分组**没有自己的步骤和判定，写了会被校验器拒绝。分组里的测试项和外面共用变量作用域。
- **调用**的子序列在自己的变量作用域里运行：它自己的 `variables` 被 `parameters` 覆盖（参数在调用方作用域求值，可以写 `${}`），它写的变量不会流回调用方。子序列使用调用方的资源，它自己声明的资源不会打开。路径交给宿主提供的 `ISequenceResolver` 解析；`FileSequenceResolver` 按某个目录解析相对路径，拒绝走出该目录的路径。嵌套超过 16 层判 `Error`，序列自己调用自己就以此结束。
- 流程定义本身没法求值（`runIf` 不是布尔值、循环次数不是数字、找不到子序列），**序列停止**：跳过等于默认通过，照跑又违背作者的条件，哪种都不该悄悄发生。
- 测试项 id 在整棵树里唯一。

## 运行语义

- 测试项结论由它的判定点（`verdictSource` 与 `checks`，见"多判定点与多通道测量"）共同决定，但判定点**不能盖过其它步骤**：任一步骤 `Error` 则测试项为 `Error`；否则 `init` / `main` / `cleanup` 中任一步骤为 `Fail`，测试项即为 `Fail`。一个测试项里放两个限值检查（先电压后电流）是很自然的写法，前一个失败不能被后一个的通过掩盖。例外只有被判定输出的步骤自身：判定点按 `outputKey` 判它的输出时，这个判定取代它自己的结论。
- **宿主应通过 `TestRunSession` 运行**（见下文"工位运行编排"），下面几条关于 runner 闸门、资源释放和清理失败的规则由它统一落实；直接使用 `TestSequenceRunner` 的宿主需要自己遵守。
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

### 比较方式

数值判定用 `comparison` 指定怎么比较，取值沿用 TestStand 的代码：

| `comparison` | 通过条件 | 读取 |
|---|---|---|
| `GELE`（默认） | 下限 ≤ x ≤ 上限 | `lowerLimit` / `upperLimit`，可只写一个 |
| `GTLT` | 下限 < x < 上限 | 两个都必须写 |
| `GELT` | 下限 ≤ x < 上限 | 两个都必须写 |
| `GTLE` | 下限 < x ≤ 上限 | 两个都必须写 |
| `GT` / `GE` | x > 下限 / x ≥ 下限 | `lowerLimit` |
| `LT` / `LE` | x < 上限 / x ≤ 上限 | `upperLimit` |
| `EQ` / `NE` | x = 期望值 / x ≠ 期望值 | `expected` |

- 不写 `comparison` 即 `GELE`，行为与引入比较方式之前完全一致，包括只写一个限值的用法。
- 其它比较方式缺了它要读的值，测试项判 `Error`，校验器也会提前报错；写了它不读的值，校验器给警告，运行时忽略。
- `EQ` / `NE` 是**精确**比较，适合电芯数、故障码这类整数。期望值带小数时校验器会警告：测量值几乎不可能恰好等于它，应改用 `GELE` 加容差。
- `lowerLimit` / `upperLimit` / `expected` 都可以写数字或**单个** `${变量}`，按 `unit` 理解。

### 多判定点与多通道测量

一个测试项除 `verdictSource` 外，还可以用 `checks` 列出更多判定点，写法与 `verdictSource` 相同，另可用 `name` 给记录命名。每个判定点判 `main` 中某一步的某个输出，各自有限值、单位和比较方式：

- 所有判定点都参与测试项结论：任一判 `Fail` 即 `Fail`；没有失败、但有判定点没法判（`Inconclusive`）则不判通过。
- 判定点必须写 `outputKey`：它总是判输出，取步骤自身结论是 `verdictSource` 的事。
- 被判定输出的步骤，其自身结论由对输出的判定取代；没有被任何判定点引用的步骤判 `Fail`，照样让测试项 `Fail`（见"运行语义"）。
- 同一测试项里两个判定点记录名相同（都叫 `value`）时校验器警告：报告里分不清两者的记录。

数值判定的输出是**列表或字典**时，每个元素都按同一组限值判定：任一元素不通过即 `Fail`；没有失败但有元素读不成数值则 `Inconclusive`；空列表也是 `Inconclusive`——什么都没测到，不能判通过。记录名取判定点的 `name`（没有则取 `outputKey`）：列表元素记为 `名称[0]`、`名称[1]`……，字典元素用自己的键（通常是信号名）。

限值引用在测试项判定时解析，即本测试项的步骤全部跑完之后，所以步骤写入的限值也能用上。变量未定义、不是数值、上下限颠倒，测试项判 `Error`，`TestItemRunResult.ErrorMessage` 说明原因——这是序列配置错误，不是 DUT 不合格。

以 80 串单体电压为例，一个测试项、一个步骤即可：

```yaml
variables:
  cellMin: 3.0
  cellMax: 3.6
  spreadMax: 10
items:
- id: cell-voltages
  name: 单体电压
  verdictSource:
    name: 单体电压
    stepId: read-cells
    outputKey: cellVoltages
    judgeType: Numeric
    lowerLimit: ${cellMin}
    upperLimit: ${cellMax}
    sourceUnit: mV
    unit: V
  checks:
  - name: 最大压差
    stepId: read-cells
    outputKey: spread
    judgeType: Numeric
    comparison: LE
    upperLimit: ${spreadMax}
    sourceUnit: mV
    unit: mV
  main:
  - id: read-cells
    name: 读取单体电压
    pluginId: bms.read-cell-voltages
```

- `bms.read-cell-voltages` 是示意，由你的 BMS 插件提供：一次读回 80 个值（mV）作为列表或字典输出到 `cellVoltages`，顺带输出最大压差 `spread`（mV）。
- 限值写在 `cellMin` / `cellMax`（V）与 `spreadMax`（mV）里，换产品型号只改变量。
- 序列文件不能带注释（见"测试序列文件"），说明请写在文件之外。

### 结果记录

`TestItemRunResult.Measurements` 逐个记录每个被判定的值：所属判定点（`Check`）、名称、序号、原始值、换算后的数值与单位、比较方式、判定时实际使用的限值或期望值、该值的结论。上例一次运行产生 81 条记录——80 串电压加 1 条压差，哪一串越限一目了然。限值按判定当时的值记录，序列文件之后改了也不影响历史结果。即使测试项已被别的步骤判为 `Fail`，各判定点照样判定并记录。`TestStepResult.PluginId` / `PluginVersion` 记录实际运行的插件与版本——发生向前兼容替换时，记的是实际跑的版本。

不使用 `name`、`comparison`、`expected`、`checks` 的序列，保存后与引入它们之前**逐字节相同**：这些键取默认值时不写入文件。

## 工位运行编排

`TestRunSession` 是一个工位（一台测试台）的运行入口，把"什么时候能跑下一次"这些容易写错的决定收在框架里：

```csharp
var session = new TestRunSession(stepPlugins, resourcePlugins, stationHost,
    new TestRunSessionOptions { SequenceResolver = new FileSequenceResolver(sequenceDir), Operator = myOperatorUi });

var result = await session.RunAsync(sequence, runInfo, observer, cancellationToken);
```

- 整个工位生命期只用一个 runner，所以插件隔离闸门跨运行有效。
- 运行判 `Error` 后把工位作用域标记为失效，下一次运行开始时重建；判 `Fail` 不重连——读数偏低说明不了 CAN 通道有问题。
- 插件超时未退出时，资源保留到它退出为止，期间拒绝下一次运行。
- 资源清理失败后工位进入故障状态（`Fault`），拒绝运行，直到宿主在有人检查过硬件后调用 `ClearFault()`。
- 同一工位上已有运行时，再次调用直接拒绝而不是排队——排队会悄悄开始一台操作员以为被拒绝的 DUT。
- 拒绝时抛 `TestRunRefusedException`，`Reason` 为 `RunInProgress` / `PluginStillRunning` / `Faulted`，宿主据此用自己的语言提示。
- 结果总会返回：取消、出错、资源打不开都得到一份结果，而不是异常——它是"这台 DUT 被要求做了什么"的记录。
- 不传工位时，每次运行按序列内联声明打开、关闭资源。

## 结果追溯与结果文件

宿主把这次运行的身份信息交给 runner，结果原样带着：

```csharp
var runInfo = new TestRunInfo
{
    DutSerialNumber = "BMS-0042",
    Operator = "张三",
    StationId = "EOL-3",
    SequenceFilePath = path,
    SequenceHash = TestSequenceYamlService.ComputeHash(File.ReadAllText(path)),
    Properties = { ["workOrder"] = "WO-17" }
};
```

`TestSequenceRunResult` 记录 `RunInfo`、序列自己的 `version`、`FrameworkVersion`（如 `0.5.0`）与 `FrameworkContractVersion`；每个步骤结果记录 `PluginId`、实际运行的 `PluginVersion` 和序列请求的 `RequestedPluginVersion`；每个测量值记录比较方式和判定时实际使用的限值。版本号说明作者认为是哪一版，哈希说明实际跑的是哪些字节——包括没人记得改版本号的那次修改。

`TestResultJson` 是框架自带的结果文件格式：

```csharp
await using var file = File.Create(resultPath);
await TestResultJson.SerializeAsync(file, result);
var readBack = TestResultJson.Deserialize(File.ReadAllText(resultPath));
```

- 外层是 `{ "format": "testframework.run-result", "schemaVersion": 1, "result": { … } }`，读取时格式或版本不认识直接拒绝，而不是误读。属性名 camelCase，枚举写名字。
- 异常记录为类型名、消息、堆栈和内层异常，读回得到 `RecordedException`（原类型可能根本加载不了）。
- 输出和变量值按 JSON 能表达的形式记录：文字、布尔、数字（读回时整数为 `long`，其余为 `double`）、列表、字典；`NaN` / `Infinity` 写成文字；其它对象写成它的 `ToString()`——宁可丢掉一个值的类型，也不能因为写不出来丢掉整份记录。

## 多工位并行

一个宿主进程可以给每个工位各建一个 `TestRunSession`，同时运行。插件实例是进程内共享的，所以：

- 插件声明 `IsThreadSafe => true` 才会被多个工位同时调用；**默认是 `false`**，框架在整个进程范围内让各工位对它的调用排队。没人考虑过并发的插件也能安全地给多个工位共用，代价只是慢一些。
- 排队等待计入步骤超时；因此超时的步骤会在错误信息里说明"在等另一个运行占用的插件"，而不是把锅甩给插件。
- 被放弃的调用在插件真正返回之前一直占着队列，别的工位不会趁它还在驱动硬件时闯进去。
- 内置步骤插件都不保存状态，均声明为线程安全。
- 资源插件的 `CreateAsync` 在进程内按插件实例排队：两个工位同时上电时，同一个驱动不会被并发初始化。厂商 SDK 在这一点上常不可靠，而打开资源本来就不频繁。

### 多工位共用一台仪器

一台万用表接矩阵开关供四个工位、一台多路电源每个工位一路——这类仪器在进程里只能打开一次。为此在工位之上多一层**产线资源**（`LineResourceHost`），用和工位配置同样的格式描述：

```csharp
var line = new LineResourceHost(lineConfig, resourcePlugins);     // 整条产线一个
var stationA = new StationResourceHost(stationAConfig, resourcePlugins, line);
var stationB = new StationResourceHost(stationBConfig, resourcePlugins, line);
```

- 工位作用域嵌套在产线作用域之下。步骤查 `dmm` 时依次找运行级、工位级、产线级，所以序列直接 `requires: [dmm]` 即可，校验器传入产线配置后也认它。
- **独占**：共用仪器的步骤写 `exclusive: [dmm]`，运行器在执行前拿到它的独占权、插件返回后释放。别的工位的同类步骤排队等待，等待计入步骤超时，超时信息会写明在等哪个资源。多个别名按固定顺序获取，不会死锁。插件完全不需要知道仪器是共用的。
- **按通道拆分**：各工位要各用一路时，工位绑定一个"通道视图"驱动，它在 `CreateAsync` 里通过作用域查到产线级的母机（`resources.Instruments.GetRequired<Psu>("psu-main")`），返回只管自己那一路的对象。这正是"传输跑在仪器上"的查找方式，不需要新概念；向母机下发命令的并发由视图驱动自己处理，或者照样在步骤上写 `exclusive: [psu-main]`。
- **故障后重建**：`line.Invalidate()` 把产线和所有工位标记为失效。谁先开始下一次运行谁重建产线资源；其他工位继续用旧的一代，直到自己下一次运行才切换，旧的一代在最后一个工位放手后释放。独占锁跨代共用，重建期间两个工位也不会同时对一台物理仪器讲话。
- `exclusive` 写了不存在的别名，步骤判 `Error`——拼错的名字不能变成一把什么都不锁的锁。校验器对它看不见的别名给警告（产线资源常常不在校验器手里）。

完整示例见 [LineResourceTests.cs](TestFramework.Tests/LineResourceTests.cs)。

## 内置步骤

| 插件 | 作用 |
|---|---|
| `basic.delay` | 延时 |
| `basic.log` | 写执行日志 |
| `basic.limit-check` | 数值是否在上下限内 |
| `basic.throw` | 故意抛异常，用于验证错误处理 |
| `basic.calculate` | 计算表达式，结果在输出 `value`；用 `variableWrites` 写入变量 |
| `basic.prompt` | 提示操作员并等待选择，选中"通过选项"则通过，否则失败 |

`basic.calculate` 不直接写变量：校验器只通过 `variableWrites` 知道哪些变量存在，插件私下写的变量会让后面每个 `${…}` 都报"未定义"。`basic.prompt` 通过宿主提供的 `IOperatorInteraction` 提问；宿主没有操作员交互时它判 `Error`，绝不自动回答"确定"——那样测试会在线束没接好的情况下继续跑。

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
- 当前契约为 **1.1**（框架 0.5.0 起）。1.1 新增：`ITestStepPlugin.IsThreadSafe`（默认 `false`）、`StepParameterKind.Expression`、`TestStepExecutionContext.Operator`。只用到 1.0 成员的插件无需改动；用到 1.1 成员的插件声明 `[assembly: TestFrameworkPlugin("1.1")]`，这样旧宿主会在加载时明确拒绝它，而不是运行到一半才缺成员。

插件开发详见 [docs/plugin-development.md](docs/plugin-development.md)，更多架构说明见 [ARCHITECTURE.md](ARCHITECTURE.md)。
