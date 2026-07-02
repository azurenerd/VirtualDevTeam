namespace VirtualDevTeam.Core.Scenarios;

/// <summary>
/// The verification verdict recorded by T-FINAL after scenario-by-scenario playtest.
/// </summary>
public enum VerificationStatus
{
    /// <summary>T-FINAL has not yet attempted to verify this scenario.</summary>
    NotYetVerified,

    /// <summary>T-FINAL successfully executed the scenario and observed the expected terminal state.</summary>
    Verified,

    /// <summary>T-FINAL executed the scenario but the terminal state was incorrect or absent.</summary>
    Broken,

    /// <summary>T-FINAL could not definitively verify or falsify (flaky app, missing fixture, etc.).</summary>
    Inconclusive,

    /// <summary>All implementing tasks completed — inferred pass without live app validation.</summary>
    InferredPass,

    /// <summary>One or more implementing tasks incomplete — inferred fail without live app validation.</summary>
    InferredFail,
}
