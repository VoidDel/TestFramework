namespace TestFramework.Abstractions.Models;

/// <summary>
/// Runs a step or an item again when it did not succeed, or until a condition holds.
///
/// On a step it retries an <c>Error</c> or a <c>Fail</c> - a CAN request that timed out, a reading
/// taken before the supply settled. On an item it retries only a <c>Fail</c>: an item that errored
/// has left a step part-way through, and running the whole item again against hardware in that
/// state is how a fault spreads. A step that was quarantined is never retried, because its plugin is
/// still running.
///
/// <see cref="Until"/> turns a retry into a poll: read SOC every second until <c>${soc} &gt;= 95</c>,
/// at most 600 times. It is evaluated after the attempt's variable writes, and an attempt counts as
/// successful only when it passed and the condition holds.
/// </summary>
public sealed class RetryDefinition
{
    /// <summary>Attempts in total, including the first. One means no retry.</summary>
    public int MaxAttempts { get; set; } = 1;

    /// <summary>Wait between attempts.</summary>
    public int IntervalMs { get; set; }

    /// <summary>An expression that must be true for the attempt to count; see <c>SequenceExpression</c>.</summary>
    public string? Until { get; set; }

    public RetryDefinition Clone() => new() { MaxAttempts = MaxAttempts, IntervalMs = IntervalMs, Until = Until };
}

/// <summary>
/// Runs an item - or a group of items - once per index, with the index in a variable: channel 0 to
/// 15, each with its own stimulus. Each iteration is its own result, carrying its index.
///
/// For 80 cells read in one transaction this is the wrong tool: a list output judged element by
/// element is one step instead of 80 round trips. A loop is for when each index needs its own
/// actions.
/// </summary>
public sealed class LoopDefinition
{
    /// <summary>How many iterations: a number, or an expression such as <c>${channelCount}</c>.</summary>
    public string Count { get; set; } = "1";

    /// <summary>The variable holding the zero-based index during each iteration.</summary>
    public string Variable { get; set; } = "loopIndex";

    public LoopDefinition Clone() => new() { Count = Count, Variable = Variable };
}

/// <summary>
/// Runs another sequence's items in place of this item's steps - a shared "insulation check" kept
/// in one file and called from every product's sequence.
///
/// The called sequence runs in a variable scope of its own: its own <c>variables</c>, overridden by
/// <see cref="Parameters"/>, which are resolved in the caller's scope and may reference its
/// variables. Nothing it writes leaks back. It uses the caller's resources; its own resource
/// declarations are not opened, because the station is the caller's.
/// </summary>
public sealed class SequenceCallDefinition
{
    /// <summary>The sequence to run, as the host's <c>ISequenceResolver</c> understands it - usually a file path.</summary>
    public string Path { get; set; } = string.Empty;

    public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
