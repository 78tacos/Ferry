using System;
using System.Collections.Generic;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;

namespace Ferry.App;

/// <summary>
/// What the pages show about the PC Ferry is running on. The design preview swaps in a sample PC, so rendered
/// screens (used in the docs) never show the name, accounts or folders of whichever PC rendered them.
/// </summary>
internal static class ThisPc
{
    public static Func<bool> IsAdministrator { get; set; } = () => Elevation.IsAdministrator;
    public static Func<string> Name { get; set; } = () => Environment.MachineName;
    public static Func<MachineInfo> Machine { get; set; } = SystemInfo.Collect;
    public static Func<List<UserProfileInfo>> Users { get; set; } = UserProfiles.Enumerate;
}
