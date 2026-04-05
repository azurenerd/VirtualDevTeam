using System.Collections.Concurrent;

namespace AgentSquad.Orchestrator;

public class DeadlockDetectedEventArgs : EventArgs
{
    public required List<string> AgentCycle { get; init; }
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
}

public class DeadlockDetector
{
    private readonly ConcurrentDictionary<string, string> _waitGraph = new();

    public event EventHandler<DeadlockDetectedEventArgs>? DeadlockDetected;

    public void RecordWaiting(string waitingAgentId, string waitingForAgentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waitingAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(waitingForAgentId);

        _waitGraph[waitingAgentId] = waitingForAgentId;

        if (HasDeadlock(out var cycle))
        {
            DeadlockDetected?.Invoke(this, new DeadlockDetectedEventArgs
            {
                AgentCycle = cycle!
            });
        }
    }

    public void ClearWaiting(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        _waitGraph.TryRemove(agentId, out _);
    }

    /// <summary>
    /// Detects a cycle in the wait-for graph using DFS (tortoise-and-hare style chain following).
    /// Returns true if a cycle is found and outputs the agent IDs forming the cycle.
    /// </summary>
    public bool HasDeadlock(out List<string>? cycle)
    {
        cycle = null;

        // Snapshot current graph to avoid concurrency issues during traversal
        var snapshot = _waitGraph.ToArray();
        var graph = new Dictionary<string, string>(snapshot.Length);
        foreach (var kvp in snapshot)
        {
            graph[kvp.Key] = kvp.Value;
        }

        var visited = new HashSet<string>();

        foreach (var startNode in graph.Keys)
        {
            if (visited.Contains(startNode))
                continue;

            var path = new List<string>();
            var pathSet = new HashSet<string>();
            var current = startNode;

            while (current is not null && !visited.Contains(current))
            {
                if (pathSet.Contains(current))
                {
                    // Found a cycle — extract it
                    var cycleStart = path.IndexOf(current);
                    cycle = path.GetRange(cycleStart, path.Count - cycleStart);
                    cycle.Add(current); // close the cycle
                    return true;
                }

                path.Add(current);
                pathSet.Add(current);

                graph.TryGetValue(current, out current!);
            }

            // Mark all nodes in this path as fully visited
            foreach (var node in path)
            {
                visited.Add(node);
            }
        }

        return false;
    }
}
