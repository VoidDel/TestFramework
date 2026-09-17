using TestFramework.Abstractions.Plugins;

// Declares the lowest framework contract these editors need; the host refuses to load the assembly
// if it cannot provide it.
[assembly: TestFrameworkPlugin("1.0")]
