namespace WowLauncher.Services.AgentControl;

/// <summary>
/// Whether this run exposes the local control surface, and on which port.
///
/// <para><b>Why this exists.</b> The launcher already answers two questions without a mouse
/// (<c>--preflight</c>, <c>--e2e</c>), for the reason spelled out in <c>Program.cs</c>: an answer that
/// only a human with a mouse can coax out of the app is invisible on any test bench. Those two are
/// one-shot and headless, though — they cannot inspect a <em>running</em> window or press a button in
/// it. On 2026-08-24 that gap cost a full afternoon: a macOS test had to go through screen sharing,
/// where the picture needed a privacy grant that no command line can set, and mouse clicks never
/// arrived at all. This surface closes the gap on every platform at once, because it is plain HTTP on
/// loopback rather than anything the operating system considers input.</para>
///
/// <para><b>🔴 It is off unless explicitly asked for.</b> Three independent conditions guard it:
/// the switch must be present, the listener binds <c>127.0.0.1</c> only, and every request must carry
/// a token that is generated per run. A player build never passes the switch, so the listener is never
/// created — there is no port to find and no default token to guess.</para>
/// </summary>
/// <param name="Enabled">True when <c>--agent-control</c> was passed.</param>
/// <param name="Port">TCP port on loopback. 0 means "let the OS pick a free one", which is the
/// default: a fixed port collides when two launchers run side by side, and the chosen port is
/// published in the handshake file anyway.</param>
public sealed record AgentControlOptions(bool Enabled, int Port)
{
    public const string Switch = "--agent-control";

    /// <summary>Off — the shape every normal run gets.</summary>
    public static AgentControlOptions Disabled { get; } = new(false, 0);

    /// <summary>
    /// Reads the switch out of the command line. Accepts a bare <c>--agent-control</c> (OS picks the
    /// port) and <c>--agent-control &lt;port&gt;</c> / <c>--agent-control=&lt;port&gt;</c>.
    ///
    /// <para>A malformed or out-of-range port is <b>not</b> silently downgraded to "pick one for me":
    /// somebody who names a port wants that port, and quietly listening somewhere else would send a
    /// test harness looking at the wrong door. It throws instead.</para>
    /// </summary>
    public static AgentControlOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (arg.StartsWith(Switch + "=", StringComparison.Ordinal))
                return new AgentControlOptions(true, ParsePort(arg[(Switch.Length + 1)..]));

            if (!string.Equals(arg, Switch, StringComparison.Ordinal))
                continue;

            // A following token counts as the port only if it looks like one. Otherwise it belongs to
            // the next switch and must stay untouched.
            var next = i + 1 < args.Count ? args[i + 1] : null;
            return next is not null && !next.StartsWith('-')
                ? new AgentControlOptions(true, ParsePort(next))
                : new AgentControlOptions(true, 0);
        }

        return Disabled;
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var port)
            || port is < 0 or > 65535)
        {
            throw new ArgumentException(
                $"{Switch}: '{value}' is not a TCP port (0-65535). Leave it out to let the OS pick one.",
                nameof(value));
        }

        return port;
    }
}
