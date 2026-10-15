namespace TestFramework.Abstractions.Resources;

/// <summary>
/// Names what a resource is - a power supply, a CAN channel - independently of who built it.
///
/// A sequence asks for a class (<c>requires: [{alias: psu, class: PowerSupply}]</c>), a driver's
/// resource type implements an interface carrying the class, and the validator and the station
/// check that the two agree before a run instead of a step failing its cast half-way through one.
/// That is what lets a station swap a Keysight supply for an ITECH one without touching the test:
/// both drivers return a <c>PowerSupply</c>, and the steps only ever ask for that.
///
/// The standard classes live in <c>TestFramework.Instruments</c>. A domain package may define more
/// the same way - a cell simulator, a BMS-specific fixture - by putting the attribute on its own
/// interface.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, Inherited = false)]
public sealed class ResourceClassAttribute : Attribute
{
    public ResourceClassAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public string Name { get; }
}

/// <summary>The resource classes a type implements, as <see cref="ResourceClassAttribute"/> names them.</summary>
public static class ResourceClasses
{
    /// <summary>
    /// Every class <paramref name="type"/> is: its own, if it carries one, and that of every
    /// interface it implements. A driver returning a type that implements both a supply and a load
    /// interface - a bidirectional source - is both.
    /// </summary>
    public static IReadOnlyCollection<string> Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new[] { type }
            .Concat(type.GetInterfaces())
            .Select(candidate => candidate.GetCustomAttributes(typeof(ResourceClassAttribute), inherit: false))
            .SelectMany(attributes => attributes.Cast<ResourceClassAttribute>())
            .Select(attribute => attribute.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>True when <paramref name="type"/> is of the class named <paramref name="className"/>, compared case-insensitively.</summary>
    public static bool Implements(Type type, string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        return Of(type).Contains(className.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
