namespace TestFramework.Abstractions.Models;

/// <summary>
/// One unit a numeric judgment can convert: a symbol, a dimension, and how a value in it becomes
/// one in the dimension's base - <c>base = value * ScaleToBase + OffsetToBase</c>. <c>mV</c> is
/// voltage scaled by 0.001; <c>K</c> is temperature offset by -273.15 from the Celsius base. A
/// dimension not in the table yet is created by its first unit, whose scale should then be 1.
///
/// A plugin whose instrument reports in a unit the framework does not know declares it in its
/// descriptor's <c>Units</c> (contract 1.2), and the registry adds it to
/// <see cref="MeasurementUnits"/> when the plugin is registered - at load, before any sequence is
/// validated against it.
/// </summary>
public sealed record MeasurementUnit(string Symbol, string Dimension, double ScaleToBase, double OffsetToBase = 0);
