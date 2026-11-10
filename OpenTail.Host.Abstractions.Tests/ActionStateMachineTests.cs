using OpenTail.Host.Abstractions;

namespace OpenTail.Host.Abstractions.Tests;

public class ActionStateMachineTests
{
    private static readonly ActionStatus[] All = Enum.GetValues<ActionStatus>();

    private static readonly HashSet<(ActionStatus, ActionStatus)> Legal =
    [
        (ActionStatus.Pending, ActionStatus.Pending), // a partial approval while more are needed
        (ActionStatus.Pending, ActionStatus.Approved),
        (ActionStatus.Pending, ActionStatus.Rejected),
        (ActionStatus.Pending, ActionStatus.ChangesRequested),
        (ActionStatus.Pending, ActionStatus.Expired),
        (ActionStatus.Pending, ActionStatus.Cancelled),
        (ActionStatus.Approved, ActionStatus.Executing),
        (ActionStatus.Approved, ActionStatus.Failed), // refused at the claim by an admission
        (ActionStatus.Executing, ActionStatus.Succeeded),
        (ActionStatus.Executing, ActionStatus.Failed),
    ];

    [Fact]
    public void Every_pair_matches_the_table_when_not_retryable()
    {
        foreach (var from in All)
            foreach (var to in All)
                Assert.Equal(Legal.Contains((from, to)), ActionStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void Retryable_failure_can_be_reapproved_and_nothing_else()
    {
        Assert.True(ActionStateMachine.CanTransition(ActionStatus.Failed, ActionStatus.Approved, retryable: true));
        foreach (var to in All.Where(s => s != ActionStatus.Approved))
            Assert.False(ActionStateMachine.CanTransition(ActionStatus.Failed, to, retryable: true));
        Assert.False(ActionStateMachine.CanTransition(ActionStatus.Failed, ActionStatus.Approved, retryable: false));
    }

    [Theory]
    [InlineData(ActionStatus.Succeeded)]
    [InlineData(ActionStatus.Denied)]
    [InlineData(ActionStatus.Rejected)]
    [InlineData(ActionStatus.ChangesRequested)]
    [InlineData(ActionStatus.Expired)]
    [InlineData(ActionStatus.Cancelled)]
    public void Terminal_states_allow_no_transition(ActionStatus terminal)
    {
        Assert.True(ActionStateMachine.IsTerminal(terminal));
        foreach (var to in All)
            Assert.False(ActionStateMachine.CanTransition(terminal, to, retryable: true));
    }

    [Fact]
    public void Failed_is_terminal_only_when_not_retryable()
    {
        Assert.True(ActionStateMachine.IsTerminal(ActionStatus.Failed));
        Assert.False(ActionStateMachine.IsTerminal(ActionStatus.Failed, retryable: true));
    }

    [Fact]
    public void Only_pending_executing_denied_are_valid_initial_states()
    {
        var valid = All.Where(ActionStateMachine.IsValidInitial).ToHashSet();
        Assert.Equal(
            new HashSet<ActionStatus> { ActionStatus.Pending, ActionStatus.Executing, ActionStatus.Denied },
            valid);
    }

    [Fact]
    public void Only_pending_may_stay_pending()
    {
        foreach (var s in All.Where(s => s != ActionStatus.Pending))
            Assert.False(ActionStateMachine.CanTransition(s, s, retryable: true));
        Assert.True(ActionStateMachine.CanTransition(ActionStatus.Pending, ActionStatus.Pending));
    }
}
