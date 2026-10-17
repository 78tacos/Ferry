using System.Security.Principal;

namespace Ferry.Core.Infrastructure;

public static class Elevation
{
    private static readonly Lazy<bool> IsAdminLazy = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    /// <summary>True when running elevated. Needed for other users' profiles, HKLM, Program Files and Wi-Fi keys.</summary>
    public static bool IsAdministrator => IsAdminLazy.Value;

    public static string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? "";
        }
    }
}
