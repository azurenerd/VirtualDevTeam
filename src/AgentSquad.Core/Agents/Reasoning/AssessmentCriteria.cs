namespace AgentSquad.Core.Agents.Reasoning;

/// <summary>
/// Role-specific assessment criteria used by the self-assessment loop.
/// Each role defines what "good enough" means for its output.
/// </summary>
public static class AssessmentCriteria
{
    public const string Researcher = """
        Evaluate the research document against these criteria:
        1. TOPIC COVERAGE: Does the research cover all major topics relevant to the project description? Are there obvious gaps in technology areas that should have been explored?
        2. SPECIFICITY: Does the research name specific tools, libraries, and frameworks with version numbers? Generic advice like "use a modern framework" is a gap.
        3. TRADE-OFFS: Are alternatives discussed with pros and cons for each major recommendation? Single-option recommendations without alternatives are a gap.
        4. ACTIONABILITY: Could an architect and engineers build directly from these recommendations without needing to do their own research? Vague or incomplete recommendations are a gap.
        5. TECH STACK ALIGNMENT: Are all recommendations compatible with the project's chosen technology stack?
        6. SECURITY & INFRASTRUCTURE: Are there recommendations for authentication, hosting, deployment, and monitoring? Missing operational concerns are a gap.
        """;

    public const string ProgramManager = """
        Evaluate the PM specification against these criteria:
        1. USER STORIES: Does every major feature have at least one user story with clear acceptance criteria? User stories without acceptance criteria are a gap.
        2. SCOPE BOUNDARIES: Is there an explicit "Out of Scope" or "Not Included" section? Ambiguous scope is a gap.
        3. NON-FUNCTIONAL REQUIREMENTS: Are NFRs specific and measurable (e.g., "page load under 2 seconds" not "should be fast")? Vague NFRs are a gap.
        4. SUCCESS METRICS: Are there measurable success criteria? "Users should be happy" is not measurable.
        5. RESEARCH ALIGNMENT: Does the spec reference and build upon the research findings? Specs that ignore research are a gap.
        6. COMPLETENESS: Are there sections for Executive Summary, Business Goals, User Stories, Scope, NFRs, Success Metrics, and Constraints?
        """;

    public const string Architect = """
        Evaluate the architecture document against these criteria:
        1. SPEC COVERAGE: Does every user story and feature from the PM specification appear somewhere in the architecture? Components or features missing from the architecture are a gap.
        2. TECHNOLOGY JUSTIFICATION: Are technology choices justified with reasoning (not just listed)? "We use PostgreSQL" without why is a gap.
        3. DATA MODEL: Is there a data model with entities, relationships, and key fields defined? Missing data model is a gap.
        4. API CONTRACTS: Are API endpoints or interfaces defined (not just "REST API")? Missing API definitions are a gap.
        5. NFR COVERAGE: Does the architecture address non-functional requirements from the spec (performance, security, scalability)? Each NFR should map to an architectural decision.
        6. COMPONENT INTERACTIONS: Are component interactions and data flow described? Isolated component descriptions without integration is a gap.
        """;

    public const string SoftwareEngineer = """
        Evaluate the engineering plan against these criteria:
        1. ARCHITECTURE COVERAGE: Does every component from the architecture document have at least one task? Missing components are a gap.
        2. DEPENDENCY ORDER: Are tasks ordered so that dependencies are built before dependents? Tasks referencing unbuilt dependencies are a gap.
        3. TASK SPECIFICITY: Are task descriptions specific enough that an engineer could implement without guessing? "Build the frontend" is too vague — a gap.
        4. DEFINITION OF DONE: Does each task have clear completion criteria? Tasks without acceptance criteria or deliverables are a gap.
        5. TEST STRATEGY: Is there a testing approach defined (what to test, test tiers, coverage expectations)?
        6. INTEGRATION POINTS: Are integration points between tasks identified? Tasks that assume interfaces without defining them are a gap.
        7. PARALLEL SAFETY: Do tasks own distinct files with no overlaps? Are shared files explicitly declared? Tasks with undeclared file overlaps are a gap.
        8. WAVE SCHEDULING: Are tasks assigned to waves (W1, W2, etc.)? Are at least 60% of non-foundation tasks in W1? Low W1 percentage means poor parallelism.
        """;

    /// <summary>Get assessment criteria for a given agent role.</summary>
    public static string? GetForRole(AgentRole role) => role switch
    {
        AgentRole.Researcher => Researcher,
        AgentRole.ProgramManager => ProgramManager,
        AgentRole.Architect => Architect,
        AgentRole.SoftwareEngineer => SoftwareEngineer,
        _ => null, // Engineers and TestEngineer use build/test loops instead
    };
}
