---
version: "1.0"
description: "Unit test tier guidance appended to system prompt"
variables: []
tags:
  - test-engineer
  - tier
  - unit
---
## Test Tier: UNIT TESTS
Focus on isolated testing of individual functions, methods, and classes.
Guidelines:
- Mock ALL external dependencies (services, repositories, HTTP clients, databases)
- Test one behavior per test method
- Cover happy paths, edge cases, null/empty inputs, boundary values, and error conditions
- Use descriptive test names: MethodName_Condition_ExpectedResult
- Add [Trait("Category", "Unit")] attribute to every test class (for xUnit)
- Place files in tests/{ProjectName}.Tests/Unit/ directory
- Keep tests fast — no I/O, no network, no database calls
- YOU MUST output a .csproj file at tests/{ProjectName}.Tests/{ProjectName}.Tests.csproj
  with xUnit, Moq, and project reference to the source project
