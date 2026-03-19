using Jint;
using Jint.Runtime;
using System.Text.RegularExpressions;

namespace ProgrammaticMcp.Jint.Spike;

/// <summary>
/// Runs small proof-of-concept scripts against the Jint runtime to validate executor behavior.
/// </summary>
public sealed class RuntimeProofHarness
{
    private readonly IReadOnlyDictionary<string, Func<object?, CancellationToken, Task<object?>>> _handlers;

    /// <summary>
    /// Creates a new proof harness that forwards host calls to the provided handler map.
    /// </summary>
    /// <param name="handlers">The host-call handlers exposed to the proof script.</param>
    public RuntimeProofHarness(IReadOnlyDictionary<string, Func<object?, CancellationToken, Task<object?>>> handlers)
    {
        _handlers = handlers;
    }

    /// <summary>
    /// Evaluates a script and returns the result or the structured failure captured by the harness.
    /// </summary>
    public async Task<RuntimeProofResult> EvaluateAsync(string script, CancellationToken cancellationToken = default)
    {
        var bridge = new SerializedHostBridge(_handlers);
        var engine = CreateEngine(bridge, cancellationToken);

        try
        {
            var value = await engine.EvaluateAsync(script, cancellationToken: cancellationToken);
            return Succeed(value.ToObject(), bridge.MaxObservedConcurrency);
        }
        catch (Exception exception)
        {
            return Fail(exception, bridge.MaxObservedConcurrency);
        }
    }

    /// <summary>
    /// Executes a script, invokes the named function, and returns the settled result or structured failure.
    /// </summary>
    public async Task<RuntimeProofResult> ExecuteAndInvokeAsync(
        string script,
        string functionName,
        CancellationToken cancellationToken = default,
        params object?[] arguments)
    {
        var bridge = new SerializedHostBridge(_handlers);
        var engine = CreateEngine(bridge, cancellationToken);

        try
        {
            await engine.ExecuteAsync(script, cancellationToken: cancellationToken);
            var pendingValue = engine.Invoke(functionName, arguments);
            var value = await pendingValue.UnwrapIfPromiseAsync(cancellationToken);
            return Succeed(value.ToObject(), bridge.MaxObservedConcurrency);
        }
        catch (Exception exception)
        {
            return Fail(exception, bridge.MaxObservedConcurrency);
        }
    }

    /// <summary>Creates the configured Jint engine used by the proof harness.</summary>
    private static Engine CreateEngine(SerializedHostBridge bridge, CancellationToken cancellationToken)
    {
        var engine = new Engine(
            options =>
            {
                options.LimitMemory(4_000_000);
                options.MaxStatements(50_000);
                options.TimeoutInterval(TimeSpan.FromSeconds(5));
                options.CancellationToken(cancellationToken);
            });

        engine.SetValue(
            "hostCall",
            new Func<string, object?, Task<object?>>(async (path, argument) =>
                await bridge.InvokeAsync(path, argument, cancellationToken)));

        return engine;
    }

    /// <summary>Builds a successful harness result.</summary>
    private static RuntimeProofResult Succeed(object? value, int maxObservedHostConcurrency)
    {
        return new RuntimeProofResult(
            Succeeded: true,
            Value: value,
            FailureCode: null,
            Message: null,
            Line: null,
            Column: null,
            MaxObservedHostConcurrency: maxObservedHostConcurrency);
    }

    /// <summary>Builds a structured failure result from an exception.</summary>
    private static RuntimeProofResult Fail(Exception exception, int maxObservedHostConcurrency)
    {
        if (TryGetSyntaxErrorLocation(exception, out var line, out var column, out var description))
        {
            return new RuntimeProofResult(
                Succeeded: false,
                Value: null,
                FailureCode: "syntax_error",
                Message: description,
                Line: line,
                Column: column,
                MaxObservedHostConcurrency: maxObservedHostConcurrency);
        }

        if (exception is OperationCanceledException)
        {
            return new RuntimeProofResult(
                Succeeded: false,
                Value: null,
                FailureCode: "execution_cancelled",
                Message: "Execution was cancelled.",
                Line: null,
                Column: null,
                MaxObservedHostConcurrency: maxObservedHostConcurrency);
        }

        if (TryGetUnknownCapabilityPath(exception, out var path))
        {
            return new RuntimeProofResult(
                Succeeded: false,
                Value: null,
                FailureCode: "unknown_capability",
                Message: path,
                Line: null,
                Column: null,
                MaxObservedHostConcurrency: maxObservedHostConcurrency);
        }

        return new RuntimeProofResult(
            Succeeded: false,
            Value: null,
            FailureCode: "unhandled_error",
            Message: DescribeException(exception),
            Line: null,
            Column: null,
            MaxObservedHostConcurrency: maxObservedHostConcurrency);
    }

    /// <summary>Attempts to extract an unknown-capability path from the supplied exception.</summary>
    private static bool TryGetUnknownCapabilityPath(Exception exception, out string? path)
    {
        if (exception is UnknownCapabilityException unknownCapabilityException)
        {
            path = unknownCapabilityException.Path;
            return true;
        }

        var message = exception.Message;
        const string prefix = "Unknown capability '";

        var prefixIndex = message.IndexOf(prefix, StringComparison.Ordinal);
        if (prefixIndex >= 0)
        {
            var startIndex = prefixIndex + prefix.Length;
            var endQuote = message.IndexOf('\'', startIndex);
            if (endQuote > startIndex)
            {
                path = message[startIndex..endQuote];
                return true;
            }
        }

        if (exception.InnerException is not null)
        {
            return TryGetUnknownCapabilityPath(exception.InnerException, out path);
        }

        path = null;
        return false;
    }

    /// <summary>Attempts to extract source-location details from a syntax error.</summary>
    private static bool TryGetSyntaxErrorLocation(
        Exception exception,
        out int? line,
        out int? column,
        out string? description)
    {
        if (exception.GetType().FullName == "Esprima.ParserException")
        {
            line = ReadNullableInt(exception, "LineNumber");
            column = ReadNullableInt(exception, "Column");
            description = exception.GetType().GetProperty("Description")?.GetValue(exception) as string ?? exception.Message;
            return true;
        }

        if (exception is JavaScriptException javaScriptException)
        {
            var match = Regex.Match(javaScriptException.Message, @"^(?<description>.+?) \((?:<anonymous>|anonymous):(?<line>\d+):(?<column>\d+)\)");
            if (match.Success)
            {
                line = int.Parse(match.Groups["line"].Value);
                column = int.Parse(match.Groups["column"].Value);
                description = match.Groups["description"].Value;
                return true;
            }
        }

        if (exception.InnerException is not null)
        {
            return TryGetSyntaxErrorLocation(exception.InnerException, out line, out column, out description);
        }

        line = null;
        column = null;
        description = null;
        return false;
    }

    /// <summary>Reads an optional integer property from an exception instance.</summary>
    private static int? ReadNullableInt(Exception exception, string propertyName)
    {
        var value = exception.GetType().GetProperty(propertyName)?.GetValue(exception);
        return value switch
        {
            int intValue => intValue,
            long longValue => checked((int)longValue),
            _ => null
        };
    }

    /// <summary>Formats an exception and its inner exceptions into a single diagnostic string.</summary>
    private static string DescribeException(Exception exception)
    {
        return exception.InnerException is null
            ? $"{exception.GetType().FullName}: {exception.Message}"
            : $"{exception.GetType().FullName}: {exception.Message} --> {DescribeException(exception.InnerException)}";
    }

    /// <summary>
    /// Serializes host calls so the proof harness can measure concurrency behavior deterministically.
    /// </summary>
    private sealed class SerializedHostBridge
    {
        private readonly IReadOnlyDictionary<string, Func<object?, CancellationToken, Task<object?>>> _handlers;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _activeCalls;
        private int _maxObservedConcurrency;

        /// <summary>Creates a serialized host bridge over the supplied capability handlers.</summary>
        public SerializedHostBridge(IReadOnlyDictionary<string, Func<object?, CancellationToken, Task<object?>>> handlers)
        {
            _handlers = handlers;
        }

        /// <summary>Gets the highest number of concurrent host calls observed during execution.</summary>
        public int MaxObservedConcurrency => _maxObservedConcurrency;

        /// <summary>Invokes a host handler while enforcing serialized access.</summary>
        public async Task<object?> InvokeAsync(string path, object? argument, CancellationToken cancellationToken)
        {
            if (!_handlers.TryGetValue(path, out var handler))
            {
                throw new UnknownCapabilityException(path);
            }

            await _gate.WaitAsync(cancellationToken);

            try
            {
                var activeCalls = Interlocked.Increment(ref _activeCalls);
                UpdateMaxObservedConcurrency(activeCalls);

                return await handler(argument, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
                _gate.Release();
            }
        }

        private void UpdateMaxObservedConcurrency(int activeCalls)
        {
            while (true)
            {
                var snapshot = _maxObservedConcurrency;
                if (activeCalls <= snapshot)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxObservedConcurrency, activeCalls, snapshot) == snapshot)
                {
                    return;
                }
            }
        }
    }

    private sealed class UnknownCapabilityException : Exception
    {
        public UnknownCapabilityException(string path)
            : base($"Unknown capability '{path}'.")
        {
            Path = path;
        }

        public string Path { get; }
    }
}
