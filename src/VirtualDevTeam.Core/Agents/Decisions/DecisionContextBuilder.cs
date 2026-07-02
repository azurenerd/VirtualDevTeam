using System.Text;
using VirtualDevTeam.Core.Configuration;

namespace VirtualDevTeam.Core.Agents.Decisions;

/// <summary>
/// Builds decision-awareness context for injection into agent prompts.
/// Provides the {{unanswered_decisions}} variable content and previously-decided context.
/// </summary>
public static class DecisionContextBuilder
{
    /// <summary>
    /// Builds the full unanswered decisions context for injection into agent prompts.
    /// Includes questions that haven't been answered and excludes ones already decided by upstream agents.
    /// </summary>
    /// <param name="unansweredQuestions">All unanswered wizard questions from config.</param>
    /// <param name="decisionLog">The decision log to check for already-decided questions.</param>
    /// <param name="agentRole">Current agent's role (for context in the prompt).</param>
    /// <returns>Formatted markdown section for prompt injection, or empty string if no questions remain.</returns>
    public static string BuildUnansweredDecisionsContext(
        IReadOnlyList<string> unansweredQuestions,
        IDecisionLog? decisionLog = null,
        string? agentRole = null)
    {
        if (unansweredQuestions.Count == 0)
            return "";

        var alreadyDecided = new List<(string Question, string Choice, string Agent)>();
        var stillUndecided = new List<string>();

        foreach (var question in unansweredQuestions)
        {
            if (decisionLog is not null)
            {
                var existing = decisionLog.GetDecisionsBySourceQuestion(question);
                var approved = existing.FirstOrDefault(d =>
                    d.Status == DecisionStatus.Approved || d.Status == DecisionStatus.AutoApproved);

                if (approved is not null)
                {
                    // Extract the choice from the rationale (it contains "Choice: ..." format)
                    var choice = ExtractChoice(approved.Rationale) ?? approved.Title;
                    alreadyDecided.Add((question, choice, approved.AgentDisplayName));
                    continue;
                }
            }

            stillUndecided.Add(question);
        }

        if (stillUndecided.Count == 0 && alreadyDecided.Count == 0)
            return "";

        var sb = new StringBuilder();

        // Show previously decided questions for context
        if (alreadyDecided.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Previously Decided (by upstream agents)");
            sb.AppendLine();
            foreach (var (question, choice, agent) in alreadyDecided)
            {
                sb.AppendLine($"- **Q:** {question}");
                sb.AppendLine($"  **Decided by {agent}:** {choice}");
            }
            sb.AppendLine();
            sb.AppendLine("Use these decisions as constraints — do not contradict them unless you have strong justification.");
            sb.AppendLine();
        }

        // Show undecided questions for the agent to decide
        if (stillUndecided.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Decisions Required (from project clarification)");
            sb.AppendLine();
            sb.AppendLine("The following questions were left unanswered during project setup. For each one relevant");
            sb.AppendLine("to your current task, make a clear decision and report it using this exact format:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("DECISION: [short descriptive title]");
            sb.AppendLine("QUESTION: [which question this answers — copy exactly]");
            sb.AppendLine("CHOICE: [your decision]");
            sb.AppendLine("RATIONALE: [why you chose this — trade-offs considered]");
            sb.AppendLine("IMPACT: [XS|S|M|L|XL]");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("Unanswered questions:");
            for (int i = 0; i < stillUndecided.Count; i++)
                sb.AppendLine($"{i + 1}. {stillUndecided[i]}");
            sb.AppendLine();
            sb.AppendLine("Only decide questions relevant to YOUR role and current task. Skip questions outside your domain.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the ambient decision-tracking instruction for role descriptions.
    /// This is a static instruction appended to all agent role descriptions.
    /// </summary>
    public static string GetAmbientDecisionInstruction()
    {
        return """

## Decision Tracking

When you make a significant decision during your work (technology choice, scope trade-off, 
architecture pattern, prioritization call, feature interpretation), report it using this format:

DECISION: [short descriptive title]
CHOICE: [what you decided]
RATIONALE: [why — trade-offs and alternatives considered]
IMPACT: [XS|S|M|L|XL — how significantly this affects the project]

Only log decisions that a stakeholder might want to review or override — not trivial 
formatting, naming, or obvious implementation choices. Focus on decisions where reasonable 
people might disagree or where the choice significantly shapes the project direction.
""";
    }

    private static string? ExtractChoice(string rationale)
    {
        // Try to find "Choice: ..." pattern in the rationale
        var choiceIdx = rationale.IndexOf("Choice:", StringComparison.OrdinalIgnoreCase);
        if (choiceIdx < 0) return null;

        var afterChoice = rationale[(choiceIdx + 7)..];
        var newlineIdx = afterChoice.IndexOf('\n');
        return newlineIdx > 0
            ? afterChoice[..newlineIdx].Trim()
            : afterChoice.Trim();
    }
}
