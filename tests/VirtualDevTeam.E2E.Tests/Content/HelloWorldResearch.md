# Research: Hello World ASP.NET Web Application

## Executive Summary

A research analysis for building a simple "Hello World" ASP.NET web application using .NET 8 and Razor Pages.

## Technology Stack Assessment

### ASP.NET Core with Razor Pages
- **Maturity**: Production-ready, GA since .NET Core 2.0
- **Performance**: Kestrel web server provides excellent throughput
- **Simplicity**: Razor Pages is the simplest web UI model in ASP.NET Core
- **Deployment**: Cross-platform (Windows, Linux, macOS)

## Key Findings

1. **Razor Pages**: Best choice for simple page-focused web apps. Each page is a `.cshtml` file with an optional code-behind `.cshtml.cs`.
2. **Project Template**: `dotnet new webapp` generates a working hello world app with basic layout, error handling, and static files.
3. **Middleware Pipeline**: Request → Routing → Static Files → Authorization → Endpoints
4. **Hot Reload**: Supported via `dotnet watch` for rapid development

## Recommendations

- Use `dotnet new webapp` as the project template
- Keep the default Kestrel configuration for simplicity
- Use the built-in layout template with Bootstrap CSS
- Target .NET 8 LTS for long-term support

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Over-engineering | Medium | Keep to Razor Pages, avoid MVC/Blazor complexity |
| Dependency bloat | Low | Stick to framework-provided packages only |

## Conclusion

ASP.NET Core with Razor Pages is the optimal choice for a hello world web application. The built-in template provides everything needed with minimal configuration.
