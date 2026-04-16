# Setup Guide

Complete guide to installing, configuring, and running AgentSquad.

## Table of Contents

- [System Requirements](#system-requirements)
- [Installing Prerequisites](#installing-prerequisites)
- [API Key Setup](#api-key-setup)
- [Ollama Setup for Local Models](#ollama-setup-for-local-models)
- [Configuration Walkthrough](#configuration-walkthrough)
- [First Run](#first-run)
- [Troubleshooting](#troubleshooting)

---

## System Requirements

| Requirement | Minimum | Recommended |
|-------------|---------|-------------|
| **OS** | Windows 10+, macOS 12+, Linux (x64/ARM64) | Any modern OS |
| **.NET SDK** | 8.0 | 8.0 (latest patch) |
| **RAM** | 4 GB | 8 GB (16 GB if running Ollama locally) |
| **Disk** | 500 MB | 2 GB (includes Ollama model storage) |
| **Network** | Internet access for API calls | Stable broadband |

> **Note:** If you plan to use Ollama for local model inference, you'll need significantly more RAM. Most models require 8–16 GB RAM; larger models (70B+) may need 32 GB+.

## Installing Prerequisites

### .NET 8 SDK

Download and install from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0).

Verify installation:

```bash
dotnet --version
# Should output 8.0.x or later
```

### Git

Ensure Git is installed for repository operations:

```bash
git --version
```

### Build the Solution

```bash
git clone <repository-url>
cd AgentSquad
dotnet restore
dotnet build
```

## API Key Setup

AgentSquad requires at least one AI provider and a GitHub Personal Access Token. You can mix and match providers across model tiers.

### GitHub Personal Access Token (Required)

1. Go to [github.com/settings/tokens](https://github.com/settings/tokens)
2. Click **Generate new token (classic)**
3. Select the following scopes:
   - `repo` (full control of private repositories)
     - This covers: code, commit statuses, pull requests, issues, and repository contents
4. Copy the generated token (starts with `ghp_`)

> **Important:** The token needs access to the target repository where agents will create PRs, issues, and push files.

### Anthropic API Key (Recommended for Premium Tier)

1. Sign up at [console.anthropic.com](https://console.anthropic.com/)
2. Navigate to **API Keys** → **Create Key**
3. Copy the key (starts with `sk-ant-`)

**Recommended models:**
- `claude-opus-4-20250514` — Premium tier (PM, Architect, Software Engineer)
- `claude-sonnet-4-20250514` — Standard tier (Researcher, Software Engineer, Test Engineer)

### OpenAI API Key

1. Sign up at [platform.openai.com](https://platform.openai.com/)
2. Navigate to **API Keys** → **Create new secret key**
3. Copy the key (starts with `sk-`)

**Recommended models:**
- `gpt-4o` — Standard tier
- `gpt-4o-mini` — Budget tier (Software Engineer)

### Azure OpenAI (Optional)

If using Azure OpenAI instead of direct OpenAI:

1. Create an Azure OpenAI resource in the [Azure Portal](https://portal.azure.com)
2. Deploy your desired models
3. Note your **endpoint URL** and **API key**
4. Set the provider to `"AzureOpenAI"` in configuration

## Ollama Setup for Local Models

Ollama enables free, local model inference — ideal for the budget/local tier or offline development.

### Installation

**Windows:**
Download from [ollama.ai/download](https://ollama.ai/download/windows)

**macOS:**
```bash
brew install ollama
```

**Linux:**
```bash
curl -fsSL https://ollama.ai/install.sh | sh
```

### Start the Ollama Server

```bash
ollama serve
```

The server runs on `http://localhost:11434` by default.

### Pull Recommended Models

```bash
# Good balance of speed and quality for coding tasks
ollama pull deepseek-coder-v2

# Alternative lightweight options
ollama pull codellama:7b
ollama pull mistral
```

### Verify Ollama is Running

```bash
curl http://localhost:11434/api/tags
```

You should see a JSON response listing your downloaded models.

## Configuration Walkthrough

All configuration is in `src/AgentSquad.Runner/appsettings.json` under the `AgentSquad` section.

### Project Settings

```json
{
  "AgentSquad": {
    "Project": {
      "Name": "my-project",
      "Description": "Build a REST API for task management with user auth",
      "GitHubRepo": "myorg/my-project",
      "GitHubToken": "ghp_xxxxxxxxxxxxxxxxxxxx",
      "DefaultBranch": "main"
    }
  }
}
```

| Field | Description |
|-------|-------------|
| `Name` | Project identifier used in logs and the dashboard |
| `Description` | Natural language description of what the agents should build — this drives the PM's planning |
| `GitHubRepo` | Target repository in `owner/repo` format |
| `GitHubToken` | GitHub PAT with `repo` scope |
| `DefaultBranch` | Base branch for PRs (typically `main` or `master`) |

### Model Tiers

Define the AI models available to agents. Each tier maps to a provider and model:

```json
{
  "Models": {
    "premium": {
      "Provider": "Anthropic",
      "Model": "claude-opus-4-20250514",
      "ApiKey": "sk-ant-...",
      "MaxTokensPerRequest": 8192,
      "Temperature": 0.3
    },
    "standard": {
      "Provider": "Anthropic",
      "Model": "claude-sonnet-4-20250514",
      "ApiKey": "sk-ant-...",
      "MaxTokensPerRequest": 4096,
      "Temperature": 0.3
    },
    "budget": {
      "Provider": "OpenAI",
      "Model": "gpt-4o-mini",
      "ApiKey": "sk-...",
      "MaxTokensPerRequest": 4096,
      "Temperature": 0.3
    },
    "local": {
      "Provider": "Ollama",
      "Model": "deepseek-coder-v2",
      "Endpoint": "http://localhost:11434",
      "MaxTokensPerRequest": 4096,
      "Temperature": 0.3
    }
  }
}
```

| Field | Description |
|-------|-------------|
| `Provider` | `Anthropic`, `OpenAI`, `AzureOpenAI`, or `Ollama` |
| `Model` | Model identifier (provider-specific) |
| `ApiKey` | API key for the provider (not needed for Ollama) |
| `Endpoint` | Required for Ollama and Azure OpenAI; the base URL |
| `MaxTokensPerRequest` | Maximum tokens per API call (controls cost and response length) |
| `Temperature` | Creativity parameter (0.0–1.0); lower = more deterministic |

### Agent Configuration

Assign model tiers to each agent role:

```json
{
  "Agents": {
    "ProgramManager":    { "ModelTier": "premium",  "MaxTokensPerCycle": 8192 },
    "Researcher":        { "ModelTier": "standard", "MaxTokensPerCycle": 4096 },
    "Architect":         { "ModelTier": "premium",  "MaxTokensPerCycle": 8192 },
    "SoftwareEngineer": { "ModelTier": "premium",  "MaxTokensPerCycle": 8192 },
    "TestEngineer":      { "ModelTier": "standard", "MaxTokensPerCycle": 4096 },
    "SoftwareEngineerTemplate": { "ModelTier": "standard", "MaxTokensPerCycle": 4096 },
    "SoftwareEngineerTemplate": { "ModelTier": "budget",   "MaxTokensPerCycle": 4096 }
  }
}
```

> **Templates:** Software Engineer and Software Engineer configs are templates — new instances spawned at runtime inherit these settings.

### Operational Limits

```json
{
  "Limits": {
    "MaxAdditionalEngineers": 3,
    "MaxDailyTokenBudget": 1000000,
    "GitHubPollIntervalSeconds": 30,
    "AgentTimeoutMinutes": 15,
    "MaxConcurrentAgents": 10
  }
}
```

| Field | Description | Default |
|-------|-------------|---------|
| `MaxAdditionalEngineers` | Max Software Engineers the PM can spawn beyond core team | `3` |
| `MaxDailyTokenBudget` | Combined daily token limit across all models | `1000000` |
| `GitHubPollIntervalSeconds` | How often agents poll GitHub for updates | `30` |
| `AgentTimeoutMinutes` | Agent is flagged as "stuck" if working longer than this | `15` |
| `MaxConcurrentAgents` | Hard cap on total running agents | `10` |

### Dashboard Settings

```json
{
  "Dashboard": {
    "Port": 5000,
    "SignalREnabled": true
  }
}
```

## First Run

### Option A: Interactive Configuration Wizard

If `appsettings.json` is not configured, the first run launches an interactive wizard:

```bash
cd src/AgentSquad.Runner
dotnet run
```

The wizard walks through:
1. **Project settings** — name, description, GitHub repo URL, token
2. **Model selection** — which providers and models to use
3. **Resource limits** — max engineers, token budgets
4. **Summary** — review and confirm

### Option B: Manual Configuration

1. Copy and edit `appsettings.json` as described in the [Configuration Walkthrough](#configuration-walkthrough)
2. Run the application:

```bash
cd src/AgentSquad.Runner
dotnet run
```

### What Happens on First Run

1. **Bootstrap** — The `AgentSquadWorker` spawns the 5 core agents in order:
   - Program Manager → Researcher → Architect → Software Engineer → Test Engineer
2. **Initialization** — Each agent transitions through: `Requested → Initializing → Online`
3. **Workflow begins** — The workflow state machine starts in `Initialization` phase
4. **PM takes over** — The Program Manager begins orchestrating the team, creating TeamMembers.md, and monitoring progress
5. **Dashboard available** — Open `http://localhost:5000` to watch the team in action

### Verifying the Setup

Check these indicators of a healthy start:

- **Console output** shows ASCII banner and `[AgentSquadWorker] Core agents spawned successfully`
- **Dashboard** at `http://localhost:5000` shows all 5 core agents with `Online` status
- **GitHub repo** has a new `TeamMembers.md` file in the default branch
- **No error logs** related to API authentication or rate limiting

## Troubleshooting

### API Authentication Errors

**Symptom:** `401 Unauthorized` or `403 Forbidden` in logs

**Solutions:**
- Verify API keys are correct and not expired
- For GitHub: ensure the PAT has `repo` scope and access to the target repository
- For Anthropic: check the key starts with `sk-ant-` and the account has credits
- For OpenAI: verify the key starts with `sk-` and billing is active

### Rate Limiting

**Symptom:** `429 Too Many Requests` or `RateLimitExceededException` in logs

**Solutions:**
- Increase `GitHubPollIntervalSeconds` (default 30s; try 60s)
- The built-in `RateLimitManager` handles retries with exponential backoff automatically
- GitHub API limit: 5,000 requests/hour for authenticated requests
- Check remaining quota: the health monitor logs rate limit status

### Model Timeout / Slow Responses

**Symptom:** Agent stuck in `Working` status; health monitor flags it

**Solutions:**
- Increase `AgentTimeoutMinutes` for complex tasks
- Reduce `MaxTokensPerRequest` for faster responses
- For Ollama: ensure the model is loaded (`ollama list`) and the server has enough RAM
- Switch to a faster model for budget/local tier

### Ollama Connection Failures

**Symptom:** `Connection refused` to `localhost:11434`

**Solutions:**
- Verify Ollama is running: `ollama serve`
- Check the endpoint URL in config matches (default `http://localhost:11434`)
- Ensure the model is pulled: `ollama pull deepseek-coder-v2`
- On Windows, check that the Ollama service is started

### GitHub Repository Issues

**Symptom:** Agents fail to create PRs, issues, or files

**Solutions:**
- Verify `GitHubRepo` is in `owner/repo` format (not a full URL)
- Ensure the repository exists and the PAT has access
- Check that the `DefaultBranch` matches the repo's actual default branch
- For private repos, the PAT must belong to a user with write access

### SQLite Database Errors

**Symptom:** `Microsoft.Data.Sqlite` exceptions on startup

**Solutions:**
- The database file (`agentsquad.db`) is created automatically in the working directory
- Ensure the process has write permissions to the current directory
- Delete `agentsquad.db` to reset state (agents will re-initialize from scratch)

### Dashboard Not Loading

**Symptom:** `http://localhost:5000` returns connection refused

**Solutions:**
- Verify the `Dashboard.Port` setting in config
- Check no other process is using port 5000
- Look for Kestrel startup logs in console output
- Try accessing `http://localhost:5000` after all agents have initialized

### Deadlock Detected

**Symptom:** Dashboard shows "Deadlock Detected" banner

**Solutions:**
- The `DeadlockDetector` uses DFS on a wait-for graph to find circular dependencies
- The PM agent will attempt to break deadlocks by re-prioritizing tasks
- If persistent, terminate a blocked agent via the dashboard and restart it
- Check GitHub issues for circular blocker chains between agents
