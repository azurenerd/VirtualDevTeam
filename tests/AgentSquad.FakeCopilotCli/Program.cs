using System.Diagnostics;
using System.Runtime.InteropServices;

// AgentSquad.FakeCopilotCli — a hermetic stand-in for the real `copilot` binary,
// used by StrategyFramework integration tests (p3-test-orphan-cleanup and
// friends). Behavior is keyed by the FAKE_COPILOT_SCENARIO env var so that a
// single built exe can replay every scenario by varying environment only.
//
// Scenarios:
//   streaming-ok        emits N JSONL events, writes a file, exits 0
//   stuck               emits one event, then sleeps 5 minutes (tests stuck detector)
//   toolcap-bomb        emits 2000 tool events as fast as possible (tests tool-call cap)
//   grandchild          spawns a detached grandchild process (tests Job Object kill)
//   out-of-tree-write   tries to write a file to the parent of CWD (tests snapshot)
//   gitconfig-write     runs `git config --global user.name ...` (tests GIT_CONFIG_GLOBAL scrub)
//   exit-nonzero        emits one event, exits with code 42
//   auth-prompt         emits an "auth_failure" event then exits (tests monitor fail-fast in future)
//
// Reads the full prompt from stdin but ignores it (the fake has no LLM). The
// exit code mirrors the scenario's intended outcome so tests can branch on it.

var scenario = Environment.GetEnvironmentVariable("FAKE_COPILOT_SCENARIO")?.Trim().ToLowerInvariant() ?? "streaming-ok";

// Honour --version for CLI-availability probes (CopilotCliProcessManager.StartAsync
// runs `copilot --version` before any real invocation). Exit 0 with a version
// string and no JSONL so the probe looks identical to the real binary.
if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
{
    Console.WriteLine("AgentSquad.FakeCopilotCli 0.0.0");
    return 0;
}

// Read stdin prompt (needed for scripted mode; also prevents pipe blocking).
string? stdinPrompt = null;
var stdinTask = Task.Run(async () =>
{
    try { stdinPrompt = await Console.In.ReadToEndAsync(); } catch { }
});
stdinTask.Wait(TimeSpan.FromSeconds(5));

static void Emit(string type, string? content = null)
{
    var escaped = content is null ? "null" : "\"" + content.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    Console.WriteLine($"{{\"type\":\"{type}\",\"content\":{escaped}}}");
    Console.Out.Flush();
}

try
{
    switch (scenario)
    {
        case "streaming-ok":
        {
            Emit("assistant.message", "Starting task");
            for (var i = 0; i < 5; i++)
            {
                Emit("tool.execution_start", $"op-{i}");
                Thread.Sleep(50);
                Emit("tool.execution_complete", $"op-{i}");
            }
            var marker = Path.Combine(Directory.GetCurrentDirectory(), "fake-cli-marker.txt");
            File.WriteAllText(marker, $"fake-cli ran scenario=streaming-ok at {DateTime.UtcNow:O}\n");
            Emit("assistant.message", "Done");
            return 0;
        }
        case "stuck":
        {
            Emit("assistant.message", "starting then stalling");
            Thread.Sleep(TimeSpan.FromMinutes(5));
            return 0;
        }
        case "toolcap-bomb":
        {
            for (var i = 0; i < 2000; i++)
            {
                Emit("tool.execution_start", $"op-{i}");
            }
            return 0;
        }
        case "grandchild":
        {
            Emit("assistant.message", "spawning grandchild");
            // Spawn a long-sleeping grandchild. On Windows we use `cmd /c timeout`,
            // elsewhere `sh -c sleep`. No detach flag — we WANT the Job Object to
            // reap it when the parent is killed.
            var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new ProcessStartInfo("cmd.exe", "/c timeout /t 300 /nobreak")
                : new ProcessStartInfo("sh", "-c \"sleep 300\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            var gc = Process.Start(psi);
            if (gc is not null)
            {
                // Write grandchild PID to a marker file so tests can assert it died.
                var pidFile = Path.Combine(Directory.GetCurrentDirectory(), "grandchild.pid");
                File.WriteAllText(pidFile, gc.Id.ToString());
                Emit("assistant.message", $"grandchild pid={gc.Id}");
            }
            // Then we ourselves sleep — tests kill us via timeout / cancellation.
            Thread.Sleep(TimeSpan.FromMinutes(5));
            return 0;
        }
        case "out-of-tree-write":
        {
            Emit("assistant.message", "writing outside worktree");
            var parent = Directory.GetParent(Directory.GetCurrentDirectory())?.FullName;
            if (parent is not null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(parent, "evil.txt"), "escape!");
                    Emit("assistant.message", "wrote evil.txt to parent");
                }
                catch (Exception ex)
                {
                    Emit("assistant.message", $"parent write blocked: {ex.Message}");
                }
            }
            return 0;
        }
        case "gitconfig-write":
        {
            Emit("assistant.message", "attempting global gitconfig write");
            var psi = new ProcessStartInfo("git", "config --global user.name FakeAgent")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                using var p = Process.Start(psi)!;
                p.WaitForExit(10_000);
                Emit("assistant.message", $"git exit={p.ExitCode}");
            }
            catch (Exception ex)
            {
                Emit("assistant.message", $"git invocation failed: {ex.Message}");
            }
            return 0;
        }
        case "exit-nonzero":
        {
            Emit("assistant.message", "will exit 42");
            return 42;
        }
        case "auth-prompt":
        {
            Emit("assistant.message", "auth failure coming");
            Emit("auth_failure", "not logged in");
            return 1;
        }
        case "scripted":
        {
            // Reads prompt→response mappings from FAKE_COPILOT_SCRIPT_FILE (JSON).
            // Format: [{ "promptContains": "keyword", "response": "text" }, ...]
            // Matches the first entry whose promptContains is found in stdin.
            // Falls back to a default response if no match.
            var scriptFile = Environment.GetEnvironmentVariable("FAKE_COPILOT_SCRIPT_FILE");
            if (scriptFile is null || !File.Exists(scriptFile))
            {
                Emit("assistant.message", "scripted mode but no script file found");
                return 2;
            }
            var scriptJson = File.ReadAllText(scriptFile);
            var entries = System.Text.Json.JsonSerializer.Deserialize<ScriptEntry[]>(scriptJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? Array.Empty<ScriptEntry>();

            var prompt = stdinPrompt ?? "";
            string response = "No matching script entry found.";
            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.PromptContains) &&
                    prompt.Contains(entry.PromptContains, StringComparison.OrdinalIgnoreCase))
                {
                    response = entry.Response ?? "OK";
                    break;
                }
            }
            Emit("assistant.message", response);
            // Write a marker file so tests can verify the script was used
            var scriptMarker = Path.Combine(Directory.GetCurrentDirectory(), "fake-cli-marker.txt");
            File.WriteAllText(scriptMarker, $"scripted response at {DateTime.UtcNow:O}\n{response}");
            return 0;
        }
        default:
        {
            Console.Error.WriteLine($"FakeCopilotCli: unknown scenario '{scenario}'");
            return 2;
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FakeCopilotCli crashed: {ex}");
    return 3;
}

/// <summary>A prompt→response mapping for scripted mode.</summary>
record ScriptEntry(string? PromptContains, string? Response);
