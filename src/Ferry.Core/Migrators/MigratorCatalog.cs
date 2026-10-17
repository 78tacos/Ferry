using Ferry.Core.Migrators.Apps;
using Ferry.Core.Migrators.Email;
using Ferry.Core.Migrators.Windows;

namespace Ferry.Core.Migrators;

public static class MigratorCatalog
{
    /// <summary>Every migrator Ferry ships with. Order of restore is by <see cref="IMigrator.Order"/>, not by this list.</summary>
    public static IReadOnlyList<IMigrator> CreateAll() =>
    [
        new WifiMigrator(),
        new PrintersMigrator(),
        new MappedDrivesMigrator(),
        new PersonalizationMigrator(),
        new FontsMigrator(),
        new DefaultAppsMigrator(),
        new ApplicationsMigrator(),
        new AppSettingsMigrator(),
        new BrowsersMigrator(),
        new OfficeMigrator(),
        new EnvironmentMigrator(),
        new UserFilesMigrator(),
        new FoldersMigrator(),
    ];
}
