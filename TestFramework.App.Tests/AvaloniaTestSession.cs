using Avalonia;
using Avalonia.Headless;
using Xunit;

namespace TestFramework.App.Tests;

/// <summary>
/// The single headless Avalonia session shared by every UI test in this assembly.
///
/// Avalonia keeps its platform services in process-wide statics. Starting a session per test means
/// tearing those statics down and rebuilding them repeatedly, and once one session has been
/// disposed the next teardown can find them already gone - surfacing as a
/// <see cref="NullReferenceException"/> inside <c>HeadlessUnitTestSession.Dispose</c> on whichever
/// test happened to be running. That made the Windows CI leg fail intermittently. Owning one
/// session as a collection fixture starts the platform once and tears it down once, after every
/// test in the collection has finished.
/// </summary>
public sealed class AvaloniaTestSession : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(SkiaRenderTestApp));

    public void Dispose() => Session.Dispose();
}

/// <summary>
/// Binds the "Avalonia UI" collection to <see cref="AvaloniaTestSession"/>. The collection also
/// keeps these tests from running in parallel with each other, which the shared session requires.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AvaloniaUiCollection : ICollectionFixture<AvaloniaTestSession>
{
    public const string Name = "Avalonia UI";
}

/// <summary>
/// AppBuilder with a real rendering pipeline, so visual frames are actually produced
/// (<c>UseHeadlessDrawing=false</c> is what makes rendering real rather than stubbed).
/// </summary>
public static class SkiaRenderTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
