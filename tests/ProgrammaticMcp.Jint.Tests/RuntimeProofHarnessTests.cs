using System.Diagnostics;
using System.Globalization;
using ProgrammaticMcp.Jint.Spike;

namespace ProgrammaticMcp.Jint.Tests;

public sealed class RuntimeProofHarnessTests
{
    [Fact]
    public async Task AsyncEntrypointReturnsItsAwaitedValue()
    {
        var runtime = CreateRuntime(
            new Dictionary<string, Func<object?, CancellationToken, Task<object?>>>
            {
                ["math.double"] = (argument, _) => Task.FromResult<object?>((Convert.ToInt32(argument) * 2))
            });

        var result = await runtime.ExecuteAndInvokeAsync(
            """
            async function entry() {
                return await hostCall("math.double", 21);
            }
            """,
            "entry");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(42, Convert.ToInt32(result.Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task PromiseValuesCanBeUnwrappedWithoutBlocking()
    {
        var runtime = CreateRuntime(
            new Dictionary<string, Func<object?, CancellationToken, Task<object?>>>
            {
                ["math.increment"] = async (argument, cancellationToken) =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                    return Convert.ToInt32(argument) + 1;
                }
            });

        var result = await runtime.ExecuteAndInvokeAsync(
            """
            async function entry() {
                return await hostCall("math.increment", 41);
            }
            """,
            "entry");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(42, Convert.ToInt32(result.Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task PromiseAllWorksWhileHostDispatchRemainsSerialized()
    {
        var runtime = CreateRuntime(
            new Dictionary<string, Func<object?, CancellationToken, Task<object?>>>
            {
                ["math.delayedValue"] = async (argument, cancellationToken) =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
                    return Convert.ToInt32(argument);
                }
            });

        var result = await runtime.EvaluateAsync(
            """
            (async () => JSON.stringify(
                await Promise.all([
                    hostCall("math.delayedValue", 1),
                    hostCall("math.delayedValue", 2)
                ])))()
            """);

        Assert.True(result.Succeeded);
        Assert.Equal("[1,2]", result.Value);
        Assert.Equal(1, result.MaxObservedHostConcurrency);
    }

    [Fact]
    public async Task SyntaxErrorsAreMappedWithLineAndColumn()
    {
        var runtime = CreateRuntime(new Dictionary<string, Func<object?, CancellationToken, Task<object?>>>());

        var result = await runtime.EvaluateAsync(
            """
            async function entry() {
                const value = ;
                return value;
            }
            entry()
            """);

        Assert.False(result.Succeeded);
        Assert.True(result.FailureCode == "syntax_error", result.Message);
        Assert.NotNull(result.Line);
        Assert.NotNull(result.Column);
    }

    [Fact]
    public async Task CancellationStopsExecutionWithinABoundedTimeout()
    {
        var runtime = CreateRuntime(
            new Dictionary<string, Func<object?, CancellationToken, Task<object?>>>
            {
                ["runtime.wait"] = async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                }
            });
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        var result = await runtime.EvaluateAsync(
            """
            (async () => {
                await hostCall("runtime.wait", null);
            })()
            """,
            cancellationTokenSource.Token);

        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.Equal("execution_cancelled", result.FailureCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task UnknownCapabilitiesFailDeterministicallyThroughTheBridge()
    {
        var runtime = CreateRuntime(new Dictionary<string, Func<object?, CancellationToken, Task<object?>>>());

        var result = await runtime.EvaluateAsync(
            """
            (async () => {
                await hostCall("missing.capability", null);
            })()
            """);

        Assert.False(result.Succeeded);
        Assert.True(result.FailureCode == "unknown_capability", result.Message);
        Assert.Equal("missing.capability", result.Message);
    }

    private static RuntimeProofHarness CreateRuntime(
        IReadOnlyDictionary<string, Func<object?, CancellationToken, Task<object?>>> handlers)
    {
        return new RuntimeProofHarness(handlers);
    }
}
