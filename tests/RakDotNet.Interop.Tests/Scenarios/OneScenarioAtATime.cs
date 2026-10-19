namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Scenario classes run one at a time: several move megabytes or measure timeouts and send rates, and running them
/// side by side would make each one's timing depend on the others.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OneScenarioAtATime
{
    public const string Name = "Interop scenarios";
}
