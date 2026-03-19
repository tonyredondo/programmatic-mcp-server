using Jint;
using Jint.Runtime;
using System.Text.RegularExpressions;

namespace ProgrammaticMcp.Jint.Spike;

public sealed class RuntimeProofHarness
{
    private readonly IReadOnlyDictionary<string, Func<object?, CancellationToken, Task<object?>>> _handlers;

    public RuntimeProofHarness(IReadOnlyDictionary<string, Func<object?, CancellationToken, Task<object?>>> handlers)
    {
        _handlers = handlers;
    }

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

    private static string DescribeException(Exception exception)
    {
        return exception.InnerException is null
            ? $"{exception.GetType().FullName}: {exception.Message}"
            : $"{exception.GetType().FullName}: {exception.Message} --> {DescribeException(exception.InnerException)}";
    }

    private sealed class SerializedHostBridge
    {
        private readonly IReadOnlyDictionary<string, Func<object?, CancellationToken, Task<object?>>> _handlers;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _activeCalls;
        private int _maxObservedConcurrency;

        public SerializedHostBridge(IReadOnlyDictionary<string, Func<object?, CancellationToken, Task<object?>>> handlers)
        {
            _handlers = handlers;
        }

        public int MaxObservedConcurrency => _maxObservedConcurrency;

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
