using TestFramework.Abstractions.Resources;

namespace TestFramework.Abstractions.Models;

/// <summary>
/// A resource a sequence needs, named by alias and described by capability rather than by address.
///
/// This is the half of the split that belongs in the test: "this test needs a programmable supply,
/// which I will call psu". Where that supply is - COM7 on this station, COM3 on the next - belongs
/// to <see cref="StationResourceBinding"/>, because it is a property of the bench, not of the test.
///
/// Keeping the two in one record, as <see cref="InstrumentDefinition"/> still does, means five
/// stations with different ports need either five copies of every sequence or the station's wiring
/// pasted into each one. Either way the sequence stops being the thing under version control that
/// describes the test.
/// </summary>
public sealed class ResourceRequirement
{
    /// <summary>
    /// How steps and other resources refer to this one. It is the join between the sequence and the
    /// station configuration, so the same alias must appear in both.
    /// </summary>
    public string Alias { get; set; } = string.Empty;

    public ResourcePluginKind Kind { get; set; } = ResourcePluginKind.InstrumentDriver;

    /// <summary>
    /// The driver the sequence insists on, when it needs a particular one. Null means any driver of
    /// the right kind will do, which is the usual case and the one that lets a station swap a
    /// vendor without touching the test.
    /// </summary>
    public string? DriverId { get; set; }

    /// <summary>
    /// The lowest driver version this sequence works with, checked against what the station binds.
    /// Null means no constraint.
    /// </summary>
    public string? MinimumDriverVersion { get; set; }

    /// <summary>What this resource is for, shown when a station has nothing bound to the alias.</summary>
    public string? Description { get; set; }
}
