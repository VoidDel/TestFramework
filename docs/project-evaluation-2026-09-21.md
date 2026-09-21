# TestFramework 项目评估报告（2026-09-21）

基线：`main @ 35d1412`（v0.2.1，工位配置与资源作用域合并后）。本机实测 `dotnet test TestFramework.sln -c Release` 163 个用例全部通过，0 警告，退出码 0。仓库 9935 行 C#、8 个项目、31 次提交。

对照 [2026-09-18 评估报告](project-evaluation-2026-09-18.md) 的行动计划逐项核实，新发现的问题用独立探针程序复现后才记入。

## 总体结论

**综合评分约 8.0 / 10**（上次 7.5）。上一轮 6 条行动建议全部落地且均有测试覆盖，这是一次干净的兑现。本次未发现上一轮那种用户可见的高危回归；两条主要问题分别是一个吞掉故障根因的缺陷，和一个把安全关键协议外包给宿主的 API 形状。

| 维度 | 上次 | 本次 | 说明 |
|---|---|---|---|
| 架构与依赖 | 8.0 | 8.5 | 宿主彻底移出，交付物路径诚实：插件不发包，与第三方插件走同一条路 |
| Core 执行引擎 | 8.5 | 8.5 | 取消、超时、隔离语义扎实；运行编排接缝有缺口（见"新发现 2"） |
| SequenceYaml | 8.0 | 8.5 | 类型往返、重复键、深嵌套、注释与锚点全部把住 |
| 插件体系 | 6.5 | 8.5 | 契约版本、单次扫描、目录共享加载、参数声明全部到位 |
| 资源与工位 | — | 7.0 | 设计方向正确，但有一处吞根因的缺陷，且零集成测试 |
| 测试充分性 | 7.5 | 8.0 | 134 → 163 用例，用例命名描述行为而非方法名；接缝未覆盖 |
| 安全与健壮性 | 7.5 | 7.5 | 信任边界与只读契约维持；无新增暴露面 |
| 工程化与文档 | 8.0 | 8.5 | 注释解释"为什么"而非"是什么"，密度罕见 |

桌面 UI 维度本轮取消：`98956af` 之后本仓库不再含宿主。

### 突出优点

几乎每个非显然的决定都在代码里留下了理由。`PluginAssemblyCatalog`、`PluginVersionPolicy`、`VariableSnapshot`、`PluginDirectoryLoader` 的注释都在讲"不这么做会怎样错"，而不是复述代码。对一个会被第三方插件作者长期面对的框架，这是真资产：它让下一个改这段代码的人知道哪些约束是踩过坑换来的。

## 上一轮问题落实情况

6 条行动建议全部完成，均有对应测试：

- 插件上下文对同目录已加载程序集的解析（`17d5cbe`，`PluginAssemblyCatalog.LoadShared`；`ResolvingASibling_ReturnsTheInstanceTheCatalogAlreadyHolds`）
- Avalonia 程序集一律由宿主共享（`201f157`，`IsSharedWithHost_CoversTheContractsAndAvalonia`）
- 参数类型语义与文档一致（`eef6515`，`PlainScalarTypeResolver` + `RoundTripScalarEventEmitter`；`SaveAndLoad_KeepsNumericTypes` 等 4 例）
- 发布流程打包全部 4 个库，PR 流水线加入 pack 冒烟（`d2587b6`，`dotnet.yml` 断言包数为 4 且内置插件已暂存）
- 只读包装向插件暴露资源作用域（`b804984`，`ReadOnlyResourceScope`；`ScopeHandedToPluginsCannotDisposeTheContainer`）
- 桌面宿主移出仓库（`98956af`），结构类 P2 项随之不再属于本仓库

## 本次新发现

### 1. 中高危：工位资源打开失败时，故障根因被清理异常完全吞掉

`TestFramework.Core/Resources/StationResourceHost.cs` 的 `BeginRunAsync` 与 `EnsureSessionAsync` 两处都是同一写法：

```csharp
catch
{
    await run.DisposeAsync().ConfigureAwait(false);
    throw;                       // DisposeAsync 自己抛出时，原始异常没了
}
```

`RuntimeResourceProvider.Dispose/DisposeAsync` 在任一资源清理失败时抛 `AggregateException`，于是它取代了真正的故障原因。

探针复现：工位绑定 psu（能打开、关闭时抛）与 dmm（打开即失败），调用 `OpenAsync()` 得到

```
类型    = AggregateException
消息    = Runtime resource cleanup failed. (PSU refused to close)
根因存在 = False
```

操作员看到的是"电源关不掉"，而真正的原因"万用表不在台上"一个字都不剩，排障方向被直接带偏。

关键在于这条教训仓库里已经学过：`RuntimeResourceBuilder.BuildAsync` 对同一场景处理得完全正确，失败时抛 `AggregateException(buildError, cleanupError)`，两个都留。更晚写的 `StationResourceHost` 把它丢了。

修复：两处改用 `RuntimeResourceBuilder` 已有的双异常写法，并为 station 路径补上对应用例。

### 2. 中高危：跨运行的插件隔离保护，在框架的 API 形状下会自动失效

`TestSequenceRunner` 在构造函数里接收 `RuntimeResourceProvider`，而 `StationResourceHost.BeginRunAsync` 每次运行返回一个新 scope。两者组合的唯一写法就是每次运行 `new TestSequenceRunner(...)`——而 `_running`、`_abandonedStep`、`PendingStepsCompletion` 全是实例字段。

后果：上一次运行隔离掉的插件仍在后台运行，新 runner 对此一无所知，从同一个注册表拿到同一个插件实例（插件是单例，由 `PluginActivator` 在加载时创建一次）再次调用 `ExecuteAsync`，对着真实设备并发执行。这正是隔离机制要防的事。

`BMS-TEST` 的 `BmsTest.Hosting/Execution/SequenceRunService.cs` 把这个补上了，而且补得正确，但代价是约 40 行容易写错的代码：semaphore 跨方法持有、从后台恢复任务里 `Release()`、`_recoveryError` 闸门。同时 `ARCHITECTURE.md` 的"Stations and Resource Scopes"一节把"运行以 `TestVerdict.Error` 结束后置工位作用域为 stale"描述成框架行为，实现却在宿主里（`_station?.Invalidate()`）。

即：框架文档承诺的策略，框架自己没有实现。第二个宿主必须重新推导一遍，推错了没有任何编译期或运行期提示，而失效模式是静默的。README 那句"宿主必须先 `await TestSequenceRunner.PendingStepsCompletion`"把一条安全关键协议完整外包了出去。

修复方向有两档。彻底的一档是在 Core 提供组合好的运行入口，把"取 scope → 运行 → Error 则 Invalidate → 隔离则延迟释放并闸住下一次运行"整套收回框架；最小的一档是消除根因——让资源可以按次传入，宿主就能复用同一个 runner，现有闸门自然跨过运行边界。本轮采用了后者（见"本轮已落实"）。

### 3. 中危：框架内没有 runner × station 的集成测试

`StationResourceTests` 从不构造 `TestSequenceRunner`，`RuntimeResourceTests` 从不构造 `StationResourceHost`。两个头牌特性的接缝只在下游 `BmsTest.Hosting.Tests/StationRunTests.cs` 被覆盖——而框架按 NuGet 包交付，下游测试挡不住框架侧的回归。

这正是"新发现 1"能溜进来的机制：builder 路径有 `BuildAsync_DisposesCreatedResourcesWhenLaterResourceFails`，station 路径没有对应用例。补测试比单修那个缺陷更重要，它是结构性措施。

### 4. 低危：单位解析规则正确但没有写下来

初稿在此处判为"中危：判定单位可以被静默丢弃"，并建议"测量值自带单位而判定未声明单位时判 `Inconclusive`"。复核 `VerdictSource` 的 `SourceUnit` / `Unit` 语义后，**这个结论和它的修复建议都是错的**，已更正——按原建议改会把一批合法序列变成 `Inconclusive`。

实际规则（读 `UnitConverter` 得出）是自洽的：

- `unit` 是**限值所用的单位**，即换算目标。留空表示"不要求换算"，限值就按测量值本身的单位理解。
- `sourceUnit` 是测量值的单位，**仅在测量值自己不带单位时**作为兜底。
- 测量值自带的单位优先于 `sourceUnit`。
- 单位不认识、或量纲不匹配 → `Inconclusive`，绝不静默裸比。

所以"插件返回 `1234 mV`、`unit` 留空、限值写成 mV"是**受支持的用法**，不是缺陷。把它改成 `Inconclusive` 会破坏它。

真正的缺口是：以上四条规则在 README 里只有一句"数值判定支持常用单位自动换算"，`ARCHITECTURE.md` 完全没写。对一个测量框架，单位解析规则正是必须落纸的部分——留空 `unit` 的含义、以及测量值单位盖过 `sourceUnit` 这条优先级，现在只能靠读源码得知。

修复：把规则写进 `ARCHITECTURE.md` 的 Execution Rules 与 README 的运行语义，不动代码。

### 5. 低危

- `TestFramework.Abstractions/Plugins/ITestStepPlugin.cs` 顶部注释"The application serializes sequence runs by default"已过时：那个 application 在 `98956af` 已移出仓库，该并发保证现在由宿主提供。这条注释恰好位于"新发现 2"的入口处，且是第三方插件作者最先读到的文字。
- 无 `TreatWarningsAsErrors`。当前 0 警告，但没有任何机制守着它。
- 文档语言分裂：README 与 `docs/` 为中文，`ARCHITECTURE.md` 与全部代码注释、XML doc 为英文；而进 nupkg 的是中文 README，面向的却是会读英文 API 文档的插件作者。
- `PluginAssemblyCatalog` 为进程级静态，宿主无法作用域化或重置。插件本就不可卸载，实际影响有限，但值得在插件文档里点明。

## 行动建议

1. 修复 `StationResourceHost` 两处吞根因的 `catch`，改用 `RuntimeResourceBuilder` 已有的双异常写法。
2. 补 runner × station 接缝的集成测试——防止第 1 类回归的结构性措施，优先级高于单修缺陷本身。
3. 把单位解析规则写进 `ARCHITECTURE.md` 与 README（不动代码）。
4. 更正 `ITestStepPlugin` 的过时并发注释。
5. 中期：把运行编排收回 Core，让 `ARCHITECTURE.md` 承诺的工位失效策略由框架实现而非宿主复刻。
6. 低优先：`TreatWarningsAsErrors`、nupkg 内 README 的语言选择。

## 本轮已落实

五项全部处理，`dotnet test` 从 163 增至 168 个用例全部通过，0 警告：

- **1 + 3**：新增 `TestFramework.Core/Resources/ResourceScopeCleanup.cs`，把"释放半开的作用域时不得让清理异常取代构建异常"收敛为唯一实现，`RuntimeResourceBuilder` 与 `StationResourceHost` 的三处调用点全部改走它。新增 `TestFramework.Tests/StationRunTests.cs`：两个根因回归用例 + 两个 runner × station 接缝用例。回归用例经验证——把 `StationResourceHost` 还原到修复前，两者都以"Expected: the meter is not on the bench / Actual: the supply would not close"失败。
- **2**：`TestSequenceRunner` 新增 `RunAsync(sequence, resources, cancellationToken)` 重载，资源可按次传入，宿主得以复用同一个 runner 实例——`_running` / `_abandonedStep` / `PendingStepsCompletion` 这套闸门于是真正跨过运行边界。构造函数路径保持不变，非破坏性改动。新增用例 `RunAsync_ReusedAcrossRuns_RefusesTheNextRunWhileAPluginHasNotExited`：第一次运行的插件无视超时被隔离后，同一 runner 对下一次运行（带自己的新 scope）抛 `InvalidOperationException`。README 与 `ARCHITECTURE.md` 已把"复用同一个 runner"写成宿主应遵循的模式。
- **4**：单位解析的四条规则已写入 `ARCHITECTURE.md` 与 README。
- **5**：`ITestStepPlugin` 的并发注释已改为描述实际契约，并指向 `PendingStepsCompletion` 协议。

仍未动的是把整套运行编排（含 `Invalidate` 策略与延迟释放）收进 Core 的那一档。根因既已消除，它降级为整洁性改进：`ARCHITECTURE.md` 承诺的工位失效策略目前仍由宿主实现，第二个宿主还是得自己写一遍——但写错的后果已从"插件并发执行"降为"故障后多跑一次旧连接"。

### 下游 `BMS-TEST` 已同步改造

`SequenceRunService` 已把 `TestSequenceRunner` 提升为服务级字段，改调 `RunAsync(sequence, resources, ct)`。`dotnet test BMS-TEST.sln` 从 77 增至 79 个用例全部通过，0 警告。

- **新增 `CurrentRunObserver`**：runner 的 observer 在构造时固定，而调用方是按次传入的，所以需要一层转发。它刻意不在运行结束时解绑——超时后仍在跑的插件还会继续写日志，那些行属于启动它的那次运行，而在下一次运行被放行之前，它一直是当前绑定的那个。新增 `BmsTest.Hosting.Tests/RunObserverTests.cs` 两个用例覆盖：每次运行只汇报给自己那个 observer；observer 抛异常不会让运行作废。
- **更正**：初稿说宿主的 `_runLock` / `_recoveryError` "可以相应简化"，这是错的。二者是承重的：`_runLock` 跨方法持有到恢复完成，把调用方挡在**打开任何资源之前**，并给出宿主自己的中文提示；框架闸门只能在服务已经开完这次运行的资源之后才拒绝。`_recoveryError` 承载的"清理失败后永久停止接受运行"也是框架没有的策略。二者原样保留。
- 因此对本宿主而言，这次改造是**纵深防御**而非修 bug——它原本的记账就是对的。真正的收益在于：框架现在自己守住了这条不变量，下一个宿主不必重新推导。

**需要注意**：`SequenceRunService` 现在依赖 0.2.1 里还没有的重载。本地已通过 `tools/sync-framework.ps1` 切到 `0.2.1-dev`（`Directory.Build.local.props`，gitignored）。在框架发出含该重载的版本、并把 `TestFrameworkVersion` 指过去之前，不能执行 `sync-framework.ps1 -Off`，否则宿主编译不过。按 semver，新增公开 API 应发 **0.3.0**。
