using TestFramework.Abstractions.Plugins;

// Declares the lowest framework contract these plugins need; the host refuses to load the assembly
// if it cannot provide it.
// 1.2: the steps drive the standard classes in TestFramework.Instruments.
[assembly: TestFrameworkPlugin("1.2")]
