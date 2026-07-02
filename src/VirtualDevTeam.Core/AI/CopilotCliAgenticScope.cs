using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace VirtualDevTeam.Core.AI;

/// <summary>
/// Builds the env-scrub + sandbox-directory scaffolding for a single agentic
/// copilot session (<c>p3-real-sandbox</c>). Producing a scope is pure data —
/// materialization (directory creation, gitconfig write) happens when the caller
/// invokes <see cref="Prepare"/>. Scopes are intended to be one-shot per session.
///
/// <para>
/// The sandbox is <b>process-level</b>, not OS-level: it scrubs credential- and
/// home-leaking env vars, reroutes the HOME / APPDATA / XDG / git-global files
/// into throwaway dirs under the worktree, and removes (does not just blank)
/// every CLI-secret env var we know about. Real OS containment (AppContainer /
/// container / Hyper-V isolation) is a separate, deferred phase. See plan §1.
/// </para>
///
/// <para>
/// Windows note: <c>HOME</c>, <c>USERPROFILE</c>, <c>HOMEDRIVE</c>, <c>HOMEPATH</c>,
/// <c>APPDATA</c>, <c>LOCALAPPDATA</c>, <c>XDG_CONFIG_HOME</c>,
/// <c>XDG_CACHE_HOME</c>, <c>GIT_CONFIG_GLOBAL</c>, <c>GIT_CONFIG_NOSYSTEM</c>,
/// <c>GIT_TERMINAL_PROMPT</c>, <c>GCM_INTERACTIVE</c> are all set.
/// </para>
/// </summary>
public sealed class CopilotCliAgenticScope
{
    /// <summary>Worktree path (used as the process CWD and sandbox root).</summary>
    public string WorktreePath { get; }

    /// <summary>Scrubbed / remapped env overrides to merge into ProcessStartInfo.Environment.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; }

    /// <summary>Absolute path of the per-session sandbox gitconfig file.</summary>
    public string SandboxGitconfigPath { get; }

    /// <summary>Sandbox HOME root under the worktree.</summary>
    public string SandboxHomePath { get; }

    private CopilotCliAgenticScope(
        string worktreePath,
        IReadOnlyDictionary<string, string?> env,
        string sandboxGitconfigPath,
        string sandboxHomePath)
    {
        WorktreePath = worktreePath;
        EnvironmentOverrides = env;
        SandboxGitconfigPath = sandboxGitconfigPath;
        SandboxHomePath = sandboxHomePath;
    }

    /// <summary>
    /// Materializes sandbox directories, writes the empty gitconfig, and returns
    /// a scope describing the env overrides to apply to the copilot process.
    /// </summary>
    /// <param name="worktreePath">The agentic candidate's git worktree — used as CWD.</param>
    public static CopilotCliAgenticScope Prepare(string worktreePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        if (!Directory.Exists(worktreePath))
            throw new DirectoryNotFoundException($"Worktree path does not exist: {worktreePath}");

        var sandboxRoot = Path.Combine(worktreePath, ".sandbox");
        var home = Path.Combine(sandboxRoot, "home");
        var xdgConfig = Path.Combine(sandboxRoot, "xdg-config");
        var xdgCache = Path.Combine(sandboxRoot, "xdg-cache");
        var appData = Path.Combine(sandboxRoot, "appdata");
        var localAppData = Path.Combine(sandboxRoot, "localappdata");
        var gitconfig = Path.Combine(sandboxRoot, "gitconfig");

        Directory.CreateDirectory(home);
        Directory.CreateDirectory(xdgConfig);
        Directory.CreateDirectory(xdgCache);
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(localAppData);

        // GIT_CONFIG_GLOBAL expects a file path (not a dir). Write an empty one
        // so git resolves "no global config" without falling back to ~/.gitconfig.
        if (!File.Exists(gitconfig))
            File.WriteAllText(gitconfig, "# VirtualDevTeam sandbox gitconfig — intentionally empty\n");

        var env = BuildEnvironmentOverrides(home, xdgConfig, xdgCache, appData, localAppData, gitconfig);
        return new CopilotCliAgenticScope(worktreePath, env, gitconfig, home);
    }

    /// <summary>
    /// Builds the env override dictionary. Set values remap home / config roots
    /// into the sandbox; null values (in the overrides map) tell the caller to
    /// REMOVE that key from the child process environment. Preserved vars are
    /// simply omitted from this map (and inherit).
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> BuildEnvironmentOverrides(
        string homeDir, string xdgConfig, string xdgCache,
        string appData, string localAppData, string gitconfig)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        // --- Remap home-like roots ---
        env["HOME"] = homeDir;
        env["USERPROFILE"] = homeDir;
        // Split HOME into HOMEDRIVE (e.g. "C:") and HOMEPATH (e.g. "\git\...\home").
        // git on Windows concatenates them to find ~/.gitconfig fallback paths.
        var root = Path.GetPathRoot(homeDir) ?? string.Empty;
        env["HOMEDRIVE"] = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        env["HOMEPATH"] = root.Length > 0
            ? homeDir.Substring(root.Length - (root.EndsWith(Path.DirectorySeparatorChar) ? 1 : 0))
            : homeDir;
        env["APPDATA"] = appData;
        env["LOCALAPPDATA"] = localAppData;
        env["XDG_CONFIG_HOME"] = xdgConfig;
        env["XDG_CACHE_HOME"] = xdgCache;

        // --- Git & credential-manager hardening ---
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_CONFIG_GLOBAL"] = gitconfig;
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GCM_INTERACTIVE"] = "Never";

        // --- Scrub: null values signal "delete this key" ---
        foreach (var key in SecretEnvVars)
            env[key] = null;
        foreach (var prefix in SecretEnvPrefixes)
        {
            foreach (var existing in Environment.GetEnvironmentVariables().Keys)
            {
                if (existing is string k && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    // EXCEPTION: image-gen credentials are intentionally provisioned for the
                    // agent (see IAzureImageAuthProvider.GetEnvironmentForChildProcessAsync).
                    // Without these the agent fabricates fake PNGs via Pillow primitives
                    // instead of calling the real Azure OpenAI image endpoint. Don't scrub them.
                    if (IsPreservedImageGenVar(k)) continue;
                    env[k] = null;
                }
            }
        }

        return env;
    }

    /// <summary>
    /// Returns true for env vars that are intentionally provisioned for agentic image
    /// generation (e.g. <c>AZURE_OPENAI_IMAGE_ENDPOINT</c>) and must NOT be stripped by
    /// the <c>AZURE_*</c> prefix scrub. Match is case-insensitive on the full name.
    /// </summary>
    internal static bool IsPreservedImageGenVar(string name)
        => name.StartsWith("AZURE_OPENAI_IMAGE_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Exact env vars to scrub. Deletion (not overwrite) is important — a child
    /// process can distinguish missing from empty for some ask-pass helpers.
    /// </summary>
    internal static readonly string[] SecretEnvVars =
    {
        // Git / GitHub auth
        "GIT_ASKPASS", "SSH_ASKPASS", "SSH_AUTH_SOCK",
        "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN",
        // AI providers
        "OPENAI_API_KEY", "ANTHROPIC_API_KEY",
        "COPILOT_API_KEY", "COPILOT_GITHUB_TOKEN",
        "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN",
        // Cloud provider credentials
        "KUBECONFIG",
        "GOOGLE_APPLICATION_CREDENTIALS",
        // Package registries
        "NPM_TOKEN", "NODE_AUTH_TOKEN", "YARN_NPM_AUTH_TOKEN",
        "PYPI_TOKEN", "TWINE_USERNAME", "TWINE_PASSWORD",
        // Chat / telemetry
        "SLACK_TOKEN",
    };

    /// <summary>
    /// Prefixes of env-var name families to scrub. Families covered:
    /// Azure credentials (AZURE_*), Git Credential Manager (GCM_*),
    /// AWS credentials (AWS_*), Google Cloud (GOOGLE_* / GCP_*),
    /// Docker client auth (DOCKER_*), GPG agent (GPG_*), and pip index auth (PIP_*).
    /// </summary>
    internal static readonly string[] SecretEnvPrefixes =
    {
        "AZURE_", "GCM_",
        "AWS_",
        "GOOGLE_", "GCP_",
        "DOCKER_",
        "GPG_",
        "PIP_",
    };
}
