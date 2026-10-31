using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Resourcefull;

/// <summary>
/// Configures Resourcefull inside <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/>: its storage
/// provider, versioning, URI provider, telemetry, container policies and expiry.
/// </summary>
public class ResourcefullOptionsBuilder
{
    /// <summary>
    /// Gets whether a storage provider has been configured. Exactly one must be, through a provider package's
    /// <c>Use…</c> extension or <see cref="UseProvider{TRepository}"/>.
    /// </summary>
    public bool ProviderConfigured { get; private set; }

    /// <summary>
    /// Gets the application's service collection, for provider extensions to register their services in.
    /// </summary>
    public IServiceCollection Services { get; init; }

    /// <summary>
    /// Creates a builder that registers into <paramref name="services"/>. Applications get one from
    /// <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> rather than creating it.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    public ResourcefullOptionsBuilder(IServiceCollection services) => Services = services;

    /// <summary>
    /// Uses <typeparamref name="TRepository"/> as the storage provider. Provider packages call this from their own
    /// <c>Use…</c> extension; a custom provider can be registered with it directly.
    /// </summary>
    /// <typeparam name="TRepository">The provider's <see cref="IResourceRepository"/> implementation.</typeparam>
    /// <param name="lifetime">How long one instance serves: singleton for a provider that holds its own state.</param>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    public ResourcefullOptionsBuilder UseProvider<TRepository>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TRepository : class, IResourceRepository
    {
        if (ProviderConfigured)
        {
            throw new InvalidOperationException("Provider has already been configured.");
        }

        Services.RemoveAll<IResourceRepository>();
        Services.Add(new ServiceDescriptor(typeof(IResourceRepository), typeof(TRepository), lifetime));
        ProviderConfigured = true;
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="T"/> to give containers and resources their <c>Uri</c>, replacing any URI provider set
    /// before. It is registered as a scoped service, so it can depend on other services.
    /// </summary>
    /// <typeparam name="T">The <see cref="IUriProvider"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder SetUriProvider<T>() where T : class, IUriProvider
    {
        ServiceDescriptor? descriptor = Services.SingleOrDefault(
                d => d.ServiceType ==
                    typeof(IUriProvider));

        if (descriptor != null)
        {
            Services.Remove(descriptor);
        }

        Services.AddScoped<IUriProvider, T>();
        return this;
    }

    /// <summary>
    /// Turns on versioning: each upload of a name stores a new version and keeps up to <paramref name="maxVersions"/>
    /// of them, pruning the oldest. Without versioning, a name holds one version, which an upload with
    /// <see cref="ResourceUploadOptions.Overwrite"/> replaces.
    /// </summary>
    /// <param name="maxVersions">How many versions of each name to keep, including the current one; 100 when null.</param>
    /// <exception cref="InvalidOperationException"><paramref name="maxVersions"/> is zero or negative.</exception>
    public ResourcefullOptionsBuilder UseVersioning(int? maxVersions = null)
    {
        if (maxVersions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxVersions), maxVersions, "Keep at least one version.");
        }

        Services.Configure<ResourcefullOptions>(options =>
        {
            options.IsVersioningEnabled = true;
            options.MaxVersions = maxVersions ?? 100;
        });
        return this;
    }

    /// <summary>
    /// Configures what Resourcefull's spans and metrics record. Telemetry is published whether or not this is called,
    /// under the names in <see cref="ResourcefullTelemetry"/>; this only changes what goes into it.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="configure">A callback to set <see cref="ResourcefullTelemetryOptions"/>.</param>
    public ResourcefullOptionsBuilder UseTelemetry(Action<ResourcefullTelemetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure<ResourcefullOptions>(options => configure(options.Telemetry));
        return this;
    }

    /// <summary>
    /// Turns on container policies, with <paramref name="defaults"/> for every container, replacing any set before.
    /// Add <see cref="UsePolicyProvider{TProvider}"/> to give containers their own. Without either, no policy is checked.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="defaults">The policy every container has, unless its own says otherwise; none when null.</param>
    /// <param name="cacheFor">
    /// How long a container's policy is kept before the policy provider is asked again, so how long a change can take
    /// to apply: 5 seconds when null, and zero to ask on every write.
    /// </param>
    /// <param name="keepLastKnownFor">
    /// When the policy provider throws, how long after it last gave a container's policy that policy is used instead,
    /// so an outage of the policy store doesn't fail every write: 5 minutes when null, and zero to fail at once. It
    /// helps only when longer than <paramref name="cacheFor"/>, since until then the cached policy is used anyway.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cacheFor"/> or <paramref name="keepLastKnownFor"/> is negative.
    /// </exception>
    public ResourcefullOptionsBuilder UseContainerPolicies(ResourceContainerPolicy? defaults = null, TimeSpan? cacheFor = null,
        TimeSpan? keepLastKnownFor = null)
    {
        if (cacheFor < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheFor), cacheFor, "The cache time can't be negative.");
        if (keepLastKnownFor < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(keepLastKnownFor), keepLastKnownFor,
                "The time can't be negative.");
        }

        Services.RemoveAll<ContainerPolicySettings>();
        Services.AddSingleton(_ => new ContainerPolicySettings(defaults ?? new ResourceContainerPolicy(),
            cacheFor ?? TimeSpan.FromSeconds(5), keepLastKnownFor ?? TimeSpan.FromMinutes(5)));
        Services.TryAddScoped<ContainerPolicies>();
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="TProvider"/> to give containers their own policies, replacing any policy provider set
    /// before, and turns on container policies with no defaults if <see cref="UseContainerPolicies"/> hasn't. It's
    /// registered as a scoped service, so it can depend on other services, such as the application's database context.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <typeparam name="TProvider">The <see cref="IResourcePolicyProvider"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder UsePolicyProvider<TProvider>() where TProvider : class, IResourcePolicyProvider
    {
        Services.RemoveAll<IResourcePolicyProvider>();
        Services.AddScoped<IResourcePolicyProvider, TProvider>();
        if (!Services.Any(d => d.ServiceType == typeof(ContainerPolicySettings))) UseContainerPolicies();
        return this;
    }

    /// <summary>
    /// Deletes expired versions in the background: a hosted service calls
    /// <see cref="IResourceServiceClient.DeleteExpiredAsync"/> when the application starts and every
    /// <paramref name="interval"/> after, so a version is gone within one interval of its
    /// <see cref="ResourceInfo.ExpiresAt"/>. Every instance of the application can run it. With a provider that doesn't
    /// support expiry, it logs a warning and stops. It also changes pruning: a version past
    /// <see cref="ResourcefullOptions.MaxVersions"/>, or replaced by an overwrite, is set to expire five minutes later
    /// instead of being deleted at once, so a download already reading it can finish.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="interval">How often to look for expired versions; one minute when null.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> isn't positive.</exception>
    public ResourcefullOptionsBuilder UseExpiry(TimeSpan? interval = null)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");

        Services.RemoveAll<ExpirySettings>();
        Services.AddSingleton(new ExpirySettings(interval ?? TimeSpan.FromMinutes(1)));
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ExpiryService>());
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="THandler"/> to hear about every version Resourcefull deletes by itself, before it's
    /// deleted, replacing any cleanup handler set before. It's registered as a scoped service, so it can depend on other
    /// services, such as the application's audit client.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <typeparam name="THandler">The <see cref="IResourceCleanupHandler"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder UseCleanupHandler<THandler>() where THandler : class, IResourceCleanupHandler
    {
        Services.RemoveAll<IResourceCleanupHandler>();
        Services.AddScoped<IResourceCleanupHandler, THandler>();
        return this;
    }

    /// <summary>
    /// Applies the settings in <paramref name="section"/>, such as a section of appsettings.json, as the matching
    /// methods would: each of its sections that's present turns that feature on, and a value left out keeps its
    /// default. Calls made after this one override it.
    /// <list type="bullet">
    /// <item><c>Versioning</c>: <see cref="UseVersioning"/>, with <c>MaxVersions</c>.</item>
    /// <item><c>Expiry</c>: <see cref="UseExpiry"/>, with <c>Interval</c>, such as <c>00:01:00</c>.</item>
    /// <item><c>Telemetry</c>: <see cref="UseTelemetry"/>, with <c>RecordResourceNames</c>.</item>
    /// <item>
    /// <c>ContainerPolicies</c>: <see cref="UseContainerPolicies"/>, with <c>CacheFor</c>, <c>KeepLastKnownFor</c> and
    /// <c>Defaults</c>, which takes the properties of <see cref="ResourceContainerPolicy"/>. Sizes are written as
    /// <see cref="ResourceSize.Parse(string, IFormatProvider?)"/> reads them, such as <c>100 MB</c>.
    /// </item>
    /// </list>
    /// The storage provider, the policy provider and the cleanup handler are types rather than settings, so they're
    /// set in code.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="section">The settings, such as <c>builder.Configuration.GetSection("Resourcefull")</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="section"/> has a key that isn't one of these settings, so a misspelled one isn't ignored, or a
    /// value it can't use: one that doesn't parse, is out of range, or isn't a section or list where one belongs.
    /// </exception>
    public ResourcefullOptionsBuilder UseConfiguration(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        ThrowIfUnknownKeys(section, "Versioning", "Expiry", "Telemetry", "ContainerPolicies");

        if (Present(section, "Versioning", "MaxVersions") is { } versioning)
        {
            int? maxVersions = Read<int>(versioning, "MaxVersions");
            Apply(versioning, () => UseVersioning(maxVersions));
        }
        if (Present(section, "Expiry", "Interval") is { } expiry)
        {
            TimeSpan? interval = Read<TimeSpan>(expiry, "Interval");
            Apply(expiry, () => UseExpiry(interval));
        }
        if (Present(section, "Telemetry", "RecordResourceNames") is { } telemetry
            && Read<bool>(telemetry, "RecordResourceNames") is { } recordResourceNames)
        {
            UseTelemetry(options => options.RecordResourceNames = recordResourceNames);
        }
        if (Present(section, "ContainerPolicies", "Defaults", "CacheFor", "KeepLastKnownFor") is { } policies)
        {
            IConfigurationSection? defaults = Present(policies, "Defaults",
                nameof(ResourceContainerPolicy.MaxResourceSize), nameof(ResourceContainerPolicy.MaxContainerSize),
                nameof(ResourceContainerPolicy.MaxResourceCount), nameof(ResourceContainerPolicy.AllowedContentTypes),
                nameof(ResourceContainerPolicy.ReadOnly));
            var policy = defaults is null ? null : new ResourceContainerPolicy
            {
                MaxResourceSize = Read<ResourceSize>(defaults, nameof(ResourceContainerPolicy.MaxResourceSize)),
                MaxContainerSize = Read<ResourceSize>(defaults, nameof(ResourceContainerPolicy.MaxContainerSize)),
                MaxResourceCount = Read<long>(defaults, nameof(ResourceContainerPolicy.MaxResourceCount)),
                AllowedContentTypes = ReadList(defaults, nameof(ResourceContainerPolicy.AllowedContentTypes)),
                ReadOnly = Read<bool>(defaults, nameof(ResourceContainerPolicy.ReadOnly))
            };
            TimeSpan? cacheFor = Read<TimeSpan>(policies, "CacheFor");
            TimeSpan? keepLastKnownFor = Read<TimeSpan>(policies, "KeepLastKnownFor");
            Apply(policies, () => UseContainerPolicies(policy, cacheFor, keepLastKnownFor));
        }
        return this;
    }

    /// <summary>
    /// The section <paramref name="name"/> of <paramref name="parent"/>, if it's there, once its keys are checked. It's
    /// found by its key, since an empty one, such as <c>{}</c> in JSON, has neither a value nor children.
    /// </summary>
    private static IConfigurationSection? Present(IConfiguration parent, string name, params string[] keys)
    {
        if (Child(parent, name) is not { } section) return null;
        if (!string.IsNullOrEmpty(section.Value))
        {
            throw new InvalidOperationException(
                $"The setting {section.Path} must be a section, not \"{section.Value}\".");
        }
        ThrowIfUnknownKeys(section, keys);
        return section;
    }

    // Configuration keys don't depend on case.
    private static IConfigurationSection? Child(IConfiguration parent, string name) =>
        parent.GetChildren()
            .FirstOrDefault(child => string.Equals(child.Key, name, StringComparison.OrdinalIgnoreCase));

    private static void ThrowIfUnknownKeys(IConfiguration section, params string[] keys)
    {
        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (!keys.Contains(child.Key, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{child.Path} isn't a Resourcefull setting. The settings there are {string.Join(", ", keys)}.");
            }
        }
    }

    private static T? Read<T>(IConfigurationSection section, string key) where T : struct, IParsable<T>
    {
        string? text = section[key];
        if (string.IsNullOrEmpty(text)) return null;
        if (T.TryParse(text, CultureInfo.InvariantCulture, out T value)) return value;
        throw new InvalidOperationException(
            $"The setting {ConfigurationPath.Combine(section.Path, key)}, \"{text}\", isn't a valid {typeof(T).Name}.");
    }

    /// <summary>
    /// A list of text, such as a JSON array of strings. An empty one, such as <c>[]</c>, reads as not set, as
    /// <c>null</c> does: configuration can't tell them apart, and an empty list of allowed types would refuse every
    /// upload.
    /// </summary>
    private static string[]? ReadList(IConfigurationSection section, string key)
    {
        if (Child(section, key) is not { } list || (list.Value is null or "" && !list.GetChildren().Any())) return null;
        string? notAList = !string.IsNullOrEmpty(list.Value) ? list.Value
            : list.GetChildren().Any(item => item.Value is null) ? "a section" : null;
        if (notAList is not null)
            throw new InvalidOperationException($"The setting {list.Path} must be a list of text, not {notAList}.");
        return [.. list.GetChildren().Select(item => item.Value!)];
    }

    /// <summary>Calls a <c>Use…</c> method with a section's values, naming the section if it refuses one.</summary>
    private static void Apply(IConfigurationSection section, Action use)
    {
        try
        {
            use();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"The settings in {section.Path} aren't valid: {exception.Message}",
                exception);
        }
    }
}