# VirtualDevTeam — Key Differentiators

## Multi-Agent Collaboration
- **Specialized Agent Roles**: 7+ agent types (PM, Researcher, Architect, Software Engineer, Test Engineer, Specialist/SME, Custom) each with domain expertise and focused responsibilities
- **Agent-to-Agent Communication**: Agents interact with each other, ask clarifying questions, and coordinate work through an in-process message bus + dev platform artifacts
- **Dynamic Specialist Spawning**: Specialist engineers (Frontend, Backend, Game Engine, Infra, etc.) are dynamically created based on project needs with slot-based concurrency limits
- **Team View**: Visual representation of the entire virtual team — who's working on what, their status, and how they collaborate. Future vision includes 2D/3D interactive team environments (office, house, etc.)

## Quality & Human Oversight
- **Human Gating at Every Step**: Configurable approval gates at every phase of development — from project planning through final PR merge. Nothing gets through without human sign-off if you want it
- **Pre-PR Clarifying Questions**: Before each PR, agents generate up to 10 clarifying questions with proposed answers to validate assumptions and align with human expectations
- **Project-Level Clarifying Questions**: At project start, the PM generates questions to ensure agents understand the plan and fill potential detail gaps
- **Peer Reviews from Specialized Agents**: Focused code reviews from agents with specific expertise (architecture alignment, PM business review, test coverage, adversarial rework critique)
- **Scenario-Based Validation**: Scenarios are built from the project plan and used throughout the entire build to ensure each PR meets the original vision and passes scenario tests
- **OWASP Top 10 Security Auditing**: A dedicated Security Auditor agent reviews changes against the OWASP Top 10, classifies findings by category (e.g., A03 Injection, A07 Authentication Failures), and blocks risky PRs from merging until they're resolved

## Agentic Frameworks & Strategy
- **Multi-Agent Strategy Framework**: Multiple candidates work on the same task simultaneously using different approaches. A judge evaluates which candidate performed best
- **Agentic Framework Judging**: AI judges score candidates 0-10 on acceptance criteria, design quality, and readability to pick winners
- **Visual Progress Tracking**: Playwright generates screenshots, GIFs, and videos during development so you can see visual progress as agents work

## Developer Experience
- **No PAT Tokens Required**: Uses already-logged-in tools (GitHub CLI, Copilot CLI, Azure CLI) for authentication. No need to generate, manage, or rotate Personal Access Tokens
- **Multiple Dev Platform Support**: Native integration with both GitHub and Azure DevOps — work items, PRs, code reviews, and repository operations work on either platform
- **Native Work Item Generation**: Automatically creates engineering task issues/work items on your chosen platform with full dependency tracking and wave-based execution
- **Vertical-Slice Delivery**: Work is cut into self-contained vertical slices — each task delivers a feature end-to-end (UI + logic + styling + tests) that's demonstrable on its own, instead of "horizontal" layer-tasks, so parallel engineers don't collide or leave broken half-built states between merges
- **Clone & Run Preview at Any Stage**: Preview Build feature lets you clone and run the project at any point during development to see current state
- **Director CLI**: Chat with a director agent that manages the entire buildout, or chat directly with individual agents to guide their work

## Visibility & Monitoring
- **Full E2E Phase Timeline**: Complete visibility of the entire development lifecycle from Research through Completion, with drill-down into each phase and individual PR
- **Reasoning & Decision Trail**: See exactly WHY agents made each decision — every reasoning step, every gate check, every choice is logged and browsable
- **Dev Platform Wrapper**: Stay in the dashboard to browse code, documents, issues, and PRs without switching to GitHub/ADO — Repository page brings everything into one view
- **Detailed Metrics & Logs**: Comprehensive operational metrics, usage statistics, and performance tracking for monitoring agent effectiveness
- **Health Monitor & Flow Monitor**: Automated watchdog systems detect stuck agents, phase mismatches, and deadlocks — with escalation ladders for resolution
- **Testing Artifacts in One Spot**: Browse all screenshots, videos, and Playwright traces from agent workspaces in a single Testing page

## Customization
- **Fully Customizable Agent Roles**: Define custom agent roles with specific capabilities, model tiers, and behaviors
- **Editable Prompt Templates**: ~100 prompt templates in editable Markdown files — customize how every agent thinks and communicates
- **MCP Server Integration**: Model Context Protocol server exposes workspace operations to external AI tools
- **Image Generation**: OpenAI integration for generating design mockups, art assets, and visual content during development
- **Configurable Model Tiers**: Map agent roles to different AI model providers and tiers (premium/standard/budget/local) based on quality requirements

## Future Vision
- **2D/3D Team Environments**: Interactive virtual workspaces where you can monitor and interact with your agent team in customizable environments (office, house, etc.)
