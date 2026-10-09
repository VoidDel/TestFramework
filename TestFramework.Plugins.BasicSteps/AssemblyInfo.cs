using TestFramework.Abstractions.Plugins;

// Declares the lowest framework contract these plugins need; the host refuses to load the assembly
// if it cannot provide it.
// 1.1: basic.prompt reaches the operator through TestStepExecutionContext.Operator.
[assembly: TestFrameworkPlugin("1.1")]
