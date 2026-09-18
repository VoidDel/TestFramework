# TestFramework 项目评估报告（2026-09-18）

基线：`main @ 6c0e5e5`（插件体系重构 PR #1 合并后）。本机实测 Release 构建 0 警告，`dotnet test` 134 个用例全部通过（核心 115、桌面 19），GitHub Actions 双平台绿色。

对照 [2026-09-17 评估报告](project-evaluation-2026-09-17.html) 的行动计划逐项核实，并对新发现的问题用独立探针程序复现。

## 总体结论

**综合评分约 7.5 / 10**（上次 7.0）。上一轮 P0/P1 几乎全部落实且均有测试覆盖；但本次重构在插件加载上下文中引入了一个用户可见的高危回归：内置步骤的配置界面在桌面应用里全部失效，而现有测试恰好被错误提示控件满足，没有拦住。

| 维度 | 上次 | 本次 | 说明 |
|---|---|---|---|
| 架构与依赖 | 7.5 | 8.0 | 宿主与插件彻底解耦，框架契约版本落地 |
| Core 执行引擎 | 7.5 | 8.5 | 取消清理、observer 隔离、快照深拷贝全部修复 |
| SequenceYaml | 7.0 | 8.0 | null 腐蚀、重复键、深嵌套修复；类型语义与文档不符 |
| 插件体系 | 6.0 | 6.5 | 版本策略、单次扫描、契约版本完成；加载上下文回归 |
| 桌面 UI | 6.5 | 6.0 | 安全项修复；配置界面失效；结构未变 |
| 测试充分性 | 6.5 | 7.5 | 70 → 134 用例；一处断言过宽形成掩盖 |
| 安全与健壮性 | 6.0 | 7.5 | 信任边界与只读契约已明确 |
| 工程化与文档 | 8.0 | 8.0 | 文档质量高；发布流程与文档承诺不一致 |

## 上一轮问题落实情况

已核实修复并有对应测试：

- YAML `"null"` / `"~"` 字符串往返腐蚀（`NullLikeStringEventEmitter`）
- YAML 重复键静默覆盖（`WithDuplicateKeyChecking`）
- 深嵌套 YAML 栈溢出导致应用无法启动（Scanner 预扫描，上限 64 层）
- 删除序列文件无确认（确认对话框 + `DeleteSequence_WithoutConfirmation_KeepsTheFileOnDisk`）
- `async void` 处理器无兜底（10 处全部包裹 try/catch）
- 日志 O(n²) 拼接与内存无界（StringBuilder + 256 KB 上限）
- observer 回调异常吞掉运行结果（`SafeExecutionObserver`）
- 取消后整体跳过 cleanup（`RunCleanupAfterCancellationAsync`，30 秒宽限）
- U+00B5 微符号不归一化
- 变量快照浅拷贝（`VariableSnapshot` 深拷贝）
- 资源插件契约依赖具体类（`IResourceScope` 只读契约，`RuntimeResourceProvider` 下沉 Core）
- 两个加载器隔离粒度不一致、目录扫描噪音（`PluginActivator` 统一逐类型隔离，`PEReader` 过滤原生 DLL）
- 版本全链路精确匹配（`PluginVersionPolicy` 精确优先、同主版本向前兼容，三处共用）
- 校验器规则缺失（枚举越界、资源插件注册；用例 7 → 16）
- `Enabled=false` 跳过行为零覆盖
- 示例 YAML 无自动化校验（`SampleSequenceTests`）
- 插件生命周期、信任边界、共享程序集清单无文档
- `.gitignore` 缺项

**上次一项高危为误报**：`Directory.Build.props` 的 Item 条件在属性求值完成后才计算，`dotnet pack` 实测成功且 README.md 已进入 nupkg，`NU5039` 不会发生。

未处理的 P2 结构项：`SequenceEditorView.axaml.cs` 增至 1956 行、无 MVVM、数据目录硬编码在程序目录、无撤销体系、测试仍通过反射调用私有成员。

## 本次新发现

### 1. 高危：内置步骤配置界面在应用中全部失效

`TestFramework.Core/Plugins/PluginAssemblyLoadContext.cs` 的 `Load` 解析同目录依赖时直接按路径加载进当前上下文，绕过了 `PluginAssemblyCatalog`。`BasicSteps.UI.dll` 引用 `BasicSteps.dll`，于是后者被加载了第二份；编辑器中的 `(DelayStepSettings)settings` 抛出 `InvalidCastException`：

```
[A]DelayStepSettings cannot be cast to [B]DelayStepSettings.
Type A originates from ... context 'TestFramework.Plugin:TestFramework.Plugins.BasicSteps:...'
Type B originates from ... context 'TestFramework.Plugin:TestFramework.Plugins.BasicSteps.UI:...'
```

`StepEditorWindow` 捕获后仅显示"插件配置界面加载失败，请使用参数列表编辑"。该回归由 4297e80 引入。`EditorSafetyTests.BoundNumericParameter_RemainsEditable...` 只断言 `PluginSettingsContent.Content` 非空，错误提示的 `TextBlock` 恰好满足，因此未被发现。

修复：`Load` 中对已由目录加载过的程序集，返回目录持有的同一实例；测试改为断言渲染出真实的编辑控件。

### 2. 中危：UI 插件随 Avalonia DLL 一起部署会整批加载失败

把 `Avalonia.Base.dll` 与 `Avalonia.Controls.dll` 放在插件旁（`dotnet publish` 的默认产物），四个编辑器全部报错：

```
Method 'CreateEditor' in type 'DelayStepEditorPlugin' ... does not have an implementation.
```

Avalonia 不在共享程序集名单中，插件上下文加载了第二份 Avalonia，接口签名中的 `Control` 类型身份不一致。`docs/plugin-development.md` 第 6 节只警告了两个契约包。

修复：Avalonia 程序集一律由宿主共享，并在插件开发文档中说明。

### 3. 中危：文档承诺的类型语义与实际不符

YAML 加载后，`variables`、`parameters`、`settings` 中的标量一律是字符串（`5.0` → `"5.0"`，`true` → `"true"`），而 README 与 ARCHITECTURE 声称 `${var}` 单独引用时"保留原始变量类型"。桌面应用运行前经 Save→Load 做快照，所以插件永远拿不到类型化的值。内置插件因 `SettingsMap` 会解析字符串而未暴露；第三方插件按文档实现会出错。

修复：让文件加载与文档一致（对未加引号的标量推断类型，保存时对会被误判类型的字符串加引号），并补充往返测试。

### 4. 低危

- `release.yml` 只打包 `Abstractions` 与 `Plugin.Abstractions.UI`，文档承诺发布 4 个包（含 `Core`、`SequenceYaml`）；PR 流水线无 `dotnet pack` 冒烟。
- 插件拿到的 `IResourceScope` 就是 `RuntimeResourceProvider` 本体，`as IDisposable` 即可释放宿主容器。文档已声明"不是沙箱"，但一个只读包装成本很低。

## 行动建议

1. 修复插件上下文对同目录已加载程序集的解析，并加强编辑器测试。
2. 共享 Avalonia 程序集，更新插件部署文档。
3. 统一参数类型语义，使文件加载与文档一致。
4. 发布流程打包全部 4 个库，PR 流水线加入 pack 冒烟。
5. 用只读包装向插件暴露资源作用域。
6. 中期：拆分编辑器代码隐藏、数据目录可配置、撤销体系。
