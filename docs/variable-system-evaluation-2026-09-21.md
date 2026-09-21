# 变量系统专项评估（2026-09-21）

基线：`main @ 3ef0e06`（v0.3.0）。评估范围是变量系统的全部落点：

- `VariableReference`（Abstractions）——`${name}` 模式
- `VariableResolver` / `VariableSnapshot` / `TestSequenceRunner` 的变量表（Core）
- `TestSequenceValidator` + `StepParameterCheck` 的运行前检查
- `TestSequenceYamlService` 的类型往返与字典归一化
- `SettingsValueConverter`（Plugin.Abstractions.UI）与宿主编辑器侧

本报告的每条问题都用独立探针复现过，探针在记录结论后删除。

## 总体结论

**约 7.0 / 10。** 内核扎实，问题集中在校验层跟不上运行时的能力。三条确认问题里有两条属于"特性 A 与特性 B 各自都对、组合起来失效"——与 [2026-09-21 项目评估](project-evaluation-2026-09-21.md) 在 runner × station 接缝上发现的是同一种形状。这个仓库的单个组件推敲得都很细，缺陷集中在接缝上。

### 做得好的

- **YAML 往返保持类型**是真难题，解得干净：单个占位符独占整个值时保留原类型（`${targetVoltage}` 传 double），嵌在文本里则转 invariant 字符串；4 个用例守着。
- **`VariableReference` 把 `${}` 模式收敛为一份实现**，解析、校验、编辑器共用——方向正确（但见问题 3）。
- **快照深拷贝带 32 层深度上限**，挡住"插件原地改容器把已记录的快照改掉"这一真实隐患。
- **解析发生在 `LoadSettings` 之前**，插件不必自己解析变量语法。
- 写入语义克制：`Error` 时默认跳过，输出缺失报错而非静默略过。

## 问题

### 1. 校验器不认识 variableWrites 写出的变量（Error 级误报）

校验器的变量集合是 `sequence.Variables.Keys`，固定不变。`ValidateItems` 把它一次性传给每个 `ValidateSteps`，全程不因 `variableWrites` 增长。

于是文档里作为招牌写的用法——步骤 1 测量 → 写入 `measured` → 步骤 2 用 `${measured}`——直接报错：

```
Error: items[0].main[1].parameters.value:
       Parameter 'value' references variable 'measured', which is not defined.
```

是 Error 而非 Warning，在按错误拦截的宿主里直接挡住运行。且只在插件声明了 `Parameters` 时触发——只打新特性的用户。

修复注意：变量是**序列作用域**的，后一个测试项读前一个测试项写的变量合法，所以累积集合必须活在 `ValidateItems` 这一层，不能只在 `ValidateSteps` 内部累积。同时"引用了后面步骤才写的变量"仍应报错。

### 2. 变量里的嵌套字典被强制大小写不敏感，键冲突打掉整次运行

`VariableSnapshot.CaptureValue` 把每个嵌套 `IDictionary<string, object?>` 用
`StringComparer.OrdinalIgnoreCase` 重建。插件返回 `{"SOC": 80, "soc": 81}`——CAN 信号表、
UDS、JSON 载荷里再正常不过——即抛：

```
RUN-EX=ArgumentException: An item with the same key has already been added. Key: soc
at VariableSnapshot.CaptureValue ... VariableSnapshot.cs:line 37
```

端到端复现：异常从 `RunAsync` 抛出，**整个 `TestSequenceRunResult` 根本不返回**——不是步骤错误，
是整次运行的记录全丢。且它从 `RunItemAsync` 的 `finally` 里抛出，还能顶替掉正在传播的取消。

顶层变量表大小写不敏感是有意的、写进文档的设计。但**变量值内部的嵌套字典是插件的数据，
不是框架的命名空间**，框架没有理由改它的键语义。同样的问题在另外两处：

- `VariableResolver.ResolveDictionary` 对嵌套字典同样强制 `OrdinalIgnoreCase`，用的是索引器赋值，
  因此不抛异常而是**静默合并**——数据丢失，比抛异常更糟。
- `TestSequenceYamlService.NormalizeValue` 对嵌套 `IDictionary<object, object?>` 用
  `ToDictionary(..., OrdinalIgnoreCase)`，于是同样的冲突写在 YAML 文件里时，加载抛裸
  `ArgumentException`，而非该服务对所有其它畸形输入统一使用的 `InvalidDataException`——
  宿主按 `InvalidDataException` 捕获"文件无效"，这里会漏成未处理异常。

### 3. 拼错的引用静默退化成字面量

```
${measured}    isRef=True   resolved=Double=42              问题数=0
${1stReading}  isRef=False  resolved=String=${1stReading}   问题数=0
${my var}      isRef=False  resolved=String=${my var}       问题数=0
${measured     isRef=False  resolved=String=${measured      问题数=0
```

模式要求 `[A-Za-z_][A-Za-z0-9_.-]*`。不符合的就不是引用——不报错、不警告，插件收到字面文本。
而"未定义变量"这条检查存在的意义就是抓"这个引用解析不了"，它偏偏只抓格式正确的那些；
数字开头、带空格、少个括号，恰恰是人手打错时产生的形态。

编辑器侧还让它更糟：`SettingsValueConverter.IsVariableReference` 用的是
`text.Contains("${")`，与 `VariableReference.Pattern` 是**两套规则**。宿主在两处依赖它
（`StepSettingsParameterMerger`、`StepEditorWindow` 的预览参数），于是编辑器把 `${my var}`
当作变量绑定保护起来，而校验器与运行器把它当普通字符串。

这正是 `ARCHITECTURE.md` 说 `VariableReference` 要防的那件事：

> A second copy of this pattern would let a sequence pass validation and fail in the runner.

而这份副本就在插件作者编译所依赖的 SDK 里；`Plugin.Abstractions.UI` 本就引用了
`Abstractions`，这个分歧是白白产生的。

### 4. 变量无类型、无声明（设计缺口，非缺陷）

`TestSequence.Variables` 就是 `Dictionary<string, object?>`，序列没有地方声明"我有哪些变量、
各是什么类型"。后果：

- `${x}` 进入 Number 参数时类型与范围检查整个挂起（有意为之、已记录）。但这意味着声明式参数
  最大的卖点——运行前抓住 `DelayMs: abc`——恰恰对"来自产线/操作员的值"全部失效。
- 步骤可以把数值变量覆盖成字符串，没有任何地方会注意到，直到插件在接着 DUT 的情况下中途抛异常。

给 `variables:` 加上类型声明，校验器就能把引用的类型与参数的类型对上，把保证接回变量这道边界。
这是结构性动作，需要单独提案（涉及 schema 变更与编辑器改动）。

### 5. 次要

- `VariableResolver.ResolveValue` 没有递归深度保护，而 `VariableSnapshot.CaptureValue` 有（32 层）。
  当前可达输入受 YAML 的 64 层限制约束，不构成实际风险，但不对称本身说明有一侧想错了。
- 没有转义机制：无法写一个字面的 `${...}`。更麻烦的是它**不一致**——格式错的能当字面量过去，
  格式对的过不去。
- `TestFramework.Tests` 没有引用 `Plugin.Abstractions.UI`，因此 `SettingsValueConverter`
  ——变量系统契约面的一部分、问题 3 那个分歧的所在地——在框架仓库里零测试覆盖。

## 行动建议

1. **问题 2**：嵌套字典保留源比较器（快照、解析、YAML 归一化三处）；YAML 冲突改报
   `InvalidDataException`。打掉整次运行记录是最严重的后果，改动也最小。
2. **问题 1**：校验器在 `ValidateItems` 层累积 `variableWrites` 写出的名字。
3. **问题 3**：`${` 出现但解析不出合法引用时给警告；`SettingsValueConverter` 改走
   `VariableReference`；把 `Plugin.Abstractions.UI` 纳入测试项目引用。
4. **问题 4**：变量类型声明，作为独立提案讨论。

## 本轮已落实

问题 1–3 及问题 5 的后两项已修复。`dotnet test` 从 168 增至 187 个用例全部通过，0 警告。

**问题 2（嵌套字典）** 排查中发现是**四处**而非一处，属于缺一条共享规则而非一次手滑：
`VariableSnapshot.CaptureValue`、`VariableResolver.ResolveValue`、
`TestSequenceYamlService.NormalizeValue`、`TestStepDefinition.CloneValue`。新增
`TestFramework.Abstractions/Models/VariableValue.cs` 把规则收敛为唯一实现——顶层块是框架的
命名空间（大小写不敏感），值内部的字典是插件的数据（沿用源比较器，默认 `Ordinal`）。
顶层块出现仅大小写不同的键时改报 `InvalidDataException` 并点名该键，不再静默合并或抛裸
`ArgumentException`。`NestedDictionaryTests` 覆盖四个拷贝点。

**问题 1（校验器）**：累积集合提到 `ValidateItems` 层，跨测试项延续；每个步骤的
`variableWrites` 在该步骤自身参数检查完之后才加入，因此"引用后续步骤才写的变量"、
"引用自己写的变量"仍然是 Error。

**问题 3（拼错的引用）**：`StepParameterCheck` 对"含 `${` 但解析不出合法引用"的值给
Warning（不是 Error——参数可以合法地带 `${`，不可以的是看起来像绑定却不是）。
`SettingsValueConverter.IsVariableReference` 改为直接委托 `VariableReference.IsReference`，
SDK 里那份副本消除。`TestFramework.Tests` 现已引用 `Plugin.Abstractions.UI`，
`SettingsValueConverter` 不再零覆盖。

回归用例经反向验证：还原这三处修复后，6 个目标用例全部失败，而两个"仍应报错"的用例保持通过。

**下游**：`SettingsValueConverter.IsVariableReference` 的语义变化影响宿主两处调用
（`StepSettingsParameterMerger`、`StepEditorWindow` 的预览参数）。已用
`sync-framework.ps1` 对 `0.3.0-dev` 验证：`BMS-TEST` 构建 0 警告，79 个用例全部通过，
随后已切回正式 0.3.0。宿主无需改动。

**问题 4（类型检查）** 最终没有引入类型声明，而是发现类型**本来就在文件里**——保持 YAML 往返
类型不变的那套工作已经让初始值带着自己的类型。所以不改 schema，只是把已有信息用起来：

- `VariableValue.KnownIn(sequence)` 给出"变量 → 运行前可知的值"，可知的定义是：值写在文件里，
  且**没有任何** `variableWrites` 写它。凡是被写的一律记为"已定义、类型未知"——插件输出携带
  什么是插件的事，某次写入是否执行又取决于 `Enabled` 与错误策略，读文件定不下来。猜错会挡住
  本来能跑的运行，而这条检查的目的正相反。
- `StepParameterCheck.Check` 的第三个参数从"名字集合"改成"名字 → 已知值"的字典，于是
  `${targetVoltage}` 落进 Number 参数时按字面量一视同仁地查类型。
- 只查类型不查范围（范围属于运行产生的值），且只查"引用独占整个值"的情形（嵌在文本里结果
  必然是文本）。
- 同一份计算供校验器与宿主表单共用，因此表单不会接受校验器随后拒绝的值——`SequenceEditorView`
  在能拿到整个序列的地方算好后传给编辑器窗口。

**问题 5**：

- `VariableResolver` 加了 64 层递归上限，与加载器的上限同值，因此任何能加载的文件都必然能解析。
  它防的不是文件而是插件：`SaveSettings` 决定宿主写进 `Parameters` 的结构，一个自包含的结构
  会一直递归到爆栈，而 `StackOverflowException` 捕获不了，进程连同 DUT 当前的动作一起没。
- 转义落地为 `$${` → 字面量 `${`，且**仅在 `$$` 紧邻左花括号时**生效，其它位置的 `$$` 原样保留——
  否则会改变所有已经含 `$$` 的值的含义。`VariableReference.HasMalformedReference` 因此能把
  "想写引用写错了"和"故意写 `${`"分开，畸形警告不会误伤转义。

以上对应用例：`VariableTypeCheckTests`（7 个）、`VariableResolverTests`（12 个）。`dotnet test`
187 → 206 全部通过，0 警告。下游 `BMS-TEST` 两处调用点已同步（`SchemaSettingsEditor` 改传字典，
`SequenceEditorView` 改传 `VariableValue.KnownIn`），对 `0.3.0-dev` 构建 0 警告、79 用例通过。
