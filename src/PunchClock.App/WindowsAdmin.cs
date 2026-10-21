using System.Security.Principal;

namespace PunchClock.App;

/// <summary>
/// Whether a Windows administrator is running PunchClock elevated. Creating the first admin and
/// recovering an admin account both require it, so a kiosk user cannot claim or take over the site.
/// </summary>
internal static class WindowsAdmin
{
    public static bool IsElevated()
    {
        // Under UAC this is true only for an elevated process, not for an admin's filtered token.
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The signed-in Windows account, as DOMAIN\user, recorded in the audit log.</summary>
    public static string CurrentUserName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }
}
