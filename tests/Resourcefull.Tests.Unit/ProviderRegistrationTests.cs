using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

/// <summary>
/// Registering a provider needs nothing from its namespace, so switching providers changes one line and no <c>using</c>:
/// this file names none of them, and it wouldn't compile if an extension method lived anywhere else.
/// </summary>
public class ProviderRegistrationTests
{
    [Fact]
    public void Use_ShouldRegisterEachProviderWithNoUsingOfItsOwn_WhenGivenOnlyWhatItNeeds()
    {
        // Arrange
        Action<ResourcefullOptionsBuilder>[] providers =
        [
            options => options.UseInMemory(),
            options => options.UseSqlServer("Server=unused"),
            options => options.UsePostgreSql("Host=unused"),
            options => options.UseSqlite("Data Source=unused"),
            options => options.UseFileSystem(Path.GetTempPath()),
            options => options.UseS3("bucket", o => o.Client = new Amazon.S3.AmazonS3Client(
                new Amazon.Runtime.AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)),
            options => options.UseAzureBlob("container", o => o.Client = new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"))
        ];

        // Act & Assert
        foreach (Action<ResourcefullOptionsBuilder> use in providers)
        {
            var services = new ServiceCollection();
            services.AddResourcefull(use);
            Assert.Single(services, d => d.ServiceType == typeof(ResourceRepository));
        }
    }

    [Fact]
    public void AddCheck_ShouldRegisterEachProvidersHealthCheckWithNoUsingOfItsOwn_WhenCalled()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddHealthChecks()
            .AddResourcefullSqlServerCheck("sql-server")
            .AddResourcefullPostgreSqlCheck("postgresql")
            .AddResourcefullSqliteCheck("sqlite");

        // Assert
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Equal(["sql-server", "postgresql", "sqlite"],
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value.Registrations.Select(r => r.Name));
    }
}
