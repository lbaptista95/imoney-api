namespace Api.Tests;

/// <summary>
/// C82: the test step's canary. A test that always fails, so `ci/assert-test-step-fails.cs`
/// can measure, on the runner itself, that the CI's `dotnet test` exits non-zero when a
/// test fails - whatever configuration might have turned that exit code off.
/// </summary>
/// <remarks>
/// Explicit, so the normal suite never runs it; only `--explicit only` does.
/// </remarks>
public sealed class CanaryTests
{
    /// <summary>The name the canary script filters on. Renaming the test breaks the script, by design.</summary>
    public const string Name = nameof(TestStepCanaryAlwaysFails);

    [Fact(Explicit = true)]
    public void TestStepCanaryAlwaysFails() =>
        Assert.Fail("The canary always fails: ci/assert-test-step-fails.cs needs the test step to report it.");
}
