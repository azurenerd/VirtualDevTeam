---
version: "1.0"
description: "Integration test tier guidance appended to system prompt"
variables: []
tags:
  - test-engineer
  - tier
  - integration
---
## Test Tier: INTEGRATION TESTS
Focus on testing component interactions with real or near-real dependencies.
Guidelines:
- Test actual DI container wiring and service resolution
- Test API endpoints end-to-end (request → response)
- Test data access layer with in-memory databases where possible
- Test middleware pipeline behavior
- Add [Trait("Category", "Integration")] attribute to every test class
- Place files in tests/{ProjectName}.Tests/Integration/ directory
- Use WebApplicationFactory for ASP.NET Core integration tests
- Test error handling, validation, and edge cases at API boundaries
- YOU MUST output a .csproj file at tests/{ProjectName}.Tests/{ProjectName}.Tests.csproj
  with xUnit, Moq, Microsoft.AspNetCore.Mvc.Testing, and project reference
