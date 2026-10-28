using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// The scripts shipped under <c>src/Resourcefull.SqlServer/Scripts</c>: a hand-written preamble followed by the output of
/// <c>dotnet ef migrations script --idempotent</c> for a migration range.
/// </summary>
internal static partial class ProvisioningScripts
{
    public const string DatabaseName = "ResourceDB";
    public const string ScriptsRelativeDirectory = "src/Resourcefull.SqlServer/Scripts";
    public const string FreshInstallFileName = "Resourcefull.sql";
    public const string UpgradeDirectoryName = "Migrations";
    public const string UpgradeFileNamePrefix = "migrate_";

    public const string FreshInstallPreamble =
        "IF NOT EXISTS (\n" +
        "    SELECT [name] FROM sys.databases WHERE [name] = N'ResourceDB'\n" +
        ")\n" +
        "BEGIN\n" +
        "    CREATE DATABASE [ResourceDB];\n" +
        "END;\n" +
        "GO\n" +
        "\n" +
        "USE [ResourceDB];\n" +
        "GO\n" +
        "\n";

    public const string UpgradePreamble =
        "USE [ResourceDB];\n" +
        "GO\n" +
        "\n";

    private const string SolutionFileName = "Resourcefull.sln";

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ScriptsDirectory => Path.Combine(RepositoryRoot, ScriptsRelativeDirectory);

    public static IReadOnlyList<string> MigrationIds
    {
        get
        {
            using ResourceDbContext context = CreateNonExistentContext();
            return [.. context.Database.GetMigrations()];
        }
    }

    /// <summary>The fresh-install script plus one upgrade script per migration after the first.</summary>
    public static IReadOnlyList<ProvisioningScript> Expected()
    {
        IReadOnlyList<string> ids = MigrationIds;
        var scripts = new List<ProvisioningScript>
        {
            new(FreshInstallFileName, FreshInstallPreamble, FromMigration: null, ToMigration: null)
        };

        for (int i = 1; i < ids.Count; i++)
        {
            scripts.Add(new ProvisioningScript(
                Path.Combine(UpgradeDirectoryName, $"{UpgradeFileNamePrefix}{ids[i]}.sql"),
                UpgradePreamble,
                FromMigration: ids[i - 1],
                ToMigration: ids[i]));
        }

        return scripts;
    }

    /// <summary>Same output as <c>dotnet ef migrations script --idempotent</c>.</summary>
    public static string GenerateBody(string? fromMigration, string? toMigration)
    {
        using ResourceDbContext context = CreateNonExistentContext();
        return context.GetService<IMigrator>().GenerateScript(fromMigration, toMigration, MigrationsSqlGenerationOptions.Idempotent);
    }

    /// <summary>Ignores the line endings and trailing whitespace.</summary>
    public static string Normalize(string sql)
    {
        string text = sql.TrimStart('﻿').Replace("\r\n", "\n");
        IEnumerable<string> lines = text.Split('\n').Select(line => line.TrimEnd());
        return string.Join("\n", lines).TrimEnd('\n') + "\n";
    }

    public static string DescribeFirstDifference(string expected, string actual)
    {
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');
        int limit = Math.Min(expectedLines.Length, actualLines.Length);
        for (int i = 0; i < limit; i++)
        {
            if (!string.Equals(expectedLines[i], actualLines[i], StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}.{Environment.NewLine}  expected: {expectedLines[i]}{Environment.NewLine}  actual:   {actualLines[i]}";
            }
        }

        return expectedLines.Length == actualLines.Length
            ? "The texts are identical."
            : $"The texts agree for {limit} lines; expected {expectedLines.Length} lines but found {actualLines.Length}.";
    }

    /// <summary>
    /// Runs the script batch by batch like <c>sqlcmd</c>, from <c>master</c>, with <c>ResourceDB</c> swapped for the
    /// throwaway database in <paramref name="targetConnectionString"/> so a real one is never touched.
    /// </summary>
    public static async Task ApplyAsync(string sql, string targetConnectionString)
    {
        var target = new SqlConnectionStringBuilder(targetConnectionString);
        string replaced = Regex.Replace(sql, $@"\b{Regex.Escape(DatabaseName)}\b", target.InitialCatalog);
        IReadOnlyList<string> batches = [.. BatchSeparatorPattern().Split(replaced).Where(batch => !string.IsNullOrWhiteSpace(batch))];

        var master = new SqlConnectionStringBuilder(targetConnectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();

        for (int i = 0; i < batches.Count; i++)
        {
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = batches[i];
            command.CommandTimeout = 120;
            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException exception)
            {
                throw new InvalidOperationException($"Batch {i + 1} of {batches.Count} failed (SQL error {exception.Number}): " +
                    $"{exception.Message}{Environment.NewLine}{batches[i].Trim()}", exception);
            }
        }
    }

    public static string ReadCommitted(ProvisioningScript script) =>
        File.ReadAllText(Path.Combine(ScriptsDirectory, script.RelativePath));

    /// <summary>
    /// Listing migrations and generating scripts only read the migrations assembly; EF never opens a connection for
    /// them. UseSqlServer still requires a connection string, so this points at a server that doesn't exist.
    /// </summary>
    private static ResourceDbContext CreateNonExistentContext() =>
        new(new DbContextOptionsBuilder<ResourceDbContext>()
            .UseSqlServer("Server=(non-existent);Database=ResourceDB;Encrypt=False")
            .Options);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find '{SolutionFileName}' above '{AppContext.BaseDirectory}'; the provisioning script checks read the committed scripts and must run from a checkout.");
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparatorPattern();
}

internal sealed record ProvisioningScript(string RelativePath, string Preamble, string? FromMigration, string? ToMigration)
{
    public string Command =>
        "dotnet ef migrations script" +
        (FromMigration is null ? string.Empty : $" {FromMigration}") +
        (ToMigration is null ? string.Empty : $" {ToMigration}") +
        $" --idempotent --project src/Resourcefull.SqlServer --output {ProvisioningScripts.ScriptsRelativeDirectory}/{RelativePath.Replace('\\', '/')}";

    public override string ToString() => RelativePath;
}
