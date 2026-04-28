# SME Agents Plan: Dynamic Agent Creation with MCP Server Tooling

> **Purpose:** Comprehensive plan for enabling AgentSquad agents to dynamically create new specialized agents ("SME Agents") with configurable MCP server toolsets, external knowledge sources, and custom role definitions. This plan builds on the existing AgentSquad architecture and extends it to support runtime agent composition.
>
> **Author:** Ben Humphrey (@azurenerd) with Copilot CLI
> **Status:** ✅ IMPLEMENTED — SME agents, MCP server registry, knowledge pipeline, and PM-driven team composition are all live in the current codebase. This document is retained as historical design reference.
> **Last Updated:** 2026-04-15

---

## Table of Contents

1. [Vision & Goals](#1-vision--goals)
2. [Current State Analysis](#2-current-state-analysis)
3. [Architecture Overview](#3-architecture-overview)
4. [MCP Server Registry & Lifecycle](#4-mcp-server-registry--lifecycle)
5. [SME Agent Role Definition Model](#5-sme-agent-role-definition-model)
6. [Dynamic Agent Creation Flow](#6-dynamic-agent-creation-flow)
7. [Knowledge Ingestion Pipeline](#7-knowledge-ingestion-pipeline)
8. [MCP Server Integration with Copilot CLI](#8-mcp-server-integration-with-copilot-cli)
9. [Configuration Schema](#9-configuration-schema)
10. [Agent-Creates-Agent Workflow](#10-agent-creates-agent-workflow)
11. [Security & Sandboxing](#11-security--sandboxing)
12. [Dashboard Integration](#12-dashboard-integration)
13. [Testing Strategy](#13-testing-strategy)
14. [Implementation Phases](#14-implementation-phases)
15. [Open Questions & Decisions](#15-open-questions--decisions)

---

## 1. Vision & Goals

### What We're Building

Enable AgentSquad agents (primarily the PM and SE) to **dynamically define and spawn new agents at runtime** with:

1. **Custom role definitions** — system prompts, behavioral instructions, domain expertise
2. **MCP server toolsets** — each SME agent gets a tailored set of MCP servers providing specialized tools (GitHub, Playwright, database, custom APIs, etc.)
3. **External knowledge sources** — URLs, documents, and APIs that the agent digests to become a subject matter expert in a specific domain
4. **Composable capabilities** — agents can mix and match MCP servers + knowledge to create specialists like "Security Auditor" (GitHub MCP + OWASP knowledge), "UI Tester" (Playwright MCP + design spec knowledge), or "Database Architect" (PostgreSQL MCP + schema docs)

### The Director's Vision: PM-Driven Agent Team Composition

The core vision is that the **PM agent acts as the team director** — it doesn't just manage tasks, it **designs the team** needed for each project. The flow:

1. **PM reads the project description** (the feature change / project scope for the GitHub project)
2. **PM reads Research.md** (produced by the Researcher agent during the Research phase)
3. **PM writes and reads PMSpec.md** (its own business specification synthesizing the above)
4. **PM reviews the catalog of available agents and their capabilities** — both the 7 built-in roles and any previously-defined SME agent templates
5. **PM decides the optimal team composition:**
   - Which built-in agents are needed (and how many of each — e.g., 2 Software Engineers, 1 Software Engineer)
   - Which existing SME templates match the project needs (e.g., "Database Architect" for a data-heavy feature)
   - Whether a **new SME agent role needs to be created** for capabilities not covered by any existing agent
6. **For new SME agents, the PM defines the role** — provides a role description, selects MCP servers from the registry, and specifies websites/documents/APIs for the agent to digest and build domain expertise from
7. **Human gate approval** — before any new SME agent is actually spawned, the human director can review and approve/modify/reject the PM's team composition proposal
8. **PM tracks SME agents** as part of the team — assigns them to tasks, includes their output in the team's workflow, and coordinates their work alongside the built-in agents

This makes agent team composition a **strategic, project-aware decision** rather than a reactive response to individual task complexity. The PM thinks holistically: "What kind of team do I need to deliver this project?" — just like a real PM staffing a team.

### Goals

- **G1**: Agents can create other agents with defined roles and MCP toolsets without human pre-configuration
- **G2**: MCP servers are managed programmatically — started, connected, and cleaned up by the agent runtime
- **G3**: External knowledge links are fetched, summarized, and injected into agent system prompts automatically
- **G4**: The existing 7-agent workflow continues to work unchanged; SME agents are additive
- **G5**: MCP servers that aren't available on the host machine degrade gracefully (warn, skip, not crash)
- **G6**: SME agent definitions can be saved/reused across runs (not just ephemeral)
- **G7**: The PM reads the full document pipeline (project description → Research.md → PMSpec.md) before making agent team composition decisions
- **G8**: The PM evaluates the full catalog of available agents (built-in + SME templates) before deciding to create new ones
- **G9**: Agent team composition decisions are human-gated — the PM proposes, the human director approves

### Non-Goals (for v1)

- Agents creating MCP servers from scratch (writing server code)
- Cross-machine MCP server distribution (all local to the runner host)
- Custom MCP server development (use existing community/official servers)
- A2A (Agent-to-Agent) protocol — we use our existing message bus

---

## 2. Current State Analysis

### What Already Exists

The codebase already has significant infrastructure we can build on:

#### 2.1 MCP Server Name Passthrough (Partial)

**`AgentCallContext.McpServers`** (`src/AgentSquad.Core/AI/AgentCallContext.cs`):
```csharp
// AsyncLocal storage — already flows MCP server names through async context
public static IReadOnlyList<string>? McpServers { get; set; }
```

**`CopilotCliProcessManager`** (`src/AgentSquad.Core/AI/CopilotCliProcessManager.cs`, line ~293):
```csharp
// Already passes MCP server names to copilot CLI as --mcp-server flags
var mcpServers = AgentCallContext.McpServers;
if (mcpServers is { Count: > 0 })
    foreach (var server in mcpServers)
        args.Append($" --mcp-server {server}");
```

**`AgentConfig.McpServers`** (`src/AgentSquad.Core/Configuration/AgentSquadConfig.cs`, line ~100):
```csharp
// Per-role MCP server names already configurable
public List<string> McpServers { get; set; } = new();
```

**Gap:** This only passes server *names* to the CLI. It assumes the MCP servers are already configured on the host machine (in the user's `copilot` CLI config). There's no programmatic server lifecycle management.

#### 2.2 Role Context & Knowledge Links (Working)

**`RoleContextProvider`** (`src/AgentSquad.Core/AI/RoleContextProvider.cs`):
- Already fetches URLs, extracts text, truncates to budget, caches per-role
- Injects `[ROLE CUSTOMIZATION]` and `[ROLE KNOWLEDGE]` sections into system prompts
- Has `GetMcpServers(role)` method returning configured server names

**`AgentConfig`** in `AgentSquadConfig.cs`:
```csharp
public string? RoleDescription { get; set; }        // Custom system prompt addition
public List<string> KnowledgeLinks { get; set; }     // URLs to digest for SME context
public List<string> McpServers { get; set; }         // MCP server names
```

**Gap:** Works well for the 7 predefined roles. No mechanism for dynamically defining NEW roles at runtime.

#### 2.3 Dynamic Agent Spawning (Working)

**`AgentSpawnManager`** (`src/AgentSquad.Orchestrator/AgentSpawnManager.cs`):
- Already handles runtime agent creation with slot reservation, gate checks, DI-based instantiation
- Supports SE roles with configurable pool sizes

**`AgentFactory`** (`src/AgentSquad.Agents/AgentFactory.cs`):
- Switch-based factory mapping `AgentRole` → concrete agent class
- Uses `ActivatorUtilities.CreateInstance<T>()` for DI injection

**Gap:** Factory only creates the 7 hardcoded roles. No `AgentRole.Custom` or `AgentRole.SME` type. No way to pass a custom role definition to the factory.

#### 2.4 Copilot CLI MCP Support

The `copilot` CLI (v1.0.18+) supports MCP servers via:
```bash
copilot --mcp-server github --mcp-server playwright
```

These server names must correspond to MCP servers configured in the CLI's settings file (`~/.config/github-copilot/mcp.json` or equivalent). The CLI handles the server lifecycle (start/stop) when it has configuration for them.

**This is the key architectural constraint** — see Section 4 for options.

---

## 3. Architecture Overview

### High-Level Design

```
┌─────────────────────────────────────────────────────────────┐
│                     AgentSquad Runner                        │
│                                                             │
│  ┌──────────┐    ┌─────────────────┐    ┌──────────────┐   │
│  │ PM Agent  │───▶│ SME Agent Defn  │───▶│ AgentSpawn   │   │
│  │ SE Agent  │    │ Service         │    │ Manager      │   │
│  └──────────┘    └────────┬────────┘    └──────┬───────┘   │
│                           │                     │           │
│                           ▼                     ▼           │
│                  ┌─────────────────┐    ┌──────────────┐   │
│                  │ MCP Server      │    │ SME Agent    │   │
│                  │ Registry        │    │ (AgentBase)  │   │
│                  └────────┬────────┘    └──────┬───────┘   │
│                           │                     │           │
│                           ▼                     ▼           │
│                  ┌─────────────────┐    ┌──────────────┐   │
│                  │ MCP Server      │    │ Copilot CLI  │   │
│                  │ Lifecycle Mgr   │    │ + MCP flags  │   │
│                  └────────┬────────┘    └──────────────┘   │
│                           │                                 │
│                           ▼                                 │
│                  ┌─────────────────┐                        │
│                  │ MCP Server      │                        │
│                  │ Processes       │                        │
│                  │ (stdio/HTTP)    │                        │
│                  └─────────────────┘                        │
└─────────────────────────────────────────────────────────────┘
```

### Key Components (New or Modified)

| Component | Type | Purpose |
|-----------|------|---------|
| `SMEAgentDefinition` | Record | Defines a custom agent: role prompt, MCP servers, knowledge links, model tier |
| `SMEAgentDefinitionService` | Service | CRUD for SME definitions, persistence, AI-assisted definition generation |
| `McpServerRegistry` | Singleton | Registry of available MCP servers: name → launch config (command, args, URL) |
| `McpServerLifecycleManager` | Singleton | Starts/stops/monitors MCP server processes per-agent |
| `SmeAgent` | Class (extends AgentBase) | Generic agent that executes custom role behavior using AI + MCP tools |
| `AgentRole.Custom` | Enum value | New role type for dynamically-defined agents |
| Modified `AgentFactory` | | Extended to handle `AgentRole.Custom` with SME definitions |
| Modified `AgentSpawnManager` | | Extended to accept `SMEAgentDefinition` in spawn requests |

---

## 4. MCP Server Registry & Lifecycle

### The Core Problem

MCP servers need to be **runnable** on the host machine. There are three approaches, and the right choice depends on how AgentSquad's Copilot CLI integration works:

### Option A: Copilot CLI-Managed MCP Servers (Recommended for v1)

**How it works:** The `copilot` CLI already manages MCP server lifecycles when servers are in its config. We write MCP server definitions to the CLI's config file programmatically, then pass `--mcp-server <name>` flags as we already do.

**Pros:**
- Minimal new code — leverages existing `CopilotCliProcessManager` passthrough
- CLI handles stdio transport, process lifecycle, reconnection
- Battle-tested MCP client in the CLI

**Cons:**
- Requires writing to the CLI's config file (side effect on the host)
- All agents share the CLI's MCP config (no per-agent isolation)
- Depends on CLI's MCP config format (may change between versions)

**Implementation:**
```csharp
public class CopilotMcpConfigManager
{
    // Reads/writes to ~/.config/github-copilot/mcp.json (or platform equivalent)
    
    public async Task EnsureServerConfiguredAsync(McpServerDefinition server)
    {
        var config = await ReadCliMcpConfigAsync();
        if (!config.ContainsKey(server.Name))
        {
            config[server.Name] = new {
                command = server.Command,       // e.g., "npx"
                args = server.Args,             // e.g., ["-y", "@anthropic/mcp-server-fetch"]
                env = server.EnvironmentVars    // e.g., { "GITHUB_TOKEN": "..." }
            };
            await WriteCliMcpConfigAsync(config);
        }
    }
    
    public async Task RemoveServerConfigAsync(string serverName) { ... }
}
```

### Option B: Direct MCP Client (Future / v2)

**How it works:** Use the official `ModelContextProtocol` NuGet package (v1.0, released March 2026) to manage MCP servers directly from the .NET process. Agents get an `IMcpClient` that connects to servers over stdio or HTTP.

**Pros:**
- Full control over server lifecycle per-agent
- No dependency on CLI config files
- Can use MCP tools directly in C# (not just through CLI prompts)
- Per-agent server isolation

**Cons:**
- Significant new code — must build MCP tool → Semantic Kernel function bridge
- More complex process management (per-agent server pools)
- Tool results need to flow back into the AI conversation

**NuGet packages:**
```xml
<PackageReference Include="ModelContextProtocol" Version="1.*" />
<!-- or minimal: -->
<PackageReference Include="ModelContextProtocol.Core" Version="1.*" />
```

**Implementation sketch:**
```csharp
public class McpServerLifecycleManager : IHostedService, IDisposable
{
    // Per-agent MCP client sessions
    private readonly ConcurrentDictionary<string, List<IMcpClient>> _agentClients = new();
    
    public async Task<IMcpClient> StartServerForAgentAsync(
        string agentId, McpServerDefinition server, CancellationToken ct)
    {
        var client = await McpClientFactory.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = server.Command,
                Arguments = server.Args,
                EnvironmentVariables = server.EnvironmentVars
            }), ct);
        
        _agentClients.AddOrUpdate(agentId, 
            _ => new List<IMcpClient> { client },
            (_, list) => { list.Add(client); return list; });
        
        return client;
    }
    
    public async Task<IReadOnlyList<McpClientTool>> GetToolsForAgentAsync(string agentId)
    {
        // Aggregate tools from all MCP clients for this agent
        var tools = new List<McpClientTool>();
        if (_agentClients.TryGetValue(agentId, out var clients))
            foreach (var client in clients)
                tools.AddRange(await client.ListToolsAsync());
        return tools;
    }
    
    public async Task StopServersForAgentAsync(string agentId)
    {
        if (_agentClients.TryRemove(agentId, out var clients))
            foreach (var client in clients)
                await client.DisposeAsync();
    }
}
```

### Option C: Hybrid (Recommended Long-Term)

Start with **Option A** (CLI-managed) for fast iteration, then migrate to **Option B** (direct client) when deeper tool integration is needed. The `McpServerRegistry` abstraction supports both:

```csharp
public interface IMcpServerProvider
{
    Task EnsureAvailableAsync(string serverName, CancellationToken ct);
    Task<bool> IsAvailableAsync(string serverName, CancellationToken ct);
    Task CleanupAsync(string serverName, CancellationToken ct);
}

// v1: writes to CLI config
public class CopilotCliMcpProvider : IMcpServerProvider { ... }

// v2: manages processes directly via MCP SDK
public class DirectMcpProvider : IMcpServerProvider { ... }
```

### MCP Server Registry

A registry of known/available MCP servers, populated from config:

```csharp
public record McpServerDefinition
{
    public required string Name { get; init; }          // "github", "playwright", "fetch"
    public required string Description { get; init; }   // Human-readable description
    public required string Command { get; init; }       // "npx", "uvx", "docker"
    public List<string> Args { get; init; } = [];       // ["-y", "@modelcontextprotocol/server-github"]
    public Dictionary<string, string> Env { get; init; } = new();  // Environment variables
    public McpTransportType Transport { get; init; } = McpTransportType.Stdio;
    public string? Url { get; init; }                   // For HTTP/SSE transport
    public List<string> RequiredRuntimes { get; init; } = [];  // ["node", "python", "docker"]
    public List<string> ProvidedCapabilities { get; init; } = [];  // ["github-issues", "github-prs", "github-code-search"]
}

public enum McpTransportType { Stdio, Http, Sse }
```

---

## 5. SME Agent Role Definition Model

### SMEAgentDefinition Record

This is the core data model that describes a dynamically-created agent:

```csharp
public record SMEAgentDefinition
{
    /// Unique identifier for this definition (reusable across runs)
    public required string DefinitionId { get; init; }
    
    /// Human-readable name for the role (e.g., "Security Auditor", "API Specialist")
    public required string RoleName { get; init; }
    
    /// Detailed system prompt defining the agent's expertise and behavior
    public required string SystemPrompt { get; init; }
    
    /// MCP server names from the registry that this agent needs
    public List<string> McpServers { get; init; } = [];
    
    /// External URLs to fetch and digest as domain knowledge
    public List<string> KnowledgeLinks { get; init; } = [];
    
    /// Model tier override (premium/standard/budget) — defaults to standard
    public string ModelTier { get; init; } = "standard";
    
    /// What kinds of tasks this agent can handle
    public List<string> Capabilities { get; init; } = [];
    
    /// Maximum concurrent instances of this SME type
    public int MaxInstances { get; init; } = 1;
    
    /// How the agent participates in the workflow
    public SmeWorkflowMode WorkflowMode { get; init; } = SmeWorkflowMode.OnDemand;
    
    /// Message types this agent should subscribe to
    public List<string> SubscribeTo { get; init; } = [];
    
    /// Agent that created this definition (for audit/lineage)
    public string? CreatedByAgentId { get; init; }
    
    /// When this definition was created
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public enum SmeWorkflowMode
{
    /// Agent is spawned on-demand when a task matches its capabilities
    OnDemand,
    
    /// Agent runs continuously in a polling loop (like existing agents)
    Continuous,
    
    /// Agent runs once for a specific task then shuts down
    OneShot
}
```

### Predefined SME Templates

Ship with a library of ready-to-use SME definitions:

```json
{
  "SmeTemplates": {
    "security-auditor": {
      "RoleName": "Security Auditor",
      "SystemPrompt": "You are a security auditor specializing in OWASP Top 10 vulnerabilities...",
      "McpServers": ["github", "fetch"],
      "KnowledgeLinks": [
        "https://owasp.org/www-project-top-ten/",
        "https://cheatsheetseries.owasp.org/"
      ],
      "Capabilities": ["security-review", "vulnerability-scan", "dependency-audit"],
      "ModelTier": "premium"
    },
    "api-designer": {
      "RoleName": "API Designer",
      "SystemPrompt": "You are an API design specialist following REST/OpenAPI best practices...",
      "McpServers": ["fetch"],
      "KnowledgeLinks": [
        "https://swagger.io/specification/",
        "https://restfulapi.net/"
      ],
      "Capabilities": ["api-design", "openapi-spec", "endpoint-review"],
      "ModelTier": "standard"
    },
    "ui-tester": {
      "RoleName": "UI Test Specialist",
      "SystemPrompt": "You are a UI testing specialist using Playwright for end-to-end testing...",
      "McpServers": ["playwright", "fetch"],
      "KnowledgeLinks": [
        "https://playwright.dev/docs/intro"
      ],
      "Capabilities": ["ui-testing", "accessibility-audit", "visual-regression"],
      "ModelTier": "standard"
    },
    "database-specialist": {
      "RoleName": "Database Specialist",
      "SystemPrompt": "You are a database architecture specialist...",
      "McpServers": ["postgres", "fetch"],
      "KnowledgeLinks": [],
      "Capabilities": ["schema-design", "query-optimization", "migration-planning"],
      "ModelTier": "standard"
    }
  }
}
```

---

## 6. Dynamic Agent Creation Flow

### Sequence: PM/SE Creates an SME Agent

```
1. PM/SE identifies need for specialized expertise
   (e.g., "This task requires deep security knowledge")

2. PM/SE constructs or selects an SMEAgentDefinition
   Option A: Select from SmeTemplates ("security-auditor")
   Option B: AI generates a custom definition based on task requirements
   Option C: Human provides via dashboard Configuration page

3. PM/SE sends SpawnSmeAgentMessage to AgentSpawnManager
   {
     Definition: <SMEAgentDefinition>,
     AssignToTask: <IssueNumber or null>,
     Justification: "Task #45 requires OWASP security review"
   }

4. AgentSpawnManager validates:
   a. MaxInstances not exceeded for this DefinitionId
   b. Required MCP servers available (or can be made available)
   c. Required runtimes installed on host (node, python, etc.)
   d. Human gate check (if configured)

5. McpServerProvider ensures all MCP servers are available:
   a. For each server in Definition.McpServers:
      - Check McpServerRegistry for launch config
      - Ensure runtime prerequisites exist
      - Configure server (Option A: write CLI config, Option B: start process)
   b. If any server unavailable: log warning, continue without it (graceful degradation)

6. AgentFactory creates SmeAgent:
   a. AgentIdentity { Role=Custom, DisplayName=Definition.RoleName, ... }
   b. SmeAgent(identity, definition, messageBus, githubService, ...)
   c. Agent's OnInitializeAsync:
      - RoleContextProvider fetches and caches KnowledgeLinks
      - AgentCallContext.McpServers = Definition.McpServers
      - Subscribe to configured message types

7. Agent enters its workflow loop:
   - OnDemand: waits for task assignment, executes, reports results
   - Continuous: polls for work matching its capabilities
   - OneShot: executes assigned task, then self-terminates

8. On agent shutdown:
   - Unsubscribe from message bus
   - McpServerProvider cleans up per-agent servers (if Option B)
   - Unregister from AgentRegistry
```

### Modified AgentFactory

```csharp
public class AgentFactory : IAgentFactory
{
    // Existing: Create by role
    public IAgent Create(AgentRole role, AgentIdentity identity) { ... }
    
    // New: Create custom SME agent
    public IAgent CreateSme(AgentIdentity identity, SMEAgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        
        return ActivatorUtilities.CreateInstance<SmeAgent>(
            _serviceProvider, identity, definition);
    }
}
```

### Modified AgentSpawnManager

```csharp
public class AgentSpawnManager
{
    // Existing
    public async Task<AgentIdentity?> SpawnAgentAsync(AgentRole role, CancellationToken ct) { ... }
    
    // New
    public async Task<AgentIdentity?> SpawnSmeAgentAsync(
        SMEAgentDefinition definition, 
        int? assignToIssue = null,
        CancellationToken ct = default)
    {
        // Check instance limits
        var existingCount = _registry.GetAllAgents()
            .Count(a => a.Identity.Role == AgentRole.Custom 
                     && a is SmeAgent sme 
                     && sme.Definition.DefinitionId == definition.DefinitionId);
        
        if (existingCount >= definition.MaxInstances)
        {
            _logger.LogWarning("SME agent {RoleName} at max instances ({Max})", 
                definition.RoleName, definition.MaxInstances);
            return null;
        }
        
        // Ensure MCP servers available
        await _mcpProvider.EnsureServersAvailableAsync(definition.McpServers, ct);
        
        // Human gate
        await _gateCheck.WaitForGateAsync(GateIds.AgentTeamComposition, ...);
        
        // Create identity
        var identity = new AgentIdentity
        {
            Id = $"sme-{definition.DefinitionId}-{Guid.NewGuid():N[..8]}",
            DisplayName = definition.RoleName,
            Role = AgentRole.Custom,
            ModelTier = definition.ModelTier,
            Rank = existingCount,
            CreatedAt = DateTime.UtcNow
        };
        
        // Create, register, initialize, start
        var agent = _agentFactory.CreateSme(identity, definition);
        await _registry.RegisterAsync(agent, ct);
        await agent.InitializeAsync(ct);
        _ = Task.Run(() => agent.StartAsync(ct), ct);
        
        // Optionally assign to a task
        if (assignToIssue.HasValue)
        {
            await _messageBus.PublishAsync(new IssueAssignmentMessage
            {
                FromAgentId = "system",
                ToAgentId = identity.Id,
                IssueNumber = assignToIssue.Value
            });
        }
        
        return identity;
    }
}
```

---

## 7. Knowledge Ingestion Pipeline

### Current State

`RoleContextProvider` already handles:
- Fetching URLs with size limits (50KB per link)
- HTML tag stripping
- Truncation to budget (2500 chars total)
- Caching per role

### Enhancements for SME Agents

#### 7.1 Richer Content Extraction

The current `TruncateToSummary` is basic (strip HTML, take first 800 chars). For SME knowledge, we need:

```csharp
public interface IContentExtractor
{
    Task<string> ExtractAsync(string url, string rawContent, CancellationToken ct);
}

// Implementations:
public class MarkdownExtractor : IContentExtractor { ... }     // .md files
public class HtmlExtractor : IContentExtractor { ... }         // Web pages (Readability algorithm)
public class PdfExtractor : IContentExtractor { ... }          // PDF documents
public class OpenApiExtractor : IContentExtractor { ... }      // Swagger/OpenAPI specs
public class GitHubRepoExtractor : IContentExtractor { ... }   // README + tree structure
```

#### 7.2 AI-Powered Summarization

For long documents, use a budget-model AI call to produce focused summaries:

```csharp
public class AiKnowledgeSummarizer
{
    public async Task<string> SummarizeForRoleAsync(
        string rawContent, 
        string roleName, 
        string rolePrompt,
        CancellationToken ct)
    {
        var prompt = $"""
            Summarize the following content for an AI agent whose role is: {roleName}
            Focus on information directly relevant to their expertise: {rolePrompt[..200]}
            Produce a concise summary (max 1000 chars) with key facts, patterns, and rules.
            
            Content:
            {rawContent[..Math.Min(rawContent.Length, 8000)]}
            """;
        
        var kernel = _modelRegistry.GetKernel("budget");
        // ... execute and return summary
    }
}
```

#### 7.3 Knowledge Budget Management

```csharp
public class KnowledgeBudget
{
    // Per-model-tier token budgets for knowledge context
    public static int GetMaxKnowledgeChars(string modelTier) => modelTier switch
    {
        "premium" => 8000,    // Opus-class models handle large context well
        "standard" => 4000,   // Sonnet-class
        "budget" => 2000,     // Mini models need concise context
        _ => 2500
    };
}
```

---

## 8. MCP Server Integration with Copilot CLI

### How the CLI Uses MCP Servers

When `copilot --mcp-server github` is invoked:
1. CLI reads its MCP config file for the "github" server definition
2. CLI starts the server process (e.g., `npx -y @modelcontextprotocol/server-github`)
3. CLI connects via stdio transport
4. CLI discovers available tools from the server
5. During the conversation, the AI model can call these tools
6. CLI translates tool calls → MCP requests → server → responses → back to AI
7. On session end, CLI shuts down the server process

### What We Need to Configure

For each MCP server the agents want to use, the CLI needs a config entry:

```json
// ~/.config/github-copilot/mcp.json (or platform equivalent)
{
  "mcpServers": {
    "github": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-github"],
      "env": {
        "GITHUB_PERSONAL_ACCESS_TOKEN": "<PAT>"
      }
    },
    "fetch": {
      "command": "npx", 
      "args": ["-y", "@anthropic/mcp-server-fetch"]
    },
    "playwright": {
      "command": "npx",
      "args": ["-y", "@playwright/mcp-server"],
      "env": {
        "PLAYWRIGHT_HEADLESS": "true"
      }
    },
    "postgres": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-postgres"],
      "env": {
        "POSTGRES_CONNECTION_STRING": "..."
      }
    },
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/allowed/path"]
    }
  }
}
```

### Config File Management

```csharp
public class CopilotCliMcpConfigManager
{
    private readonly string _configPath;  // Auto-detected per platform
    
    /// Ensures an MCP server is in the CLI's config. Idempotent.
    public async Task EnsureConfiguredAsync(McpServerDefinition server, CancellationToken ct)
    {
        var config = await ReadConfigAsync(ct);
        
        if (config.McpServers.ContainsKey(server.Name))
            return;  // Already configured
            
        config.McpServers[server.Name] = new McpServerConfig
        {
            Command = server.Command,
            Args = server.Args,
            Env = MergeEnvironmentVars(server.Env)  // Inject PAT, etc. from appsettings
        };
        
        await WriteConfigAsync(config, ct);
        _logger.LogInformation("Configured MCP server {Name} in Copilot CLI config", server.Name);
    }
    
    /// Removes servers that were added by AgentSquad (tracks via metadata)
    public async Task CleanupAgentServersAsync(CancellationToken ct) { ... }
    
    /// Returns list of currently configured MCP servers
    public async Task<IReadOnlyList<string>> GetConfiguredServersAsync(CancellationToken ct) { ... }
    
    private Dictionary<string, string> MergeEnvironmentVars(Dictionary<string, string> serverEnv)
    {
        // Auto-inject known secrets from AgentSquad config
        var merged = new Dictionary<string, string>(serverEnv);
        
        if (merged.ContainsKey("GITHUB_PERSONAL_ACCESS_TOKEN") 
            && string.IsNullOrEmpty(merged["GITHUB_PERSONAL_ACCESS_TOKEN"]))
        {
            merged["GITHUB_PERSONAL_ACCESS_TOKEN"] = _config.Project.GitHubToken;
        }
        
        return merged;
    }
}
```

### Graceful Degradation

```csharp
public class McpServerAvailabilityChecker
{
    /// Checks if prerequisites for an MCP server are met
    public async Task<McpAvailability> CheckAsync(McpServerDefinition server, CancellationToken ct)
    {
        // Check runtime
        foreach (var runtime in server.RequiredRuntimes)
        {
            if (!await IsRuntimeAvailableAsync(runtime, ct))
                return McpAvailability.MissingRuntime(runtime);
        }
        
        // Check if command exists
        if (!await IsCommandAvailableAsync(server.Command, ct))
            return McpAvailability.MissingCommand(server.Command);
        
        return McpAvailability.Available;
    }
    
    private async Task<bool> IsRuntimeAvailableAsync(string runtime, CancellationToken ct)
    {
        // Try running "node --version", "python --version", "docker --version", etc.
        try
        {
            var result = await ProcessRunner.RunAsync(runtime, "--version", ct);
            return result.ExitCode == 0;
        }
        catch { return false; }
    }
}
```

---

## 9. Configuration Schema

### appsettings.json Additions

```json
{
  "AgentSquad": {
    "McpServers": {
      "github": {
        "Description": "GitHub API access — issues, PRs, code search",
        "Command": "npx",
        "Args": ["-y", "@modelcontextprotocol/server-github"],
        "Env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "" },
        "RequiredRuntimes": ["node"],
        "ProvidedCapabilities": ["github-issues", "github-prs", "github-code-search", "github-repos"]
      },
      "fetch": {
        "Description": "HTTP fetch — retrieve web pages and APIs",
        "Command": "npx",
        "Args": ["-y", "@anthropic/mcp-server-fetch"],
        "RequiredRuntimes": ["node"],
        "ProvidedCapabilities": ["web-fetch", "api-call"]
      },
      "playwright": {
        "Description": "Browser automation — screenshots, UI testing, web scraping",
        "Command": "npx",
        "Args": ["-y", "@playwright/mcp-server"],
        "Env": { "PLAYWRIGHT_HEADLESS": "true" },
        "RequiredRuntimes": ["node"],
        "ProvidedCapabilities": ["browser-automation", "screenshots", "ui-testing"]
      },
      "filesystem": {
        "Description": "Local filesystem access (sandboxed to allowed paths)",
        "Command": "npx",
        "Args": ["-y", "@modelcontextprotocol/server-filesystem"],
        "RequiredRuntimes": ["node"],
        "ProvidedCapabilities": ["file-read", "file-write", "directory-list"]
      }
    },
    
    "SmeAgents": {
      "Enabled": true,
      "MaxTotalSmeAgents": 5,
      "AllowAgentCreatedDefinitions": true,
      "PersistDefinitions": true,
      "DefinitionsPath": "sme-definitions.json",
      
      "Templates": {
        "security-auditor": {
          "RoleName": "Security Auditor",
          "SystemPrompt": "You are a security auditor specializing in...",
          "McpServers": ["github", "fetch"],
          "KnowledgeLinks": ["https://owasp.org/www-project-top-ten/"],
          "Capabilities": ["security-review", "vulnerability-scan"],
          "ModelTier": "premium",
          "MaxInstances": 1
        }
      }
    },
    
    "Agents": {
      "ProgramManager": {
        "ModelTier": "premium",
        "RoleDescription": "...",
        "KnowledgeLinks": [],
        "McpServers": ["github"]
      }
    }
  }
}
```

### C# Config Classes

```csharp
public class McpServersConfig : Dictionary<string, McpServerDefinition> { }

public class SmeAgentsConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxTotalSmeAgents { get; set; } = 5;
    public bool AllowAgentCreatedDefinitions { get; set; } = true;
    public bool PersistDefinitions { get; set; } = true;
    public string DefinitionsPath { get; set; } = "sme-definitions.json";
    public Dictionary<string, SMEAgentDefinition> Templates { get; set; } = new();
}
```

---

## 10. Agent-Creates-Agent Workflow

### PM as Team Director — The Agent Composition Pipeline

The PM is the **primary agent composition decision-maker**. It follows the existing document pipeline to inform team staffing decisions:

```
Phase 1: Information Gathering (already exists in current workflow)
─────────────────────────────────────────────────────────────────
  Project.Description (from appsettings)
    → Researcher produces Research.md
    → PM produces PMSpec.md (synthesizing project desc + research)

Phase 2: Agent Team Composition (NEW — added after PMSpec is written)
─────────────────────────────────────────────────────────────────
  PM reads PMSpec.md + Research.md + Project.Description
    → PM queries the SME Agent Catalog (built-in roles + saved templates)
    → PM evaluates: "What capabilities does this project need?"
    → PM produces TeamComposition proposal:
       ├── Built-in agents needed (with counts: e.g., 2× SE, 1× SE)
       ├── Existing SME templates to activate (e.g., "database-architect")
       └── New SME agents to create (with role def, MCP servers, knowledge links)
    → Human gate: Director reviews & approves/modifies the proposal
    → Approved agents are spawned by AgentSpawnManager
    → PM updates TeamMembers.md with the full team roster
```

### PM's Agent Composition AI Prompt

When the PM decides team composition, it uses an AI call with this context:

```csharp
public class AgentTeamComposer
{
    public async Task<TeamCompositionProposal> ComposeTeamAsync(
        string projectDescription,
        string researchMd,
        string pmSpecMd,
        IReadOnlyList<AgentCapabilitySummary> builtInAgents,
        IReadOnlyList<SMEAgentDefinition> availableTemplates,
        IReadOnlyList<McpServerDefinition> availableMcpServers,
        CancellationToken ct)
    {
        var prompt = $"""
            You are a Project Manager designing the optimal team for a software project.
            
            ## Project Description
            {projectDescription}
            
            ## Research Findings
            {researchMd}
            
            ## Business Specification (PMSpec)
            {pmSpecMd}
            
            ## Available Built-in Agents
            {FormatBuiltInAgents(builtInAgents)}
            
            ## Available SME Agent Templates
            {FormatTemplates(availableTemplates)}
            
            ## Available MCP Servers (tools that can be given to new agents)
            {FormatMcpServers(availableMcpServers)}
            
            Based on the project requirements, propose the optimal team:
            
            1. **Built-in agents**: Which standard roles are needed and how many of each?
               (PM is always included. Researcher has already completed.)
            2. **Existing SME templates**: Which saved templates match this project's needs?
               Only include templates whose capabilities are actually needed.
            3. **New SME agents to create**: For capabilities not covered by any existing agent,
               define new agent roles. For each:
               - RoleName: concise specialist title
               - SystemPrompt: detailed expertise and behavioral instructions (200-500 words)
               - McpServers: which available MCP servers this agent needs
               - KnowledgeLinks: up to 5 URLs with authoritative reference material
               - Capabilities: 3-5 keyword capabilities
               - ModelTier: "premium" for complex reasoning, "standard" for implementation
               - Justification: why this agent is needed and what gap it fills
            
            Output as JSON matching TeamCompositionProposal schema.
            """;
        
        // ... execute AI call, parse JSON, validate
    }
}
```

### TeamCompositionProposal Model

```csharp
public record TeamCompositionProposal
{
    public required string ProjectSummary { get; init; }
    public required List<BuiltInAgentRequest> BuiltInAgents { get; init; }
    public required List<string> ExistingTemplateIds { get; init; }
    public required List<SMEAgentDefinition> NewSmeAgents { get; init; }
    public required string Rationale { get; init; }
}

public record BuiltInAgentRequest
{
    public required AgentRole Role { get; init; }
    public required int Count { get; init; }
    public string? Justification { get; init; }
}
```

### SE as Reactive Spawner (Complementary Role)

While the PM handles **proactive team design** at the start, the SE retains the ability to **reactively spawn SME agents** during the engineering phases when unexpected needs arise:

```
SE analyzes engineering-task Issue #55: "Implement RBAC with OAuth2 + JWT"
  → AI evaluates: "This task requires deep security expertise beyond standard engineering"
  → SE checks SmeTemplates for matching capabilities: finds "security-auditor"
  → SE sends SpawnSmeAgentMessage { DefinitionId="security-auditor", AssignToIssue=55 }
  → Human gate approval
  → Security Auditor SME agent spawns → reviews the task → provides security-focused guidance
```

### AI-Generated Agent Definitions (Used by Both PM and SE)

For tasks where no template matches, either the PM (during composition) or SE (reactively) can ask AI to design a new agent:

```csharp
public class SmeDefinitionGenerator
{
    public async Task<SMEAgentDefinition> GenerateDefinitionAsync(
        string taskDescription,
        IReadOnlyList<McpServerDefinition> availableServers,
        CancellationToken ct)
    {
        var prompt = $"""
            You are designing a specialized AI agent role. Based on this task:
            
            {taskDescription}
            
            Available MCP servers (tools the agent can use):
            {FormatServerList(availableServers)}
            
            Create a role definition with:
            1. RoleName: A concise title for this specialist
            2. SystemPrompt: Detailed instructions for the agent's expertise and behavior (200-500 words)
            3. McpServers: Which servers from the available list this agent needs
            4. KnowledgeLinks: Up to 3 URLs with authoritative reference material
            5. Capabilities: 3-5 keyword capabilities
            6. ModelTier: "premium" for complex reasoning, "standard" for implementation, "budget" for simple tasks
            
            Output as JSON matching SMEAgentDefinition schema.
            """;
        
        // ... execute AI call, parse JSON, validate
    }
}
```

### Workflow Integration: When Does Team Composition Happen?

The PM's team composition step fits naturally into the existing workflow state machine:

```
Initialization → Research → Architecture → EngineeringPlanning → ...
                    ↓
              PM reads Research.md
              PM writes PMSpec.md
              ──── NEW: PM composes team ────
              PM evaluates agent catalog
              PM proposes team composition
              Human approves composition
              SME agents spawn
              ──── Continue to Architecture ────
```

This happens **after PMSpec.md is written but before Architecture phase begins**, so the Architect has the full team (including SME agents) available when designing the system.

### Message Types

```csharp
/// PM proposes a full team composition
public record TeamCompositionProposalMessage : AgentMessage
{
    public required TeamCompositionProposal Proposal { get; init; }
}

/// Human director approves/modifies the composition
public record TeamCompositionApprovalMessage : AgentMessage
{
    public required TeamCompositionProposal ApprovedProposal { get; init; }
    public List<string> RejectedAgentIds { get; init; } = [];
    public string? DirectorNotes { get; init; }
}

/// SE/PM requests an individual SME agent spawn (reactive)
public record SpawnSmeAgentMessage : AgentMessage
{
    public required string DefinitionId { get; init; }    // Template ID or custom
    public SMEAgentDefinition? CustomDefinition { get; init; }  // For AI-generated definitions
    public int? AssignToIssue { get; init; }
    public string Justification { get; init; } = "";
}

/// SME agent reports its findings/results
public record SmeResultMessage : AgentMessage
{
    public required string DefinitionId { get; init; }
    public required string TaskSummary { get; init; }
    public required string Findings { get; init; }
    public List<string> Recommendations { get; init; } = [];
    public int? RelatedIssueNumber { get; init; }
}
```

---

## 11. Security & Sandboxing

### Concerns

1. **MCP servers have host access** — filesystem, network, processes
2. **Agents can create agents** — potential for runaway spawning
3. **Knowledge links fetch arbitrary URLs** — SSRF risk
4. **Environment variables may contain secrets** — PATs, API keys

### Mitigations

| Concern | Mitigation |
|---------|-----------|
| Host access | Filesystem MCP server sandboxed to specific paths via args; no shell MCP server by default |
| Runaway spawning | `MaxTotalSmeAgents` hard cap (default 5); per-definition `MaxInstances`; human gate on spawn |
| SSRF | Knowledge link fetcher validates URL scheme (https only), blocks private IPs, timeout limits |
| Secret leakage | Env vars for MCP servers pulled from AgentSquad config (not hardcoded in definitions); definitions stored without secrets |
| Untrusted definitions | `AllowAgentCreatedDefinitions` flag; AI-generated definitions validated against schema before use |
| MCP server trust | Only servers from the `McpServers` registry can be used; no arbitrary command execution |

### Allowlist Pattern

```csharp
public class McpServerSecurityPolicy
{
    /// Only servers in the registry can be referenced by SME definitions
    public bool IsServerAllowed(string serverName)
        => _registry.Contains(serverName);
    
    /// Block dangerous MCP servers
    private static readonly HashSet<string> BlockedServers = ["shell", "exec", "terminal"];
    
    /// Validate a complete SME definition
    public ValidationResult ValidateDefinition(SMEAgentDefinition def)
    {
        var errors = new List<string>();
        
        foreach (var server in def.McpServers)
        {
            if (BlockedServers.Contains(server))
                errors.Add($"MCP server '{server}' is blocked by security policy");
            if (!IsServerAllowed(server))
                errors.Add($"MCP server '{server}' is not in the registry");
        }
        
        if (def.KnowledgeLinks.Any(url => !IsUrlSafe(url)))
            errors.Add("Knowledge links must use HTTPS and not target private networks");
        
        if (def.SystemPrompt.Length > 5000)
            errors.Add("System prompt exceeds maximum length (5000 chars)");
            
        return new ValidationResult(errors);
    }
}
```

---

## 12. Dashboard Integration

### New Dashboard Features

#### 12.1 SME Agents Section on Overview Page

- SME agents appear as agent cards alongside the 7 core agents
- Distinguished by a ✨ badge or different card color
- Card shows: role name, MCP servers (as tool badges), knowledge source count, assigned task

#### 12.2 SME Agent Management Page (`/sme-agents`)

- **Template Library**: Browse predefined SME templates, create instances on demand
- **Active SME Agents**: List of running SME agents with status, MCP servers, assigned work
- **Definition Editor**: Create/edit custom SME definitions (role name, prompt, MCP servers, knowledge links)
- **MCP Server Status**: Which servers are available, configured, running

#### 12.3 Configuration Page Additions

- **MCP Servers**: Registry browser showing available servers, prerequisites, status
- **SME Settings**: Enable/disable, max agents, allow AI-generated definitions

### REST API Additions

```
GET  /api/dashboard/sme/templates          — List available SME templates
GET  /api/dashboard/sme/agents             — List active SME agents  
POST /api/dashboard/sme/spawn              — Spawn an SME agent from template/definition
POST /api/dashboard/sme/definitions        — Save a custom SME definition
GET  /api/dashboard/mcp/servers            — List MCP server registry
GET  /api/dashboard/mcp/servers/{name}/status — Check server availability
```

---

## 13. Testing Strategy

### Unit Tests

| Test | What It Validates |
|------|------------------|
| `McpServerRegistry_ReturnsConfiguredServers` | Registry loads from config correctly |
| `SmeAgentDefinition_ValidatesSchema` | Rejects invalid definitions (missing fields, blocked servers) |
| `McpSecurityPolicy_BlocksDangerousServers` | Shell/exec servers rejected |
| `McpSecurityPolicy_AllowsRegisteredServers` | Known servers pass validation |
| `SmeDefinitionGenerator_ProducesValidJson` | AI output parses to valid definition |
| `KnowledgeBudget_RespectsModelTierLimits` | Per-tier character budgets enforced |
| `AgentSpawnManager_RespectsMaxSmeInstances` | Doesn't exceed per-definition limits |
| `AgentSpawnManager_RespectsGlobalSmeCap` | Doesn't exceed total SME agent cap |

### Integration Tests

| Test | What It Validates |
|------|------------------|
| `SmeAgent_InitializesWithMcpServers` | MCP servers flow to AgentCallContext |
| `SmeAgent_IngestsKnowledgeLinks` | Knowledge fetched, cached, injected into prompts |
| `SmeAgent_ReceivesTaskAssignment` | Message bus delivers work to SME agent |
| `SmeAgent_ReportsResults` | SME results published back via bus |
| `CopilotMcpConfig_WritesAndReads` | CLI config file managed correctly |
| `FullSpawnLifecycle_CreateRunShutdown` | End-to-end SME agent lifecycle |

### Scenario Tests

| Scenario | Flow |
|----------|------|
| SE spawns security auditor for RBAC task | SE → SpawnSmeAgent → security-auditor starts → reviews code → posts findings on Issue |
| PM spawns API designer for spec review | PM → SpawnSmeAgent → api-designer starts → reviews endpoints → comments on PR |
| MCP server unavailable (no Node.js) | Spawn attempt → availability check fails → graceful degradation → agent runs without MCP tools |
| Max SME agents reached | 6th spawn request → rejected → SE logs warning → falls back to standard engineer |

---

## 14. Implementation Phases

### Phase 1: Foundation — MCP Server Registry & Config (Core)

**Goal:** Infrastructure for managing MCP server definitions and availability.

- Add `McpServerDefinition` record to `AgentSquad.Core`
- Add `McpServersConfig` to `AgentSquadConfig`
- Create `McpServerRegistry` singleton (loads from config, provides lookup)
- Create `McpServerAvailabilityChecker` (validates runtimes/commands)
- Add `McpServers` section to `appsettings.template.json`
- Unit tests for registry and availability checker

### Phase 2: SME Agent Definition Model (Core)

**Goal:** Data model for defining custom agent roles.

- Add `SMEAgentDefinition` record
- Add `SmeAgentsConfig` to `AgentSquadConfig`
- Add `McpServerSecurityPolicy` with validation
- Add predefined templates to config
- Create `SMEAgentDefinitionService` (CRUD, persistence to JSON file)
- Unit tests for definition validation and security policy

### Phase 3: SmeAgent Implementation (Agents)

**Goal:** A generic agent class that executes custom role behavior.

- Add `AgentRole.Custom` enum value
- Create `SmeAgent` class extending `AgentBase`
  - Constructor takes `SMEAgentDefinition`
  - `OnInitializeAsync`: sets up MCP servers, fetches knowledge, subscribes to messages
  - `RunAgentLoopAsync`: behavior varies by `WorkflowMode` (OnDemand/Continuous/OneShot)
  - Integrates with existing review pipeline when applicable
- Extend `AgentFactory` with `CreateSme()` method
- Extend `AgentSpawnManager` with `SpawnSmeAgentAsync()` method

### Phase 4: Copilot CLI MCP Config Management (Core/AI)

**Goal:** Programmatically manage MCP server entries in the Copilot CLI config.

- Create `CopilotCliMcpConfigManager`
  - Auto-detect config file path per platform
  - Read/write MCP server entries
  - Secret injection from AgentSquad config
  - Cleanup on shutdown
- Integrate with `McpServerRegistry` — auto-configure servers before agent spawn
- Integration tests with mock config file

### Phase 5: Knowledge Pipeline Enhancement (Core/AI)

**Goal:** Richer content extraction and AI-powered summarization for knowledge links.

- Add `IContentExtractor` interface with implementations (HTML, Markdown, OpenAPI)
- Add `AiKnowledgeSummarizer` for long-content summarization
- Add `KnowledgeBudget` per-model-tier limits
- Upgrade `RoleContextProvider` to use new extractors
- Tests for each extractor type

### Phase 6: PM Team Composition Pipeline (Agents/Orchestrator)

**Goal:** PM proactively designs the full agent team after reading project documents.

- Add `AgentTeamComposer` service — takes project desc, Research.md, PMSpec.md, catalog → outputs `TeamCompositionProposal`
- Add `AgentCapabilitySummary` model for representing built-in agent capabilities
- Add `TeamCompositionProposal` and `TeamCompositionApprovalMessage` message types
- Add PM workflow step: after writing PMSpec.md, PM evaluates agent catalog and proposes team
- Add human gate: team composition proposal displayed in dashboard for director approval
- Add `TeamComposition.md` output document listing the approved team with rationale
- Wire into `WorkflowStateMachine`: team composition gate between Research/PMSpec and Architecture phases
- PM tracks SME agents in TeamMembers.md alongside built-in agents

### Phase 7: SE Reactive Agent Spawning (Agents/Orchestrator)

**Goal:** SE can reactively spawn SME agents during engineering phases when unexpected needs arise.

- Add `SpawnSmeAgentMessage` and `SmeResultMessage` message types
- Add `SmeDefinitionGenerator` for AI-generated definitions
- SE integration: evaluate tasks for SME needs, request spawns (with human gate)
- Wire into existing workflow (SME agents can participate in review pipeline)

### Phase 8: Dashboard Integration (Dashboard)

**Goal:** UI for managing and monitoring SME agents.

- Add SME badge/indicator to agent cards on Overview page
- Create `/sme-agents` page (template browser, active agents, definition editor)
- Add MCP server status to Configuration page
- Add team composition proposal review UI (director approves/rejects PM's proposal)
- REST API endpoints for SME management
- Wire into both embedded and standalone dashboard modes

### Phase 9: Polish & Hardening

**Goal:** Production readiness.

- Graceful degradation when MCP servers fail mid-conversation
- SME agent error recovery (restart failed servers, retry knowledge fetch)
- Metrics: SME agent spawn count, MCP server usage, knowledge fetch success rate
- Documentation: update Session.md, Requirements.md, LessonsLearned.md
- End-to-end scenario testing

---

## 15. Open Questions & Decisions

### Architecture Decisions Needed

| # | Question | Options | Recommendation |
|---|----------|---------|---------------|
| 1 | MCP server lifecycle management | A: CLI-managed (write config), B: Direct SDK, C: Hybrid | **C (Hybrid)** — Start with A, migrate to B |
| 2 | Where to persist SME definitions | JSON file, SQLite, GitHub repo file | **JSON file** (simple, human-editable, git-friendly) |
| 3 | Should SME agents participate in the review pipeline? | Yes (as reviewers), No (advisory only), Configurable | **Configurable** per definition |
| 4 | Can SME agents create other SME agents? | Yes (recursive), No (only PM/SE), Depth-limited | **No for v1** — only PM/SE can spawn |
| 5 | How do SME agents get GitHub access? | Shared PAT, Per-agent tokens, MCP GitHub server only | **MCP GitHub server** (cleanest separation) |
| 6 | Should MCP servers run per-agent or shared? | Per-agent (isolation), Shared (efficiency) | **Shared for v1** (CLI manages lifecycle) |
| 7 | Knowledge link refresh frequency | Once at init, Periodic, On-demand | **Once at init** (v1), periodic for v2 |
| 8 | Copilot CLI config file location | Auto-detect, Configurable, Isolated copy | **Auto-detect** with configurable override |

### Runtime Prerequisites

For the MCP server ecosystem to work, the runner host needs:

| Runtime | Required For | Check Command |
|---------|-------------|---------------|
| **Node.js 18+** | Most MCP servers (npx-based) | `node --version` |
| **Python 3.10+** | Python-based MCP servers (uvx) | `python --version` |
| **Docker** | Containerized MCP servers | `docker --version` |
| **Copilot CLI 1.0.18+** | CLI-managed MCP (Option A) | `copilot --version` |

### Risk Assessment

| Risk | Impact | Likelihood | Mitigation |
|------|--------|-----------|-----------|
| CLI MCP config format changes | High — breaks server management | Medium | Abstract behind `IMcpServerProvider`, version-check CLI |
| MCP server process leaks | Medium — orphaned processes | Medium | Process tracking, cleanup on shutdown, health checks |
| Knowledge link fetch failures | Low — agent runs without knowledge | High | Graceful degradation, cached fallbacks |
| Runaway SME agent spawning | High — resource exhaustion | Low | Hard caps, human gates, rate limiting |
| AI generates bad definitions | Medium — useless agents | Medium | Schema validation, security policy, human review gate |

---

*This plan is intended as a starting point for implementation. Each phase should be broken into specific work items with acceptance criteria before coding begins.*
