# Architecture: Hello World Web Application

## Overview

A minimal ASP.NET Core Razor Pages web application following the standard project template architecture.

## System Architecture

```
┌─────────────────────────────────┐
│         Browser (Client)         │
└──────────┬──────────────────────┘
           │ HTTP/HTTPS
┌──────────▼──────────────────────┐
│       Kestrel Web Server         │
│  ┌─────────────────────────┐    │
│  │   Middleware Pipeline    │    │
│  │  - Static Files          │    │
│  │  - Routing               │    │
│  │  - Error Handling        │    │
│  └────────┬────────────────┘    │
│  ┌────────▼────────────────┐    │
│  │     Razor Pages          │    │
│  │  - Index.cshtml          │    │
│  │  - Privacy.cshtml        │    │
│  │  - Error.cshtml          │    │
│  │  - _Layout.cshtml        │    │
│  └─────────────────────────┘    │
└─────────────────────────────────┘
```

## Technology Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Framework | ASP.NET Core 8.0 | LTS, high performance, cross-platform |
| UI Model | Razor Pages | Simplest web UI model for page-focused apps |
| Web Server | Kestrel | Built-in, no external dependency |
| CSS Framework | Bootstrap 5 | Included in default template |
| Build Tool | dotnet CLI | Standard .NET toolchain |

## Project Structure

```
HelloWorld/
├── HelloWorld.csproj
├── Program.cs                  # App entry point, middleware config
├── Pages/
│   ├── _ViewImports.cshtml     # Tag helper imports
│   ├── _ViewStart.cshtml       # Layout assignment
│   ├── Shared/
│   │   ├── _Layout.cshtml      # Main layout template
│   │   └── _ValidationScriptsPartial.cshtml
│   ├── Index.cshtml            # Home page
│   ├── Index.cshtml.cs         # Home page model
│   ├── Privacy.cshtml          # Privacy page
│   ├── Privacy.cshtml.cs       # Privacy page model
│   └── Error.cshtml            # Error page
├── wwwroot/
│   ├── css/site.css            # Custom styles
│   ├── js/site.js              # Custom scripts
│   ├── lib/                    # Client libraries (Bootstrap, jQuery)
│   └── favicon.ico
└── appsettings.json            # Configuration
```

## Key Components

### Program.cs
- Configures services (Razor Pages)
- Sets up middleware pipeline: static files → routing → endpoints
- Maps Razor Pages endpoints

### Layout (_Layout.cshtml)
- Navigation bar with Home and Privacy links
- Bootstrap responsive container
- Footer with copyright

### Index Page
- Displays "Hello, World!" welcome message
- Uses the shared layout

## Deployment

- **Development**: `dotnet run` serves on localhost:5000/5001
- **Production**: Publish with `dotnet publish -c Release`

## Security Considerations

- HTTPS enforced in production
- Anti-forgery tokens on forms (default)
- Content Security Policy headers (recommended)
