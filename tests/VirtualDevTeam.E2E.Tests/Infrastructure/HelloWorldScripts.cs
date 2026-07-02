namespace VirtualDevTeam.E2E.Tests.Infrastructure;

/// <summary>
/// Factory that creates a <see cref="ScriptedChatCompletionService"/> pre-loaded
/// with the Hello World content scripts for all agent roles.
/// </summary>
public static class HelloWorldScripts
{
    /// <summary>
    /// Create a ScriptedChatCompletionService with pre-built content for all agent roles.
    /// Agents are matched by keywords in their system prompts.
    /// </summary>
    public static ScriptedChatCompletionService CreateForAllAgents()
    {
        var research = E2EContentLoader.LoadResearch();
        var pmSpec = E2EContentLoader.LoadPMSpec();
        var architecture = E2EContentLoader.LoadArchitecture();
        var engineeringPlan = E2EContentLoader.LoadEngineeringPlan();

        var service = new ScriptedChatCompletionService();

        // ── Review responses (MUST come before general agent patterns) ──────

        // Architect PR review — expects JSON with verdict
        service.When("reviewing a PR for architecture alignment",
            """{"verdict":"APPROVED","summary":"Architecture alignment verified. The implementation follows the documented patterns and component boundaries correctly.","riskLevel":"LOW","comments":[]}""");

        // PM PR review — expects VERDICT: APPROVE
        service.When("FINAL review of a PR",
            "Requirements alignment review complete. All acceptance criteria from the user story are met. " +
            "The feature aligns with the PM Spec vision.\n\nVERDICT: APPROVE");

        // PM critique — used during detailed review
        service.When("critique", "No critical issues found. The implementation meets requirements.");

        // TE testability assessment — expects JSON
        service.When("testability", """{"needsTests":false,"rationale":"Simple Hello World app with no complex logic to test."}""");

        // PM completion check — used when PM evaluates if project is complete
        service.When("project completion", "All tasks have been completed. The project is ready for delivery.\n\nVERDICT: COMPLETE");
        service.When("evidence-based assessment", "All deliverables are present and meet acceptance criteria.\n\nVERDICT: COMPLETE");

        // ── Agent document creation patterns ────────────────────────────────

        // Researcher agent — match various prompt patterns
        service.When("senior researcher", research);
        service.When("research analyst", research);
        service.When("technical researcher", research);

        // PM agent — returns PM specification
        service.When("program manager", pmSpec);
        service.When("product manager", pmSpec);

        // Architect agent — returns architecture document
        service.When("software architect", architecture);
        service.When("system architect", architecture);

        // SE agent — engineering plan creation
        service.When("engineering plan", engineeringPlan);
        service.When("task decomposition", engineeringPlan);

        // SE agent — code implementation (returns a simple confirmation)
        service.When("software engineer", "implement",
            "I have implemented the Hello World web application as specified. All files have been created according to the architecture document.");

        // SE agent — general software engineer queries
        service.When("software engineer", engineeringPlan);

        // SE agent — code review responses
        service.When("code review", "The code looks good. No issues found. LGTM.");

        // Test Engineer — test results
        service.When("test engineer", "All tests pass. The application builds and runs correctly. Home page returns HTTP 200 with 'Hello, World!' content.");

        // Judge — evaluation (for non-real-LLM scenarios)
        service.When("judge", @"{
  ""acceptance_criteria_score"": 8,
  ""design_quality_score"": 7,
  ""code_readability_score"": 8,
  ""overall_score"": 7.7,
  ""feedback"": ""The Hello World application meets all acceptance criteria. Clean code structure following ASP.NET Core conventions. Good use of Razor Pages pattern."",
  ""strengths"": [""Clean project structure"", ""Follows .NET conventions"", ""Proper use of layout""],
  ""improvements"": [""Could add unit tests"", ""Consider adding health check endpoint""]
}");

        // Generic fallback for any other prompt — return a helpful default
        service.When(h => true, "Acknowledged. Proceeding with the task as specified.");

        return service;
    }

    /// <summary>
    /// Create a ScriptedChatCompletionService for split-PR mode (multiple tasks, multiple PRs).
    /// Uses the same doc content but returns TASK| formatted engineering plan for multi-task decomposition.
    /// </summary>
    public static ScriptedChatCompletionService CreateForSplitPR()
    {
        var research = E2EContentLoader.LoadResearch();
        var pmSpec = E2EContentLoader.LoadPMSpec();
        var architecture = E2EContentLoader.LoadArchitecture();

        // In split mode, the SE's LLM call for engineering plan returns TASK| lines
        // that get parsed into separate engineering tasks mapped to enhancement issues.
        // Issue numbers are populated dynamically by the SE from existing enhancement issues.
        // We use placeholder issue numbers — the SE maps them from actual issues at runtime.
        var splitEngineeringPlan =
            "TASK|T1|1|Project Foundation & Scaffolding|Create the base ASP.NET Core Razor Pages project with shared layout, configuration, and static assets.|High|NONE|CREATE:.gitignore;CREATE:HelloWorld.sln;CREATE:HelloWorld/HelloWorld.csproj;CREATE:HelloWorld/Program.cs;CREATE:HelloWorld/Pages/Shared/_Layout.cshtml;CREATE:HelloWorld/Pages/_ViewImports.cshtml;CREATE:HelloWorld/Pages/_ViewStart.cshtml;CREATE:HelloWorld/wwwroot/css/site.css;CREATE:HelloWorld/wwwroot/js/site.js;CREATE:HelloWorld/appsettings.json;SHARED:HelloWorld/Program.cs|W0|foundation,fullstack\n" +
            "TASK|T2|1|Implement Home Page|Create the home page displaying Hello World message using the shared layout.|Low|T1|CREATE:HelloWorld/Pages/Index.cshtml;CREATE:HelloWorld/Pages/Index.cshtml.cs|W1|frontend\n" +
            "TASK|T3|1|Implement Privacy Page|Create the Privacy page with standard privacy content.|Low|T1|CREATE:HelloWorld/Pages/Privacy.cshtml;CREATE:HelloWorld/Pages/Privacy.cshtml.cs|W1|frontend";

        var service = new ScriptedChatCompletionService();

        // ── Review responses (MUST come before general agent patterns) ──────
        service.When("reviewing a PR for architecture alignment",
            """{"verdict":"APPROVED","summary":"Architecture alignment verified. The implementation follows the documented patterns and component boundaries correctly.","riskLevel":"LOW","comments":[]}""");

        service.When("FINAL review of a PR",
            "Requirements alignment review complete. All acceptance criteria from the user story are met. " +
            "The feature aligns with the PM Spec vision.\n\nVERDICT: APPROVE");

        service.When("critique", "No critical issues found. The implementation meets requirements.");

        service.When("testability", """{"needsTests":false,"rationale":"Simple Hello World app with no complex logic to test."}""");

        service.When("project completion", "All tasks have been completed. The project is ready for delivery.\n\nVERDICT: COMPLETE");
        service.When("evidence-based assessment", "All deliverables are present and meet acceptance criteria.\n\nVERDICT: COMPLETE");

        // Self-assessment returns the same content unchanged (no refinement needed in tests)
        service.When("assess the quality", splitEngineeringPlan);
        service.When("self-assess", splitEngineeringPlan);

        // ── Agent document creation patterns ────────────────────────────────
        service.When("senior researcher", research);
        service.When("research analyst", research);
        service.When("technical researcher", research);

        // PM user story extraction — MUST come before generic "program manager" pattern
        // The PM calls this to split PMSpec into individual enhancement issues
        service.When("extracting User Stories",
            "TITLE: Create project scaffold with shared layout\nDESCRIPTION:\nAs a developer, I want a base ASP.NET Core Razor Pages project with shared layout so that all pages have consistent navigation and styling.\n\nACCEPTANCE_CRITERIA:\n- [ ] Project builds without errors\n- [ ] Shared layout renders with Bootstrap\n- [ ] Navigation includes Home and Privacy links\n---\nTITLE: Implement Hello World home page\nDESCRIPTION:\nAs a user, I want to see a Hello World message when I visit the home page so that I know the application is working.\n\nACCEPTANCE_CRITERIA:\n- [ ] Home page displays \"Hello, World!\" heading\n- [ ] Page uses the shared layout\n---\nTITLE: Implement Privacy page\nDESCRIPTION:\nAs a user, I want a Privacy page accessible from the navigation so that I can read the privacy policy.\n\nACCEPTANCE_CRITERIA:\n- [ ] Privacy page displays privacy content\n- [ ] Navigation link from home page works\n- [ ] Page uses the shared layout");

        service.When("program manager", pmSpec);
        service.When("product manager", pmSpec);

        service.When("software architect", architecture);
        service.When("system architect", architecture);

        // SE agent — engineering plan creation (returns TASK| lines)
        service.When("engineering plan", splitEngineeringPlan);
        service.When("task decomposition", splitEngineeringPlan);

        // SE agent — code implementation
        service.When("software engineer", "implement",
            "I have implemented the component as specified. All files have been created according to the architecture document.");

        service.When("software engineer", splitEngineeringPlan);

        service.When("code review", "The code looks good. No issues found. LGTM.");

        service.When("test engineer", "All tests pass. The application builds and runs correctly.");

        service.When("judge", @"{
  ""acceptance_criteria_score"": 8,
  ""design_quality_score"": 7,
  ""code_readability_score"": 8,
  ""overall_score"": 7.7,
  ""feedback"": ""The Hello World application meets all acceptance criteria."",
  ""strengths"": [""Clean project structure"", ""Follows .NET conventions""],
  ""improvements"": [""Could add unit tests""]
}");

        service.When(h => true, "Acknowledged. Proceeding with the task as specified.");

        return service;
    }

    /// <summary>
    /// Create a minimal scripted service that just returns "OK" for everything
    /// except the specific role patterns provided.
    /// </summary>
    public static ScriptedChatCompletionService CreateMinimal()
    {
        return new ScriptedChatCompletionService();
    }
}
