using System;
using WowLauncher.Models;

namespace WowLauncher.Startup;

/// <summary>
/// The one place that reads <see cref="LauncherConfig.LauncherShell"/>.
///
/// <para><b>Since 1.9.1 (owner 2026-09-22: "the new launcher had a login screen") the 1.9 login/loading
/// shell IS the default.</b> Until then it sat behind this flag and every player got the old splash,
/// so a release would have shipped without the screen the redesign was built around. Only the exact
/// value "v1" opts back into the old start (splash then shell), as the escape hatch. Empty, "login",
/// the pre-1.9 alias "v2" and any unknown value open the login shell: it is the known, tested default
/// surface now, so a typo lands on it rather than on the legacy one.</para>
/// </summary>
public static class LoginShellFlag
{
    public const string Login = "login";

    /// <summary>The pre-1.9 config value, still accepted so existing configs keep working.</summary>
    public const string LegacyV2 = "v2";

    /// <summary>The only value that opts OUT: the pre-1.9.1 start without the login screen.</summary>
    public const string ClassicStart = "v1";

    public static bool IsEnabled(LauncherConfig cfg)
    {
        var value = cfg.LauncherShell?.Trim();
        return !string.Equals(value, ClassicStart, StringComparison.OrdinalIgnoreCase);
    }
}
