# 插件架构评估报告（2026-09-18）

基线：TestFramework `main @ 69d7a83`（tag `v0.2.0`，桌面宿主移出后的首个 release）；宿主侧 BMS-TEST `main @ fefd83d`（以 `v0.2.0` release 为依赖）。评估范围限于插件体系：契约、版本、加载与隔离、参数模型、资源模型、交付。执行引擎、YAML、界面不在本篇。

文中标注"实测"的结论来自对代码的直接核对，其余为设计判断。

> **处置状态（2026-09-21 更新）。** P0、P1、P3 已实现，两个仓库均已落地并有测试覆盖；P2 已完成设计、未实现，见 [plugin-worker-process-design.md](plugin-worker-process-design.md)。各条的处置见文末"处置记录"。下方findings保留评估当时的原貌，不随实现改写——它们记录的是当时为什么要做，而不是现在做成了什么样。

## 总体结论

**插件架构评分约 7.0 / 10。** 边界是对的，内核是薄的。

它把"谁能引用谁"做得很严——宿主不能命名插件类型、内置步骤走第三方的路、契约版本在实例化之前从元数据读出并拒绝、三处版本解析共用一条规则——这些在同类框架里少有做到位的，而且都有测试守着。但它对"一个步骤长什么样"几乎不发表意见：参数是一个 `object` 与一个字典，硬件地址写在序列文件里，资源只活一次运行。对一个跑 BMS 产线的上位机，后面这几条才是接下来会疼的地方。

| 维度 | 评分 | 说明 |
|---|---|---|
| 契约与版本 | 8.5 | 契约版本元数据读取、additive-only 规则、`PluginVersionPolicy` 三处共用 |
| 加载与隔离 | 8.0 | 每程序集 ALC、逐类型失败隔离、同目录共享、单次扫描；重复 id 报告不够具体 |
| 参数模型 | 4.0 | 无 schema：校验器不碰参数，编辑器要么裸文本要么手写表单 |
| 资源模型 | 4.5 | 只读作用域正确；但只有"一次运行"这一种生命周期，硬件地址与测试逻辑同文件 |
| 进程隔离 | 5.0 | 进程内、合作式隔离；文档诚实，但对原生驱动无能为力 |
| 交付与可测试性 | 8.0 | 无编译期依赖、release 携带插件 zip、`TestStepExecutionContext` 可由插件作者自行构造 |

下次评估可沿用此表对照。

## 站得住的部分

不展开，只列经得起推敲、**后续修复时不应破坏**的决定：

- **加载隔离粒度。** 每程序集一个可回收 `AssemblyLoadContext`，每类型隔离激活失败，同目录被共同依赖的文件全进程一份（`PluginAssemblyCatalog.LoadShared`）。上一轮评估的"内置配置界面全部失效"高危正是这条规则补齐后消掉的。
- **契约版本在构造之前读。** `PluginContractReader` 用元数据读 `[assembly: TestFrameworkPlugin("x.y")]`，不实例化属性——被拒绝的恰恰是可能构造不出来的那个程序集，顺序不能反。
- **版本解析单一实现。** `PluginVersionPolicy` 精确优先、同主版本向前，并**报告替代**；步骤注册表、资源注册表、设置编辑器注册表共用，三者无法漂移。
- **资源作用域只读。** 插件拿到 `IResourceScope`，容器 `RuntimeResourceProvider` 留在 Core，`ReadOnlyResourceScope` 不实现 `IDisposable`。
- **运行时插件与设置编辑器分程序集。** 无头宿主不背 Avalonia；编辑器通过 `IPluginTypeHandler` 在同一遍扫描中被发现，避免二次加载造成类型双份。
- **插件作者可自测。** 实测：`TestStepExecutionContext` 为 public、全 init 属性，资源默认 `EmptyResourceScope.Instance`，不依赖宿主即可构造。
- **信任边界写得诚实。** "不是沙箱"，写权限等同代码执行。

## 发现

按对 BMS 产线的影响排序。

### 1. 高危：参数没有 schema

`ITestStepPlugin` 的参数面是 `LoadSettings(IReadOnlyDictionary<string, object?>) → object` 与 `SaveSettings(object) → IReadOnlyDictionary<string, object?>`。框架对一个步骤有哪些参数、什么类型、什么范围、什么含义一无所知。

三个后果，实测：

- **校验器完全不碰参数。** `TestFramework.SequenceYaml/Validation/TestSequenceValidator.cs` 中不存在对 `LoadSettings`、`Parameters` 或 `CreateDefaultSettings` 的任何引用。`DelayMs: abc` 要到运行时才报错——序列通过校验、保存、被评审，跑到第 7 步才发现填错。
- **无 UI 插件的步骤退化为裸键/值文本框。** 无类型、无校验、无说明。
- **有 UI 插件的代价随步骤数超线性增长。** 4 个基础步骤：`Infrastructure/SettingsMap.cs` 52 行序列化辅助、每步约 65 行、`BasicStepSettingsEditors.cs` 232 行手写 Avalonia 表单。BMS 一套 30 个步骤按此比例约两三千行纯表单代码，每加一个参数改三处。

这是设计取舍而非疏忽——`ITestStepPlugin.SettingsType` 暴露 CLR 类型，说明当初的意图是让编辑器直接 cast。但它把"步骤参数"的知识锁在了插件的私有代码里。

**建议：** 新增声明式参数描述（名称、类型、默认值、范围/枚举、说明、是否必填），作为 `ITestStepPlugin` 的**带默认实现的新成员**（返回空 → 行为同今天），契约仍是 1.x。校验器据此校验参数值与变量引用类型；宿主据此生成表单，UI 插件退化为真正需要定制的少数。这也是第 4 条的前置——跨进程时 `object` 设置对象过不去。

### 2. 高危：资源只活一次运行

实测：`BmsTest.Hosting/Execution/SequenceRunService.cs` 在 `RunAsync` 内 `RuntimeResourceBuilder.BuildAsync` 建资源，结束时 `DisposeAsync` 释放；框架侧 `RuntimeResourceBuilder` / `RuntimeResourceProvider` 也只提供这一种作用域。

意思是**每测一个 DUT，CAN 通道开一次关一次，程控电源连一次断一次**。USB-CAN 适配器初始化动辄一秒，部分仪器需要预热，部分驱动连续开关会挂。产线节拍下既是时间也是可靠性问题。宿主只是反映了框架的形状：契约没有比"一次运行"更长的作用域可用。

**建议：** 引入工位/会话级资源作用域——宿主启动时建、关闭时拆、跨运行保活；运行级作用域嵌套其中，只建序列额外声明的部分。`IResourceScope` 已经是查询接口，加一层不破坏契约。需要想清楚的是清理语义：一次运行失败（尤其资源错误）后哪些会话级资源必须重建。与第 3 条一起做。

### 3. 中危：硬件地址写在测试序列里

实测：`TestFramework.Abstractions/Models/InstrumentDefinition.cs` 携带 `Resource` 字符串与 `Settings` 字典，而 `InstrumentDefinition` 属于 `TestSequence`。"这个测试需要一台电源"与"这台工位的电源在 COM7"是同一个文件里的同一条记录。

五个工位端口不同，要么每个序列改五份，要么工位配置复制进每个序列。`${变量}` 能缓解，但变量也在序列里；`BMSTEST_DATA_DIR` 只解决数据目录，不解决硬件绑定。

**建议：** 拆成两层。序列声明**需要什么能力**（别名 + 驱动种类 + 可选版本约束）；工位配置说明**这里有什么**（别名 → 驱动 + 地址 + 设置）。运行时按别名绑定，绑定失败在校验阶段报告而不是在运行时。这是第 2 条的另一面——工位级资源自然对应工位级配置。

### 4. 中危：进程内、无隔离——已知，但对 BMS 尤其要紧

架构文档已如实说明：不能卸载、不能强杀、以宿主权限运行、隔离靠合作（取消后 100 ms 未退出即隔离）。对纯托管步骤够用。

但 BMS 上位机必然接**厂商原生驱动**——CAN 卡、程控电源的 VISA 库。这类 DLL 的典型故障是访问违例或死锁在 native 调用里：`CancellationToken` 管不到，隔离机制看不到，结果是操作员界面当场消失，正在充电的 DUT 无人看管。

**建议：** 不建议现在做，但现在就把插件工作进程当作下一个结构性台阶规划。它会反过来影响第 1 条（跨进程要求参数可序列化，schema 化不再可选）和第 2 条（会话级资源天然应活在工作进程里）。前两条落地后它的形状会清楚得多。

### 5. 低：设置编辑器契约绑死 Avalonia

`IStepSettingsEditorPlugin.CreateEditor` 返回 `Avalonia.Controls.Control`。整套"Avalonia 必须由宿主共享"的 ALC 规则由此而来——规则本身正确，但它在为一个设计决定兜底。第 1 条做了之后，绝大多数步骤不再需要自定义编辑器，Avalonia 从插件面上基本消失，此条自然收敛。

### 6. 低：两处不一致

- 实测：`TestStepPluginDescriptor.Category` 存在、默认 `"General"`，但宿主编辑器按**部署目录**分组（`PluginCatalog.CategoryOf`），从不读它。要么删掉，要么用它。
- 实测：同一 `PluginId + Version` 出现两次时，`VersionedPluginIndex.Register`（第 66 行）抛 `InvalidOperationException`，被 `PluginActivator` 逐类型隔离捕获为加载失败。行为正确，但**谁先谁赢取决于目录枚举顺序**，且报告文案是泛泛的失败——应点名"重复注册，已保留 `<路径>` 中的那份"。

## 行动计划

| 优先级 | 项 | 涉及 | 契约影响 |
|---|---|---|---|
| P0 | 参数 schema：声明式描述 + 校验器校验参数 + 宿主按 schema 生成表单 | `Abstractions`、`SequenceYaml`、BMS-TEST 编辑器 | 新增带默认实现的成员，仍为 1.x |
| P1 | 工位配置层 + 会话级资源作用域（一起做） | `Abstractions`、`Core`、BMS-TEST 宿主层 | 新增模型与接口，仍为 1.x |
| P2 | 插件工作进程 | `Core`、新工作进程宿主 | 很可能是 2.0 |
| P3 | `Category` 二选一；重复注册报告点名并说明保留规则 | `Abstractions`、`Core` | 无 |
| P3 | 第 1 条落地后收敛 UI 契约 | `Plugin.Abstractions.UI` | 视情况 |

P0 与 P1 是 BMS-TEST 从"能跑"到"能上线"的分界。


## 处置记录（2026-09-21）

| 条目 | 状态 | 落地 |
|---|---|---|
| 1. 参数没有 schema | **已实现** | `StepParameterDescriptor` / `StepParameterKind` / `StepParameterCheck`（Abstractions，带默认实现的 `ITestStepPlugin.Parameters`，契约仍 1.x）；校验器按声明检查取值与变量引用；宿主 `SchemaSettingsEditor` 按声明生成表单；四个内置步骤已声明参数 |
| 2. 资源只活一次运行 | **已实现** | `StationResourceHost` 两层作用域：工位级跨运行保活，运行级嵌套其中；`RuntimeResourceProvider` 支持父级回落；`shared: false` 可退回每次重建 |
| 3. 硬件地址写在测试序列里 | **已实现** | `ResourceRequirement`（序列声明能力）/ `StationConfiguration`（工位声明地址）/ `StationBinding`（二者匹配，校验与运行共用）；`config/station.yaml` |
| 4. 进程内、无隔离 | **已设计，未实现** | [plugin-worker-process-design.md](plugin-worker-process-design.md)。P0/P1 落地后其形状已明确；实现前仍需先回答"工作进程崩溃时 DUT 如何进入安全状态" |
| 5. 设置编辑器契约绑死 Avalonia | **按预期自然收敛** | 第 1 条落地后，声明了参数的插件不再需要自带编辑器；契约本身未动，待 P2 时与运行时插件一并处理 |
| 6. 两处不一致 | **已实现** | `Category` 改为可空（null = 未声明），宿主优先按声明分组、未声明才回退部署目录；重复注册抛 `DuplicatePluginException` 并在报告中点名保留与忽略的路径，目录扫描改为路径序以保证"谁先谁赢"可复现 |

### 与原建议的两处偏离

- **第 2 条的清理语义**（报告原文留作待定）：定为**按运行判定**而非按错误来源。`Error` 触发工位资源重建，`Fail` 不触发。理由是"因资源而失败"在 `TestVerdict.Error` 之外无法可靠区分——步骤抛出的异常由运行器内部捕获，并不会以异常形式到达宿主——而凡是抛出来的都意味着步骤半途而废，可能停在仪器事务中间。代价是任何错误运行后多一次重连，换取故障不会静默跨 DUT 传递。
- **第 3 条的校验严格度**：工位未绑定别名为**错误**，但整机没有工位配置时降为**警告**。编辑端为别的工位准备序列是正常场景，不应因本机缺少工位配置而拒绝序列。
