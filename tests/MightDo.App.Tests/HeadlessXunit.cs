using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Xunit.Internal;
using Xunit.Sdk;
using Xunit.v3;

namespace Avalonia.Headless.XUnit;

// Avalonia.Headless.XUnit 12.1.3 targets xUnit 3.2's discovery and execution
// interfaces. Keep the small test adapter here until Avalonia supports xUnit 4.
[AttributeUsage(AttributeTargets.Method)]
[XunitTestCaseDiscoverer(typeof(HeadlessFactDiscoverer))]
public sealed class AvaloniaFactAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : FactAttribute(sourceFilePath, sourceLineNumber);

[AttributeUsage(AttributeTargets.Method)]
[XunitTestCaseDiscoverer(typeof(HeadlessTheoryDiscoverer))]
public sealed class AvaloniaTheoryAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : TheoryAttribute(sourceFilePath, sourceLineNumber);

public sealed class HeadlessFactDiscoverer : FactDiscoverer
{
    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetails(
            discoveryOptions, testMethod, factAttribute);

        return new HeadlessTestCase(
            details.ResolvedTestMethod,
            details.TestCaseDisplayName,
            details.UniqueID,
            details.Explicit,
            details.SkipExceptions,
            details.SkipReason,
            details.SkipType,
            details.SkipUnless,
            details.SkipWhen,
            testMethod.Traits.ToReadWrite(StringComparer.OrdinalIgnoreCase),
            sourceFilePath: details.SourceFilePath,
            sourceLineNumber: details.SourceLineNumber,
            timeout: details.Timeout);
    }
}

public sealed class HeadlessTheoryDiscoverer : TheoryDiscoverer
{
    protected override ValueTask<IReadOnlyCollection<IXunitTestCase>> CreateTestCasesForDataRow(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        ITheoryAttribute theoryAttribute,
        ITheoryDataRow dataRow,
        object?[] testMethodArguments,
        string? index)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetailsForTheoryDataRow(
            discoveryOptions, testMethod, theoryAttribute, dataRow, testMethodArguments, index);

        IXunitTestCase testCase = new HeadlessTestCase(
            details.ResolvedTestMethod,
            details.TestCaseDisplayName,
            details.UniqueID,
            details.Explicit,
            dataRow.Label,
            dataRow.DisableParallelization ?? false,
            details.SkipExceptions,
            details.SkipReason,
            details.SkipType,
            details.SkipUnless,
            details.SkipWhen,
            TestIntrospectionHelper.GetTraits(testMethod, dataRow),
            testMethodArguments,
            details.SourceFilePath,
            details.SourceLineNumber,
            details.Timeout);

        return new ValueTask<IReadOnlyCollection<IXunitTestCase>>([testCase]);
    }

    protected override ValueTask<IReadOnlyCollection<IXunitTestCase>> CreateTestCasesForTheory(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        ITheoryAttribute theoryAttribute)
    {
        var details = TestIntrospectionHelper.GetTestCaseDetails(
            discoveryOptions, testMethod, theoryAttribute);

        IXunitTestCase testCase = new HeadlessDelayEnumeratedTheoryTestCase(
            details.ResolvedTestMethod,
            details.TestCaseDisplayName,
            details.UniqueID,
            details.Explicit,
            theoryAttribute.SkipTestWithoutData,
            details.SkipExceptions,
            details.SkipReason,
            details.SkipType,
            details.SkipUnless,
            details.SkipWhen,
            testMethod.Traits.ToReadWrite(StringComparer.OrdinalIgnoreCase),
            details.SourceFilePath,
            details.SourceLineNumber,
            details.Timeout);

        return new ValueTask<IReadOnlyCollection<IXunitTestCase>>([testCase]);
    }
}

public sealed class HeadlessTestCase : XunitTestCase, ISelfExecutingXunitTestCase
{
    [Obsolete("For xUnit deserialization only")]
    public HeadlessTestCase() { }

    public HeadlessTestCase(
        IXunitTestMethod testMethod,
        string testCaseDisplayName,
        string uniqueID,
        bool @explicit,
        Type[]? skipExceptions = null,
        string? skipReason = null,
        Type? skipType = null,
        string? skipUnless = null,
        string? skipWhen = null,
        Dictionary<string, HashSet<string>>? traits = null,
        object?[]? testMethodArguments = null,
        string? sourceFilePath = null,
        int? sourceLineNumber = null,
        int? timeout = null)
        : base(testMethod, testCaseDisplayName, uniqueID, @explicit, skipExceptions,
            skipReason, skipType, skipUnless, skipWhen, traits, testMethodArguments,
            sourceFilePath, sourceLineNumber, timeout) { }

    public HeadlessTestCase(
        IXunitTestMethod testMethod,
        string testCaseDisplayName,
        string uniqueID,
        bool @explicit,
        string? testLabel,
        bool disableParallelization,
        Type[]? skipExceptions,
        string? skipReason,
        Type? skipType,
        string? skipUnless,
        string? skipWhen,
        Dictionary<string, HashSet<string>>? traits,
        object?[]? testMethodArguments,
        string? sourceFilePath,
        int? sourceLineNumber,
        int? timeout)
        : base(testMethod, testCaseDisplayName, uniqueID, @explicit, testLabel,
            disableParallelization, skipExceptions, skipReason, skipType, skipUnless,
            skipWhen, traits, testMethodArguments, sourceFilePath, sourceLineNumber,
            timeout) { }

    public ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        ParallelMode parallelMode,
        ExecutionScheduler scheduler,
        FixtureMappingManager caseFixtureMappings) =>
        HeadlessCaseRunner.Run(this, explicitOption, messageBus, constructorArguments,
            aggregator, cancellationTokenSource, parallelMode, scheduler, caseFixtureMappings);
}

public sealed class HeadlessDelayEnumeratedTheoryTestCase : XunitDelayEnumeratedTheoryTestCase,
    ISelfExecutingXunitTestCase
{
    [Obsolete("For xUnit deserialization only")]
    public HeadlessDelayEnumeratedTheoryTestCase() { }

    public HeadlessDelayEnumeratedTheoryTestCase(
        IXunitTestMethod testMethod,
        string testCaseDisplayName,
        string uniqueID,
        bool @explicit,
        bool skipTestWithoutData,
        Type[]? skipExceptions = null,
        string? skipReason = null,
        Type? skipType = null,
        string? skipUnless = null,
        string? skipWhen = null,
        Dictionary<string, HashSet<string>>? traits = null,
        string? sourceFilePath = null,
        int? sourceLineNumber = null,
        int? timeout = null)
        : base(testMethod, testCaseDisplayName, uniqueID, @explicit, skipTestWithoutData,
            skipExceptions, skipReason, skipType, skipUnless, skipWhen, traits,
            sourceFilePath, sourceLineNumber, timeout) { }

    public ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        ParallelMode parallelMode,
        ExecutionScheduler scheduler,
        FixtureMappingManager caseFixtureMappings) =>
        HeadlessCaseRunner.Run(this, explicitOption, messageBus, constructorArguments,
            aggregator, cancellationTokenSource, parallelMode, scheduler, caseFixtureMappings);
}

internal static class HeadlessCaseRunner
{
    public static ValueTask<RunSummary> Run(
        IXunitTestCase testCase,
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        ParallelMode parallelMode,
        ExecutionScheduler scheduler,
        FixtureMappingManager caseFixtureMappings)
    {
        var tests = aggregator.RunAsync(testCase.CreateTests, []).GetAwaiter().GetResult();
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessCaseRunner).Assembly);

        // Keep the xUnit worker occupied while the test runs on Avalonia's UI thread.
        var summary = Task.Run(() => session.Dispatch(
            () => XunitTestCaseRunner.Instance.Run(
                testCase, tests, messageBus, aggregator, cancellationTokenSource,
                parallelMode, scheduler, testCase.TestCaseDisplayName, testCase.SkipReason,
                explicitOption, constructorArguments, caseFixtureMappings).AsTask(),
            cancellationTokenSource.Token)).GetAwaiter().GetResult();

        return new ValueTask<RunSummary>(summary);
    }
}
