---
version: "1.0"
description: "Blazor-specific test guidance included when target project uses Blazor"
variables:
  - project_name
tags:
  - test-engineer
  - blazor
---
## BLAZOR PROJECT — SPECIAL INSTRUCTIONS

This is a **Blazor Server** (.NET 8) project. You MUST follow these Blazor-specific testing patterns:

### Required .csproj for Unit/Integration Tests
You MUST output this file: `tests/{{project_name}}.Tests/{{project_name}}.Tests.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="bunit" Version="1.28.9" />
    <PackageReference Include="FluentAssertions" Version="6.12.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.9.0" />
    <PackageReference Include="Moq" Version="4.20.70" />
    <PackageReference Include="xunit" Version="2.7.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.7" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\{{project_name}}.csproj" />
  </ItemGroup>
</Project>
```

### Blazor Component Testing with bUnit
- Use `bunit.TestContext` for rendering Blazor components
- `using Bunit;` namespace (lowercase 'b' in the using, but the NuGet is `bunit`)
- Render components: `var cut = ctx.RenderComponent<MyComponent>()`
- Pass parameters: `ctx.RenderComponent<MyComponent>(p => p.Add(x => x.Title, "Test"))`
- Assert markup: `cut.Markup.Contains("expected text")`
- Find elements: `cut.Find("h1").TextContent`
- Mock services: `ctx.Services.AddSingleton<IMyService>(mockService.Object)`
- Mock JSInterop: `ctx.JSInterop.SetupVoid("methodName")`
- For cascading parameters: `ctx.RenderComponent<CascadingValue<Type>>(p => p.Add(x => x.Value, myValue).AddChildContent<MyComponent>())`

### Service/Model Testing (non-Blazor classes)
- Test service classes, models, and utilities with standard xUnit + Moq
- No bUnit needed for plain C# classes
- Mock `IServiceProvider`, `IConfiguration`, `HttpClient` etc. as needed

### Key Rules
- ALWAYS include the .csproj file in your output
- Use `using {{project_name}};`, `using {{project_name}}.Models;`, `using {{project_name}}.Services;` etc. for namespaces
- Do NOT reference namespaces that don't exist in the project
- Keep test classes focused — one test class per source class/component
