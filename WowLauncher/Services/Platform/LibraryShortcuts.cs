namespace WowLauncher.Services.Platform;

/// <summary>
/// The ways back to the launcher in a new Stonetavern folder: Start menu, desktop and a shortcut in the
/// folder itself on Windows; menu entry and desktop shortcut on Linux. Written by the copy after it
/// finished the setup (so they point at the file that runs), best effort: a missing shortcut is an
/// inconvenience, the launcher runs regardless and the old download forwards to it.
/// </summary>
public static class LibraryShortcuts
{
    public static async Task CreateAsync(string root, IConfigService config, Serilog.ILogger log)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (Environment.ProcessPath is not { } exe) return;
                foreach (var folder in new[]
                         {
                             Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                             Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                             root,
                         })
                {
                    if (string.IsNullOrEmpty(folder)) continue;
                    var spec = WindowsDesktopIntegrationService.ResolveSpec(folder, exe);
                    await new WindowsDesktopIntegrationService(spec, log).InstallAsync().ConfigureAwait(false);
                }
            }
            else if (OperatingSystem.IsLinux()
                     && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage)
            {
                var setup = new LinuxFirstRunSetup(FirstRunSetup.ResolveLayout(), config, log);
                await setup.WriteShortcutsAsync(appImage).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Shortcuts for the Stonetavern folder {Root} could not be written", root);
        }
    }
}
