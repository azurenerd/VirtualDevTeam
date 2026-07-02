using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VirtualDevTeam.Core.Configuration;
using VirtualDevTeam.Core.DevPlatform.Capabilities;

namespace VirtualDevTeam.Core.Tests.Configuration;

/// <summary>
/// Pins the wizard-vs-appsettings precedence rules introduced for the
/// 2026-05-12 <c>gates-disabled-but-firing</c> bug. The PMSpec gate fired even
/// though the operator's <c>develop-settings.json</c> had
/// <c>gatePreferences.enabled = false</c>. Rules verified here:
/// <list type="number">
///   <item>Wizard's master switch <c>enabled = false</c> wins over per-gate <c>RequiresHuman = true</c> (ALL gates auto-pass).</item>
///   <item>Wizard's master switch <c>enabled = true</c> + per-gate <c>RequiresHuman = false</c> auto-passes (per-gate off).</item>
///   <item>Wizard's master switch <c>enabled = true</c> + per-gate <c>RequiresHuman = true</c> requires human approval.</item>
///   <item><c>GatePreferences = null</c> (no wizard config) falls back to appsettings.json's <c>HumanInteraction</c>.</item>
///   <item>Wizard's per-gate map overrides appsettings.json when both define the same gate.</item>
///   <item><c>AreAllGatesDisabled()</c> returns true when EITHER source disables the master switch.</item>
/// </list>
/// </summary>
public sealed class GateCheckServiceConfigPrecedenceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public GateCheckServiceConfigPrecedenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vdt-gate-precedence-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "develop-settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private GateCheckService CreateGate(
        VirtualDevTeamConfig appsettingsConfig,
        DevelopSettingsService? developSettings = null) =>
        new(
            config: Options.Create(appsettingsConfig),
            prService: new Mock<IPullRequestService>().Object,
            reviewService: new Mock<IReviewService>().Object,
            workItemService: new Mock<IWorkItemService>().Object,
            logger: NullLogger<GateCheckService>.Instance,
            notificationService: null,
            chatRunner: null,
            stateStore: null,
            developSettings: developSettings);

    private DevelopSettingsService CreateSettingsService(VirtualDevTeamConfig? appsettingsConfig = null)
    {
        var logger = NullLogger<DevelopSettingsService>.Instance;
        var options = appsettingsConfig is not null ? Options.Create(appsettingsConfig) : null;
        return new DevelopSettingsService(logger, options, _settingsPath);
    }

    private static VirtualDevTeamConfig MakeAppsettings(bool masterEnabled, bool pmSpecRequiresHuman) =>
        new()
        {
            HumanInteraction = new HumanInteractionConfig
            {
                Enabled = masterEnabled,
                Gates = new Dictionary<string, GateConfig>
                {
                    [GateIds.PMSpecification] = new() { RequiresHuman = pmSpecRequiresHuman }
                }
            }
        };

    private static DevelopSettings MakeDevelopSettings(bool? masterEnabled, bool? pmSpecRequiresHuman)
    {
        var settings = new DevelopSettings();
        if (masterEnabled is null) return settings;

        settings.GatePreferences = new GatePreferences
        {
            Enabled = masterEnabled.Value,
            Gates = new Dictionary<string, bool>()
        };
        if (pmSpecRequiresHuman is not null)
            settings.GatePreferences.Gates[GateIds.PMSpecification] = pmSpecRequiresHuman.Value;
        return settings;
    }

    // ─── Master-switch precedence ────────────────────────────────────────────────

    [Fact]
    public async Task RequiresHuman_WizardMasterOff_PerGateOn_AutoPassesAllGates()
    {
        // The bug: operator set master=false in wizard, but PMSpec still gated.
        var settingsSvc = CreateSettingsService();
        var ds = MakeDevelopSettings(masterEnabled: false, pmSpecRequiresHuman: true);
        // Force MergeIntoConfig so DevelopSettingsService.Current is populated.
        var appsettings = MakeAppsettings(masterEnabled: true, pmSpecRequiresHuman: true);
        settingsSvc.MergeIntoConfig(appsettings, ds);

        var gate = CreateGate(appsettings, settingsSvc);

        Assert.True(gate.AreAllGatesDisabled(),
            "Wizard master=false must short-circuit AreAllGatesDisabled");
        Assert.False(gate.RequiresHuman(GateIds.PMSpecification),
            "PMSpec must auto-pass when wizard master switch is off, even if per-gate is on");

        var result = await gate.CheckGateAsync(GateIds.PMSpecification, "test", resourceNumber: 100);
        Assert.Equal(GateResult.Proceed, result);

        // WaitForGateAsync extension should also short-circuit without polling.
        var wait = await gate.WaitForGateAsync(GateIds.PMSpecification, "test", resourceNumber: 100);
        Assert.False(wait.WasActivated);
        Assert.Equal(GateDecision.Approved, wait.Decision);
    }

    [Fact]
    public async Task RequiresHuman_WizardMasterOn_PerGateOff_AutoPasses()
    {
        var settingsSvc = CreateSettingsService();
        var ds = MakeDevelopSettings(masterEnabled: true, pmSpecRequiresHuman: false);
        var appsettings = MakeAppsettings(masterEnabled: true, pmSpecRequiresHuman: true);
        settingsSvc.MergeIntoConfig(appsettings, ds);

        var gate = CreateGate(appsettings, settingsSvc);

        Assert.False(gate.AreAllGatesDisabled());
        Assert.False(gate.RequiresHuman(GateIds.PMSpecification),
            "Wizard's per-gate=false must override appsettings's per-gate=true");

        var result = await gate.CheckGateAsync(GateIds.PMSpecification, "test", resourceNumber: 100);
        Assert.Equal(GateResult.Proceed, result);
    }

    [Fact]
    public async Task RequiresHuman_WizardMasterOn_PerGateOn_RequiresHumanApproval()
    {
        var settingsSvc = CreateSettingsService();
        var ds = MakeDevelopSettings(masterEnabled: true, pmSpecRequiresHuman: true);
        // appsettings is intentionally false so we know the wizard value is what's read.
        var appsettings = MakeAppsettings(masterEnabled: true, pmSpecRequiresHuman: false);
        settingsSvc.MergeIntoConfig(appsettings, ds);

        // Stub PR service so CheckGateAsync's label-update branch doesn't NRE.
        var prMock = new Mock<IPullRequestService>();
        prMock.Setup(p => p.GetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((global::VirtualDevTeam.Core.DevPlatform.Models.PlatformPullRequest?)null);
        var workItemMock = new Mock<IWorkItemService>();
        workItemMock.Setup(w => w.GetCommentsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Array.Empty<global::VirtualDevTeam.Core.DevPlatform.Models.PlatformComment>());

        var gate = new GateCheckService(
            config: Options.Create(appsettings),
            prService: prMock.Object,
            reviewService: new Mock<IReviewService>().Object,
            workItemService: workItemMock.Object,
            logger: NullLogger<GateCheckService>.Instance,
            developSettings: settingsSvc);

        Assert.False(gate.AreAllGatesDisabled());
        Assert.True(gate.RequiresHuman(GateIds.PMSpecification),
            "Wizard's per-gate=true must override appsettings's per-gate=false");

        var result = await gate.CheckGateAsync(GateIds.PMSpecification, "test", resourceNumber: 100);
        Assert.Equal(GateResult.WaitingForHuman, result);
    }

    // ─── Fallback to appsettings when no wizard config ───────────────────────────

    [Fact]
    public void RequiresHuman_NoGatePreferences_FallsBackToAppsettingsEnabledTrue()
    {
        var settingsSvc = CreateSettingsService();
        var ds = MakeDevelopSettings(masterEnabled: null, pmSpecRequiresHuman: null); // no GatePreferences
        var appsettings = MakeAppsettings(masterEnabled: true, pmSpecRequiresHuman: true);
        settingsSvc.MergeIntoConfig(appsettings, ds);

        var gate = CreateGate(appsettings, settingsSvc);

        Assert.False(gate.AreAllGatesDisabled());
        Assert.True(gate.RequiresHuman(GateIds.PMSpecification),
            "Without wizard prefs, appsettings RequiresHuman=true must apply");
    }

    [Fact]
    public void RequiresHuman_NoGatePreferences_FallsBackToAppsettingsEnabledFalse()
    {
        var settingsSvc = CreateSettingsService();
        var ds = MakeDevelopSettings(masterEnabled: null, pmSpecRequiresHuman: null);
        var appsettings = MakeAppsettings(masterEnabled: false, pmSpecRequiresHuman: true);
        settingsSvc.MergeIntoConfig(appsettings, ds);

        var gate = CreateGate(appsettings, settingsSvc);

        Assert.True(gate.AreAllGatesDisabled(),
            "Without wizard prefs, appsettings.HumanInteraction.Enabled=false must short-circuit");
        Assert.False(gate.RequiresHuman(GateIds.PMSpecification));
    }

    // ─── PrePRClarificationGate must NOT re-enable master ─────────────────────────

    [Fact]
    public void MergeIntoConfig_WizardMasterOff_PrePRClarificationGateTrue_DoesNotReEnableMaster()
    {
        // Regression: previously the merge would re-enable HumanInteraction.Enabled
        // whenever PrePRClarificationGate was true (its default). That meant
        // wizard's gatePreferences.enabled=false was silently ignored.
        var settingsSvc = CreateSettingsService();
        var ds = MakeDevelopSettings(masterEnabled: false, pmSpecRequiresHuman: false);
        ds.PrePRClarificationGate = true; // the buggy override trigger
        var appsettings = MakeAppsettings(masterEnabled: true, pmSpecRequiresHuman: false);

        settingsSvc.MergeIntoConfig(appsettings, ds);

        Assert.False(appsettings.HumanInteraction.Enabled,
            "Wizard master=false must NOT be re-enabled by PrePRClarificationGate=true");
        Assert.True(appsettings.HumanInteraction.Gates[GateIds.PrePRClarification].RequiresHuman,
            "Per-gate flag is still set on the gate, but the master switch overrides it");
    }

    // ─── DevelopSettingsService.Current is populated ─────────────────────────────

    [Fact]
    public async Task LoadAsync_PopulatesCurrent()
    {
        var settingsSvc = CreateSettingsService();
        Assert.Null(settingsSvc.Current);

        var loaded = await settingsSvc.LoadAsync();

        Assert.NotNull(settingsSvc.Current);
        Assert.Same(loaded, settingsSvc.Current);
    }

    [Fact]
    public void MergeIntoConfig_PopulatesCurrent()
    {
        var settingsSvc = CreateSettingsService();
        var ds = MakeDevelopSettings(masterEnabled: false, pmSpecRequiresHuman: true);
        var appsettings = MakeAppsettings(masterEnabled: true, pmSpecRequiresHuman: true);

        settingsSvc.MergeIntoConfig(appsettings, ds);

        Assert.NotNull(settingsSvc.Current);
        Assert.Same(ds, settingsSvc.Current);
    }
}
