namespace TestFramework.Abstractions.Execution;

/// <summary>
/// How a step asks the operator something - "connect the harness, then press OK" - without knowing
/// whether the host is a desktop window, a touch panel or a headless line controller.
///
/// The host implements it and passes it to the runner; a step reaches it through
/// <see cref="TestStepExecutionContext.Operator"/>. A host with nobody to ask keeps the default,
/// <see cref="NoOperatorInteraction"/>, which refuses - a prompt that silently answered itself would
/// let a test run on with the harness unconnected.
/// </summary>
public interface IOperatorInteraction
{
    /// <summary>
    /// Shows <paramref name="prompt"/> and waits for an answer. Cancellation - the run being stopped,
    /// or the step timing out - must close the prompt and throw.
    /// </summary>
    Task<OperatorResponse> PromptAsync(OperatorPrompt prompt, CancellationToken cancellationToken);
}

public sealed class OperatorPrompt
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    /// <summary>The answers offered, first as the default: "确定", "取消". Never empty.</summary>
    public IReadOnlyList<string> Options { get; init; } = ["OK"];

    /// <summary>The step that is asking, for a host that shows it beside the prompt.</summary>
    public string? StepName { get; init; }
}

public sealed class OperatorResponse
{
    /// <summary>The option chosen, exactly as offered.</summary>
    public required string Option { get; init; }

    /// <summary>Who answered, when the host knows.</summary>
    public string? RespondedBy { get; init; }

    public DateTimeOffset RespondedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>The interaction a host without an operator gets: every prompt fails, with the reason.</summary>
public sealed class NoOperatorInteraction : IOperatorInteraction
{
    public static NoOperatorInteraction Instance { get; } = new();

    private NoOperatorInteraction()
    {
    }

    public Task<OperatorResponse> PromptAsync(OperatorPrompt prompt, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            $"Step asked the operator '{prompt.Message}', but this host has no operator interaction.");
}
