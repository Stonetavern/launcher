using System.Collections.Generic;

namespace WowLauncher.Startup;

/// <summary>
/// Which window "come to the front" (tray, a second start) may show.
///
/// <para><b>Measured 2026-09-28 (E2E, real AppImage).</b> A second start asked the running launcher to come
/// forward while its main window was already closed; <c>Window.Show()</c> on a closed window throws
/// ("Cannot re-show a closed window") and the running launcher crashed. Since the single-instance lock, that
/// request comes from outside at any moment (during quit, between two windows), so it must never throw.</para>
/// </summary>
internal static class BringForward
{
    /// <summary>The main window while it is still open (hidden counts as open), else the most recently opened
    /// window that is, else none. <paramref name="open"/> is the lifetime's list of open windows.</summary>
    public static T? Pick<T>(T? main, IReadOnlyList<T> open) where T : class
    {
        if (main is not null && Contains(open, main)) return main;
        return open.Count > 0 ? open[^1] : null;
    }

    private static bool Contains<T>(IReadOnlyList<T> list, T item) where T : class
    {
        foreach (var x in list) if (ReferenceEquals(x, item)) return true;
        return false;
    }
}
