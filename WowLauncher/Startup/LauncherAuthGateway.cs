using System.Threading;
using System.Threading.Tasks;
using WowLauncher.Localization;
using WowLauncher.Services;

namespace WowLauncher.Startup;

/// <summary>
/// Spec §12.1 A: the 1.9 login screen signs in against the launcher service
/// (<see cref="ILauncherAuthService"/>, <c>POST /api/launcher/login</c>, SRP6 verified server side),
/// not a credential pass-through to the game client. The player signs in again inside the client;
/// the token this stores opens friends, armory, addons and profile sync.
///
/// <para>The mapping reads the service's own outcome line: the line was built from one of its
/// message keys, so "invalid username or password" is the only one that blames the field (shake,
/// focus back); the timeout gets its own sentence; everything else (rate limit, malformed body, no
/// connection) is reported as unreachable, which is the surface that offers the offline path. The
/// line itself is the service's, so nothing is re-worded here and the password only ever travels
/// inside the request body.</para>
/// </summary>
public sealed class LauncherAuthGateway : IAuthGateway
{
    private readonly ILauncherAuthService _auth;

    public LauncherAuthGateway(ILauncherAuthService auth) => _auth = auth;

    public async Task<AuthResult> SignInAsync(string username, string password, CancellationToken ct)
    {
        var outcome = await _auth.LoginAsync(username, password, ct).ConfigureAwait(false);
        if (outcome.Ok)
            return AuthResult.Success(outcome.AccountName ?? username.Trim());

        var message = outcome.Error ?? Loc.T("Login_Error_Unavailable");
        if (message == Loc.T("Login_Error_Invalid"))
            return AuthResult.Rejected(message);
        if (message == Loc.T("Login_Error_Timeout"))
            return AuthResult.Timeout(message);
        return AuthResult.Unavailable(message);
    }
}
