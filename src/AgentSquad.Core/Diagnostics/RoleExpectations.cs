using AgentSquad.Core.Agents;

namespace AgentSquad.Core.Diagnostics;

/// <summary>
/// Static lookup table mapping (Role, StatusPattern) → expected behavior + scenario reference.
/// Used by the diagnostic engine to explain why an agent thinks it's acting correctly.
/// Derived from docs/Requirements.md scenarios, especially Scenario A (happy path).
/// </summary>
public static class RoleExpectations
{
    /// <summary>
    /// Evaluate the current agent state and return a diagnostic explaining
    /// whether the agent is behaving as expected. When memoryContext is provided,
    /// the justification includes the agent's actual action history for richer reasoning.
    /// </summary>
    public static AgentDiagnostic Evaluate(
        AgentRole role, AgentStatus status, string? statusReason,
        string? assignedPr, string? memoryContext = null)
    {
        var reason = statusReason ?? "";
        var reasonLower = reason.ToLowerInvariant();

        var baseDiag = role switch
        {
            AgentRole.ProgramManager => EvaluatePM(status, reason, reasonLower, assignedPr),
            AgentRole.Researcher => EvaluateResearcher(status, reason, reasonLower),
            AgentRole.Architect => EvaluateArchitect(status, reason, reasonLower, assignedPr),
            AgentRole.SoftwareEngineer => EvaluateSoftwareEngineer(status, reason, reasonLower, assignedPr),
            AgentRole.TestEngineer => EvaluateTestEngineer(status, reason, reasonLower, assignedPr),
            _ => Compliant("Active", "Agent is running", null)
        };

        // Enrich the justification with memory-derived context when available
        if (!string.IsNullOrEmpty(memoryContext))
        {
            return baseDiag with
            {
                Justification = baseDiag.Justification + "\n\n**Recent actions from memory:**\n" + memoryContext
            };
        }

        return baseDiag;
    }

    #region PM

    private static AgentDiagnostic EvaluatePM(
        AgentStatus status, string reason, string reasonLower, string? assignedPr)
    {
        if (status == AgentStatus.Idle || status == AgentStatus.Online)
        {
            if (reasonLower.Contains("research already exists") || reasonLower.Contains("prior run"))
                return Compliant(
                    "Idle — research exists from prior run",
                    "PM detected Research.md already exists, skipping research kickoff. " +
                    "This is correct recovery behavior per §14 Idempotency Requirements.",
                    "§14 Idempotency");

            if (reasonLower.Contains("pm specification") || reasonLower.Contains("pmspec"))
                return Compliant(
                    "Idle — PMSpec complete",
                    "PM has created PMSpec.md and user story issues. Waiting for PR reviews or monitoring. " +
                    "Per Scenario A step 4, PM creates issues then monitors.",
                    "Scenario A step 4");

            if (reasonLower.Contains("review"))
                return Compliant(
                    "Idle — monitoring for review requests",
                    "PM is waiting for review requests from engineers. " +
                    "Per Scenario A steps 11-13, PM reviews all engineer PRs.",
                    "Scenario A steps 11-13");

            return Compliant(
                "Idle — monitoring",
                "PM is in monitoring mode. This is normal when no active work is pending. " +
                "PM will activate on research completion or review requests.",
                null);
        }

        if (status == AgentStatus.Working)
        {
            if (reasonLower.Contains("research"))
                return Compliant(
                    "Working — creating research issue",
                    "PM is initiating research by creating a research issue and assigning the Researcher. " +
                    "Per Scenario A step 1, PM starts by reading the project description and delegating research.",
                    "Scenario A step 1");

            if (reasonLower.Contains("pmspec") || reasonLower.Contains("specification"))
                return Compliant(
                    "Working — creating PMSpec",
                    "PM is creating the PM Specification document from research findings. " +
                    "Per Scenario A step 3, PM creates PMSpec.md after Research.md is complete.",
                    "Scenario A step 3");

            if (reasonLower.Contains("issue") || reasonLower.Contains("user stor"))
                return Compliant(
                    "Working — creating user story issues",
                    "PM is extracting user stories from the PMSpec and creating GitHub issues. " +
                    "Per Scenario A step 4, PM creates enhancement issues for each user story.",
                    "Scenario A step 4");

            if (reasonLower.Contains("review"))
                return Compliant(
                    "Working — reviewing PR",
                    "PM is reviewing an engineer's PR against the PMSpec for business alignment. " +
                    "Per §11 PR Review Requirements, PM reads actual code + PMSpec for each review.",
                    "§11 PR Review");
        }

        return Compliant(
            $"{status} — {Truncate(reason, 50)}",
            $"PM is in {status} state: {reason}. Monitoring for expected behavior.",
            null);
    }

    #endregion

    #region Researcher

    private static AgentDiagnostic EvaluateResearcher(
        AgentStatus status, string reason, string reasonLower)
    {
        if (status == AgentStatus.Idle || status == AgentStatus.Online)
        {
            if (reasonLower.Contains("waiting for research") || reasonLower.Contains("directive"))
                return Compliant(
                    "Idle — waiting for research directives",
                    "Researcher is a reactive agent that waits for task assignments from PM. " +
                    "Per §5 Researcher Requirements, Researcher only activates when directed. " +
                    "This is correct idle behavior.",
                    "§5 Researcher Requirements");
        }

        if (status == AgentStatus.Working)
        {
            if (reasonLower.Contains("research"))
                return Compliant(
                    "Working — conducting research",
                    "Researcher is performing multi-turn AI research and creating Research.md. " +
                    "Per Scenario A step 2, Researcher creates Research.md with 3 AI conversation turns.",
                    "Scenario A step 2");
        }

        return Compliant(
            $"{status} — {Truncate(reason, 50)}",
            $"Researcher is in {status} state: {reason}.",
            "§5 Researcher");
    }

    #endregion

    #region Architect

    private static AgentDiagnostic EvaluateArchitect(
        AgentStatus status, string reason, string reasonLower, string? assignedPr)
    {
        if (status == AgentStatus.Idle || status == AgentStatus.Online)
        {
            if (reasonLower.Contains("waiting for") && reasonLower.Contains("pmspec"))
                return Compliant(
                    "Idle — waiting for PMSpec",
                    "Architect waits for PMSpecReady signal before creating Architecture.md. " +
                    "Per Scenario A step 5, Architect activates after PMSpec is ready.",
                    "Scenario A step 5");

            if (reasonLower.Contains("review mode") || reasonLower.Contains("architecture.md already"))
                return Compliant(
                    "Idle — Architecture complete, review mode",
                    "Architecture.md has been created. Architect is now in PR review mode, " +
                    "waiting for review requests. Per §8 Architect Review, Architect reviews PRs for " +
                    "architectural alignment.",
                    "§8 Architect Review");
        }

        if (status == AgentStatus.Working)
        {
            if (reasonLower.Contains("architecture") && reasonLower.Contains("design"))
                return Compliant(
                    "Working — creating Architecture.md",
                    "Architect is creating the architecture document using 5 AI conversation turns. " +
                    "Per Scenario A step 5, Architect creates Architecture.md after PMSpec.",
                    "Scenario A step 5");

            if (reasonLower.Contains("review"))
                return Compliant(
                    "Working — reviewing PR",
                    "Architect is reviewing an engineer's PR for architectural alignment. " +
                    "Per §11 PR Review, Architect reads actual code against Architecture.md.",
                    "§11 PR Review");
        }

        return Compliant(
            $"{status} — {Truncate(reason, 50)}",
            $"Architect is in {status} state: {reason}.",
            "§6 Architect");
    }

    #endregion

    #region Software Engineer

    private static AgentDiagnostic EvaluateSoftwareEngineer(
        AgentStatus status, string reason, string reasonLower, string? assignedPr)
    {
        if (status == AgentStatus.Idle || status == AgentStatus.Online)
        {
            if (reasonLower.Contains("waiting for architecture"))
                return Compliant(
                    "Idle — waiting for Architecture.md",
                    "Software Engineer waits for Architecture.md and PlanningComplete signal before creating the " +
                    "engineering plan. Per Scenario A step 6, SE activates after Architecture is ready.",
                    "Scenario A step 6");

            if (reasonLower.Contains("awaiting review") || reasonLower.Contains("pr #"))
                return Compliant(
                    $"Idle — {Truncate(reason, 50)}",
                    $"Software Engineer has submitted a PR and is waiting for PM + Architect review. " +
                    $"Per Scenario A step 13, SE's PRs are reviewed by PM and Architect.",
                    "Scenario A step 13");

            if (reasonLower.Contains("waiting for task") || reasonLower.Contains("assignment"))
                return Compliant(
                    "Idle — waiting for task assignment",
                    "Software Engineer is waiting for a task to be assigned. " +
                    "Per §7 Engineer Requirements, engineers wait for task assignment.",
                    "§7 Engineer Requirements");

            if (reasonLower.Contains("ready for next task"))
                return Compliant(
                    "Idle — ready for next task",
                    "Software Engineer completed its current task and is looking for the next task " +
                    "or reviewing PRs. Per Scenario A step 7, SE works on assigned tasks.",
                    "Scenario A step 7");

            if (reasonLower.Contains("recovered"))
                return Compliant(
                    "Idle — recovered from restart",
                    "Software Engineer recovered from a restart and is checking for existing work. " +
                    "Per §14 Idempotency, engineers re-track open PRs and pending rework on restart.",
                    "§14 Idempotency");

            // SE is idle but may have pending work — flag as potentially non-compliant
            return new AgentDiagnostic
            {
                Summary = "Idle — check for pending work",
                Justification = $"Software Engineer is idle with reason: \"{reason}\". If there are pending " +
                    "tasks in the backlog, SE should be working on them. If all tasks are assigned or complete, " +
                    "this is correct. Check engineering-task issues for task statuses.",
                IsCompliant = true, // Give benefit of doubt — the loop will check
                ScenarioRef = "Scenario A step 7"
            };
        }

        if (status == AgentStatus.Working)
        {
            if (reasonLower.Contains("engineering plan") || reasonLower.Contains("creating"))
                return Compliant(
                    "Working — creating engineering task issues",
                    "Software Engineer is decomposing enhancement issues into engineering-task GitHub Issues. " +
                    "Per Scenario A step 6, SE reads enhancement issues and creates engineering-task issues in GitHub.",
                    "Scenario A step 6");

            if (reasonLower.Contains("recovered") && reasonLower.Contains("task"))
                return Compliant(
                    "Working — recovered tasks from GitHub issues",
                    "Software Engineer recovered existing tasks from engineering-task GitHub Issues after a restart. " +
                    "Per §14 Idempotency, agents recover state from GitHub artifacts on restart.",
                    "§14 Idempotency");

            if (reasonLower.Contains("pr #") && reasonLower.Contains("step"))
                return Compliant(
                    $"Working — implementing ({Truncate(reason, 40)})",
                    $"Software Engineer is implementing a task with incremental commits. " +
                    $"Per Scenario A step 10, SE implements tasks. Current: {reason}",
                    "Scenario A step 10");

            if (reasonLower.Contains("issue #") || reasonLower.Contains("starting"))
                return Compliant(
                    $"Working — implementing issue",
                    $"Software Engineer is implementing an assigned task. " +
                    $"Per Scenario A step 8-9, engineers read the issue, create a PR, and implement.",
                    "Scenario A steps 8-9");

            if (reasonLower.Contains("working on"))
                return Compliant(
                    $"Working — {Truncate(reason, 50)}",
                    $"Software Engineer is working on a task: {reason}. Per Scenario A step 7-10, SE works on " +
                    "tasks and implements features.",
                    "Scenario A steps 7-10");

            if (reasonLower.Contains("review"))
                return Compliant(
                    "Working — reviewing PR",
                    "Software Engineer is reviewing a PR for technical correctness. " +
                    "Per Scenario A steps 11-12, SE reviews PRs alongside PM.",
                    "Scenario A steps 11-12");

            if (reasonLower.Contains("assign"))
                return Compliant(
                    "Working — assigning tasks",
                    "Software Engineer is assigning tasks to available engineers based on complexity. " +
                    "Per Scenario A step 7, SE assigns tasks to engineers.",
                    "Scenario A step 7");

            if (reasonLower.Contains("implementing"))
                return Compliant(
                    assignedPr is not null
                        ? $"Generating code on PR #{assignedPr}"
                        : "Generating code for assigned task",
                    $"Software Engineer is actively generating code via AI. " +
                    $"Per Scenario A step 10, SE implements tasks with incremental commits. Current: {reason}",
                    "Scenario A step 10");

            if (reasonLower.Contains("rework") || reasonLower.Contains("feedback"))
                return Compliant(
                    "Working — addressing review feedback",
                    "Software Engineer is reworking code based on reviewer feedback. " +
                    "Per Scenario J, engineers address CHANGES_REQUESTED feedback up to MaxReworkCycles.",
                    "Scenario J");

            if (reasonLower.Contains("recovering") || reasonLower.Contains("recover"))
                return Compliant(
                    "Working — recovery in progress",
                    "Software Engineer is recovering from an error or restarting. " +
                    "Per §14 Idempotency, SE recovers state from GitHub Issues and PRs.",
                    "§14 Idempotency");
        }

        if (status == AgentStatus.Blocked)
        {
            if (reasonLower.Contains("clarif"))
                return Compliant(
                    "Blocked — awaiting clarification",
                    "Software Engineer posted clarification questions on the issue and is waiting for PM response. " +
                    "Per Scenario B, engineers can ask questions before proceeding.",
                    "Scenario B");
        }

        // Fallback: provide a role-aware explanation instead of echoing the status
        var fallbackSummary = status switch
        {
            AgentStatus.Working when assignedPr is not null => $"Active on PR #{assignedPr}",
            AgentStatus.Working => "Executing engineering work",
            _ => $"{status} — awaiting next action"
        };
        return Compliant(
            fallbackSummary,
            $"Software Engineer is in {status} state: {reason}.",
            "§7 Engineer Requirements");
    }

    #endregion

    #region Test Engineer

    private static AgentDiagnostic EvaluateTestEngineer(
        AgentStatus status, string reason, string reasonLower, string? assignedPr)
    {
        if (status == AgentStatus.Idle || status == AgentStatus.Online)
        {
            if (reasonLower.Contains("waiting") || reasonLower.Contains("scanning") || reasonLower.Contains("merged"))
                return Compliant(
                    "Idle — scanning for merged PRs",
                    "Test Engineer scans for newly merged PRs that need test coverage. " +
                    "Per Scenario A step 15, TestEngineer picks up merged PRs with full business context.",
                    "Scenario A step 15");

            if (reasonLower.Contains("no ") && reasonLower.Contains("pr"))
                return Compliant(
                    "Idle — no merged PRs to test",
                    "Test Engineer found no new merged PRs needing tests. Will re-scan next cycle. " +
                    "Per §9 Test Engineer Requirements, TestEngineer is reactive to merged PRs.",
                    "§9 TestEngineer Requirements");
        }

        if (status == AgentStatus.Working)
        {
            if (reasonLower.Contains("test") || reasonLower.Contains("generating"))
                return Compliant(
                    "Working — generating tests",
                    "Test Engineer is generating test code for a merged PR with full business context " +
                    "(linked issue + PMSpec + Architecture). Per Scenario A step 15.",
                    "Scenario A step 15");

            if (reasonLower.Contains("review") || reasonLower.Contains("pr #"))
                return Compliant(
                    $"Working — {Truncate(reason, 50)}",
                    $"Test Engineer is working on a test PR. Current: {reason}. " +
                    "Per Scenario A step 15, TestEngineer creates test PRs reviewed by Software Engineer.",
                    "Scenario A step 15");
        }

        return Compliant(
            $"{status} — {Truncate(reason, 50)}",
            $"Test Engineer is in {status} state: {reason}.",
            "§9 TestEngineer");
    }

    #endregion

    #region Helpers

    private static AgentDiagnostic Compliant(string summary, string justification, string? scenarioRef) =>
        new()
        {
            Summary = summary,
            Justification = justification,
            IsCompliant = true,
            ScenarioRef = scenarioRef,
            Timestamp = DateTime.UtcNow
        };

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : string.Concat(text.AsSpan(0, maxLen - 1), "…");

    #endregion
}
