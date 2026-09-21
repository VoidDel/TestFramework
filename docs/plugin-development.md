# 插件开发指南

本文面向在**独立仓库**中开发 TestFramework 插件的作者。插件与框架分别编译、分别发布，插件仓库不需要框架源码。

## 1. 交付物划分

框架发布四个 NuGet 包，插件仓库按需引用：

| 包 | 用途 | 插件是否引用 |
|---|---|---|
| `TestFramework.Abstractions` | 插件契约：步骤插件、资源插件、执行上下文、结果模型 | **是**，唯一必需 |
| `TestFramework.Plugin.Abstractions.UI` | 设置界面契约（Avalonia） | 仅当插件提供配置界面 |
| `TestFramework.Core` | 序列运行器、插件加载、资源管理 | 否，宿主才引用 |
| `TestFramework.SequenceYaml` | 序列文件读写与校验 | 否，宿主才引用 |

插件本身**不发 NuGet 包**。它的交付物是编译后的 DLL，投放到宿主的插件目录；宿主对插件没有任何编译期依赖。

## 2. 插件仓库结构

一个插件仓库产出两个程序集：运行时插件和可选的设置界面。

```
MyPlugin/
  MyPlugin/                 -> MyPlugin.dll        仅引用 Abstractions，无 Avalonia
    MyPlugin.csproj
    AssemblyInfo.cs
    MyStepPlugin.cs
    Settings/MyStepSettings.cs
  MyPlugin.UI/              -> MyPlugin.UI.dll     引用 Plugin.Abstractions.UI + MyPlugin
    MyPlugin.UI.csproj
    AssemblyInfo.cs
    MyStepEditorPlugin.cs
  MyPlugin.Tests/
```

**为什么拆两个程序集**：无头宿主（CI 流水线、产线运行器）加载运行时插件执行序列，永远不会显示界面。界面留在运行时程序集里会让这些宿主也必须能解析 Avalonia。拆开后，无头宿主只投放 `MyPlugin.dll` 即可。

不关心无头场景的插件，可以让同一个类同时实现两边的接口，发现机制完全相同。

### 运行时插件 csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TestFramework.Abstractions" Version="0.1.0" />
  </ItemGroup>
</Project>
```

### 声明框架契约版本

每个插件程序集必须声明它需要的**最低框架契约版本**：

```csharp
using TestFramework.Abstractions.Plugins;

[assembly: TestFrameworkPlugin("1.0")]
```

宿主在**实例化任何类型之前**读取这个声明。声明高于宿主能提供的版本时，整个程序集被拒绝并记录一条明确的失败信息，插件代码一行都不会执行 —— 而不是等到运行到一半抛 `MissingMethodException`。

未声明的程序集按 `FrameworkContract.Baseline`（1.0）处理，即该属性出现之前的插件所针对的契约。

## 3. 兼容规则

### 框架契约版本

`FrameworkContract.Version` 与包版本是两回事：包按自己的节奏发布，契约版本只在**插件所面对的接口发生变化**时才动。

框架承诺**高版本兼容低版本插件**。落到实现上就是一条硬规则：

> 已有插件接口新增成员时，必须带默认实现（default interface method）；已有成员的签名和语义永不改变。

无法以这种方式完成的变更，就是一个新的主契约版本；被它挡在门外的旧插件会在加载时被明确拒绝，而不是带病运行。

### 插件版本解析

序列文件里写的 `pluginVersion` 按"精确优先、同主版本向前兼容"解析：

| 序列写 | 已装版本 | 结果 |
|---|---|---|
| 1.0.0 | 1.0.0, 1.2.0 | 用 1.0.0 |
| 1.0.0 | 1.0.1, 1.2.3 | 用 1.2.3，并告警 |
| 1.0.0 | 2.0.0 | 拒绝：主版本不兼容 |
| 1.5.0 | 1.4.0 | 拒绝：不降级 |

因此插件的版本号要遵循语义化版本：**同主版本内只做向后兼容的改动**。改了参数含义、输出键或判定行为，就该升主版本。

设置界面按同一条规则匹配，所以为 1.0.0 注册的编辑器也会服务于向前解析到它的旧步骤。

## 4. 实现步骤插件

```csharp
public sealed class MyStepPlugin : ITestStepPlugin
{
    public TestStepPluginDescriptor Descriptor { get; } = new()
    {
        PluginId = "vendor.my-step",   // 全局唯一，建议带厂商前缀
        DisplayName = "我的步骤",
        Version = new Version(1, 0, 0),
        Category = "自定义",
        Description = "..."
    };

    public Type SettingsType => typeof(MyStepSettings);

    // 声明参数：校验器据此在运行前检查取值，宿主据此生成配置界面。
    public IReadOnlyList<StepParameterDescriptor> Parameters { get; } =
    [
        new StepParameterDescriptor
        {
            Name = nameof(MyStepSettings.TimeoutMs),
            Kind = StepParameterKind.Integer,
            DisplayName = "超时",
            Description = "等待设备响应的毫秒数。",
            DefaultValue = 1000,
            Minimum = 1,
            Maximum = 60000
        }
    ];

    public object CreateDefaultSettings() => new MyStepSettings();

    public object LoadSettings(IReadOnlyDictionary<string, object?> parameters) => /* 从参数字典构造 */;

    public IReadOnlyDictionary<string, object?> SaveSettings(object settings) => /* 写回参数字典 */;

    public async Task<TestStepResult> ExecuteAsync(
        TestStepExecutionContext context, object settings, CancellationToken cancellationToken)
    {
        // 必须响应 cancellationToken，并为设备操作设置自身超时。
        // 进程内插件无法被安全强杀：超时后不退出会导致整个序列中止并保留资源。
    }
}
```

要点：

- 插件类必须有**公开无参构造函数**，宿主用它实例化。
- 构造函数抛异常只影响该类型，同程序集内其它插件照常加载。
- 变量引用（`${name}`）在 `LoadSettings` 之前已解析完毕，插件拿到的是普通值。
- 参数值带着 YAML 标量类型到达：文件里未加引号的 `5.0` 是 double、`7` 是 int、`true` 是 bool，加引号的是字符串；保存时会给形似数字或布尔的字符串加引号，类型经往返不变。`LoadSettings` 应同时接受数字与字符串两种形式（内置的 `SettingsMap` 即如此），手写文件或文本输入都可能给出字符串。
- 输出写入 `TestStepResult.Outputs`，序列通过 `verdictSource.outputKey` 取用。

### 声明参数

`Parameters` 是**带默认实现**的成员：不实现就返回空，行为与没有这个成员时完全一致——校验器不碰该插件的参数，宿主回退到裸键值列表。老插件因此无需改动，契约仍是 1.x。

声明之后有两个收益：

- **运行前校验。** `TestSequenceValidator` 按声明检查取值：类型不符、超出范围、不在枚举内、必填缺失，都在编辑阶段报错，而不是跑到一半才在设备已接 DUT 的情况下失败。未声明的多余键报为警告（可能是笔误，也可能是新版插件才读的键），不拒绝序列。
- **自动生成界面。** 宿主按声明生成表单，只有确实需要定制控件的参数才值得再写一个设置界面插件。

`Kind` 只有 `String`/`Integer`/`Number`/`Boolean`/`Enum` 五种，刻意保持得小：这是宿主能逐项渲染、校验器能不靠反射检查、并且将来能跨进程传递的集合。参数形状超出这个范围的插件，继续自带设置界面即可。

`AllowVariableReference` 默认为 `true`。参数值写成 `${name}` 时，实际取值要到运行时才知道，因此类型与范围检查对该参数暂停，改为检查变量本身是否已定义；必须在运行前确定的参数把它设为 `false`。

## 5. 实现设置界面

> 先看上一节：声明了参数就已经有一套可用表单，这一节只针对确实需要定制控件的情况。


```csharp
public sealed class MyStepEditorPlugin : IStepSettingsEditorPlugin
{
    public string PluginId => "vendor.my-step";

    public Version PluginVersion => new(1, 0, 0);

    public Control CreateEditor(object settings, ISettingsEditContext context)
    {
        var typed = (MyStepSettings)settings;
        // 修改 settings 后调用 context.NotifySettingsChanged()
    }
}
```

读写参数值请使用 `SettingsValueConverter`：它保证数字格式和 `${variable}` 引用的处理方式与框架一致，否则值经编辑器往返后会被改写。

`context` 若可转为 `IParameterBindingEditContext`，还能读取序列变量并直接读写原始参数，用于支持变量绑定。

## 6. 部署布局

宿主扫描自己的 `Plugins` 目录（含子目录）：

```
MyTestHost.exe
Plugins/
  MyPlugin/
    MyPlugin.dll
    MyPlugin.UI.dll
    ThirdParty.Dependency.dll
  AnotherPlugin/
    AnotherPlugin.dll
```

- 每个插件建议独占一个子目录，其依赖放在同级。插件在 `Descriptor.Category` 里声明分组则按声明分组（`/` 分层，如 `电池/充电`）；不声明（留空）才回退到子目录名。
- 原生 DLL 和不含插件类型的程序集会被静默跳过，不会报错。
- 每个程序集加载到独立的可回收 `AssemblyLoadContext`，插件之间的依赖版本互不冲突。
- `TestFramework.Abstractions`、`TestFramework.Plugin.Abstractions.UI` 以及全部 `Avalonia.*` 程序集由宿主共享：即使它们随 `dotnet publish` 一起出现在插件目录里，也会被忽略并解析到宿主的版本。插件必须针对宿主提供的这些版本编译。共享 Avalonia 不是可选项——设置界面契约的 `CreateEditor` 返回 Avalonia 的 `Control`，插件私有的另一份 Avalonia 会让这个类型身份不一致，整个程序集报"没有实现"而无法加载。
- 同一目录里被多个程序集共同依赖的文件（典型的是 `MyPlugin.UI.dll` 依赖 `MyPlugin.dll`）在整个进程中只加载一份，所以设置界面拿到的设置对象与运行时插件创建的是同一个类型。

## 7. 生命周期限制

- 插件程序集加载后**无法卸载**。更新插件 DLL 必须重启宿主，运行期间文件处于占用状态。
- **资源插件可能跨多次运行存活。** 工位配置里标记为 `shared`（默认）的资源由 `StationResourceHost` 在宿主启动时建立、关闭时释放，中间连续测多个 DUT 都是同一个实例。驱动因此必须能被反复使用而不残留上一个 DUT 的状态；确实做不到的，在工位配置里把该项设为 `shared: false`，它就恢复为每次运行重建。
- 一次运行以 `Error` 结束后，工位共享资源会被标记为待重建，下一次运行前重新打开。
- 插件代码以宿主权限在同一进程内运行，加载不做签名或哈希校验。对插件目录的写权限等同于以当前用户身份执行代码。只读的资源契约防的是误用，不是沙箱。

## 8. 本地调试

插件仓库里引用框架包的正式版本即可开发。需要对着未发布的框架改动调试时，在插件仓库根目录放一个 `nuget.config` 指向本地包目录：

```xml
<configuration>
  <packageSources>
    <add key="local" value="../TestFramework/artifacts" />
  </packageSources>
</configuration>
```

框架仓库执行 `dotnet pack -c Release -o artifacts` 产出这四个包。

本仓库的 `TestFramework.Plugins.BasicSteps` 与 `TestFramework.Plugins.BasicSteps.UI` 就是按本文结构组织的参考实现：它们不被 App 以源码引用，而是在构建后复制到 `Plugins/BasicSteps/`，走与第三方插件完全相同的加载路径。
