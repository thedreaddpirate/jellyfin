using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Server.Migrations.Routines;
using MediaBrowser.Common.Configuration;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

public sealed class MigrateLoggingDefaultTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("jellyfin-logging-migration-").FullName;
    private readonly Mock<IApplicationPaths> _paths = new();

    public MigrateLoggingDefaultTests()
    {
        _paths.SetupGet(paths => paths.ConfigurationDirectoryPath).Returns(_directory);
    }

    [Fact]
    public async Task PerformAsync_FinishesWritingAndClosesTheReplacement()
    {
        var path = Path.Combine(_directory, "logging.default.json");
        const string OldConfig = "{\"Serilog\":{\"MinimumLevel\":{}}}";
        await File.WriteAllTextAsync(path, OldConfig, TestContext.Current.CancellationToken);

        await new MigrateLoggingDefault(_paths.Object).PerformAsync(TestContext.Current.CancellationToken);

        // Exclusive access verifies that the initializer has also closed its output stream.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using var reader = new StreamReader(stream);
        var config = JObject.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Error", config["Serilog"]?["MinimumLevel"]?["Override"]?["Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager"]?.Value<string>());
        Assert.Equal(OldConfig, await File.ReadAllTextAsync(Path.Combine(_directory, "logging.default.old.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PerformAsync_PropagatesReplacementWriteFailures()
    {
        var path = Path.Combine(_directory, "logging.default.json");
        await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);
        _paths.SetupSequence(paths => paths.ConfigurationDirectoryPath)
            .Returns(_directory)
            .Returns(Path.Combine(_directory, "missing-directory"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => new MigrateLoggingDefault(_paths.Object).PerformAsync(TestContext.Current.CancellationToken));

        Assert.True(File.Exists(Path.Combine(_directory, "logging.default.old.json")));
    }

    [Fact]
    public async Task PerformAsync_LeavesCurrentConfigurationUnchanged()
    {
        var path = Path.Combine(_directory, "logging.default.json");
        const string CurrentConfig = """
            {"Serilog":{"MinimumLevel":{"Override":{
              "Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager":"Error",
              "Microsoft.AspNetCore.DataProtection.Repositories.EphemeralXmlRepository":"Error",
              "Microsoft.AspNetCore.StaticFiles.StaticFileMiddleware":"Error"
            }}}}
            """;
        await File.WriteAllTextAsync(path, CurrentConfig, TestContext.Current.CancellationToken);

        await new MigrateLoggingDefault(_paths.Object).PerformAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CurrentConfig, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_directory, "logging.default.old.json")));
    }

    [Fact]
    public async Task PerformAsync_LeavesMissingConfigurationAlone()
    {
        await new MigrateLoggingDefault(_paths.Object).PerformAsync(TestContext.Current.CancellationToken);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
