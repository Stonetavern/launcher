using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Localization;
using WowLauncher.ViewModels;

namespace WowLauncher.Startup;

/// <summary>
/// QA-only: seeded login-shell ViewModels for the <c>--screenshot --section login --state …</c>
/// harness, so every Phase-1/2/3 surface can be rendered and LOOKED AT without a realm, a manifest
/// or a sign-in service. Fakes only the pipeline and the gateway; the window, the styles and the
/// ViewModel are the shipped ones.
///
/// <para>States: <c>cold</c> (Phase 0, first frame), <c>checking</c> (gate closed, skeleton rows),
/// <c>ready</c> (default: gate open, realm resolved), <c>offline</c> (realm probe said no),
/// <c>banner</c> (ready plus a launcher-update banner), <c>error</c> (rejected sign-in),
/// <c>timeout</c> (no answer, offline path), <c>transition</c> (Phase 3 storyboard has run).</para>
/// </summary>
public static class LoginShellQa
{
    public static LoginShellViewModel ViewModelFor(string? state, bool allowSkipSignIn = false)
    {
        var s = (state ?? "ready").Trim().ToLowerInvariant();
        var never = s is "checking" or "cold";

        var facts = new InitFacts();
        facts.Realms = s == "offline"
            ? [new RealmProbe("stonetavern", "Stonetavern", "play.stonetavern.app", false, 0)]
            : [new RealmProbe("stonetavern", "Stonetavern", "play.stonetavern.app", true, 43)];
        if (s == "banner") facts.UpdateNotice = new Services.LauncherUpdateNotice("1.9.0", "https://stonetavern.app");

        InitStep Step(string key, string text, bool gates) =>
            new(key, text, gates, never ? Never : _ => Task.FromResult<string?>(null));

        var pipeline = new InitPipeline(new List<InitStep>
        {
            Step(LauncherInitSteps.ConfigKey, Loc.T("Init_Status_Config"), true),
            Step(LauncherInitSteps.UpdateKey, Loc.T("Init_Status_Update"), true),
            Step(LauncherInitSteps.RealmKey, Loc.T("Init_Status_Realm"), true),
            Step(LauncherInitSteps.CdnKey, Loc.T("Init_Status_Cdn"), false),
            Step(LauncherInitSteps.InstallsKey, Loc.T("Init_Status_Installs"), false),
        });

        var mode = s switch
        {
            "error" => FakeAuthGateway.RejectMode,
            "timeout" => FakeAuthGateway.UnavailableMode,
            _ => FakeAuthGateway.SuccessMode,
        };
        var gateway = new FakeAuthGateway(mode, TimeSpan.Zero);
        var vm = new LoginShellViewModel(pipeline, facts, gateway, allowSkipSignIn);

        if (s is "error" or "timeout" or "transition")
        {
            // Fire the attempt once the gate opened (the fake steps complete synchronously after
            // BeginPhase1, which the window calls on its first frame).
            vm.Username = "player";
            vm.Password = "secret";
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LoginShellViewModel.IsGateOpen) && vm.IsGateOpen
                    && vm.SignInCommand.CanExecute(null))
                    vm.SignInCommand.Execute(null);
            };
        }

        return vm;
    }

    /// <summary>True when the still must stay in Phase 0 (the window then never calls BeginPhase1).</summary>
    public static bool HoldsAtPhase0(string? state) =>
        string.Equals(state?.Trim(), "cold", StringComparison.OrdinalIgnoreCase);

    private static Task<string?> Never(CancellationToken ct) =>
        new TaskCompletionSource<string?>().Task;
}
