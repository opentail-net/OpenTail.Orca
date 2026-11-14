namespace OpenTail.Orca;

/// <summary>
/// Thrown inside a decision's transaction when an AI budget (<c>AiBudget</c>, <c>AiRefusalBudget</c>) is used up by the time the decision is
/// written (batch 10, P6). The transaction rolls back, so nothing is recorded; the queue turns it into an audited refusal.
/// </summary>
internal sealed class AiBudgetExhausted(string message) : Exception(message);
