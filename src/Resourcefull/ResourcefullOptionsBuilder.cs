using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resourcefull.Providers;

namespace Resourcefull;

/// <summary>
/// Configures one store inside <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> or
/// <see cref="ResourcefullServiceCollectionExtensions.AddKeyedResourcefull"/>: its storage provider, versioning, URI
/// provider, telemetry, container policies and expiry.
/// </summary>
public class ResourcefullOptionsBuilder
{
    private bool _expiryRegistered;

    /// <summary>Whether the default policy keeps deleted versions, which needs the sweep of <see cref="UseExpiry"/>.</summary>
    private bool _defaultsKeepDeleted;

    /// <summary>
    /// Gets whether a storage provider has been configured. Exactly one must be, through a provider package's
    /// <c>Use…</c> extension or <see cref="UseProvider{TRepository}(ServiceLifetime)"/>.
    /// </summary>
    public bool ProviderConfigured { get; private set; }

    /// <summary>
    /// Gets the application's service collection, for provider extensions to register their services in.
    /// </summary>
    public IServiceCollection Services { get; init; }

    /// <summary>
    /// Gets the store's name, given to <see cref="ResourcefullServiceCollectionExtensions.AddKeyedResourcefull"/>, or
    /// <see langword="null"/> for the store <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> adds.
    /// The store's services are registered with it as their service key, so a provider registers its own services
    /// under it and resolves them with it; in .NET's own container, a <see langword="null"/> key is the same as no key.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public string? StoreName { get; }

    /// <summary>The name of the store's <see cref="ResourcefullOptions"/>.</summary>
    internal string OptionsName => StoreName ?? Options.DefaultName;

    /// <summary>
    /// Creates a builder that registers one store into <paramref name="services"/>. Applications get one from
    /// <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> rather than creating it.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="storeName">The store's name; <see langword="null"/> for the store without one.</param>
    public ResourcefullOptionsBuilder(IServiceCollection services, string? storeName = null)
    {
        Services = services;
        StoreName = storeName;
    }

    /// <summary>
    /// Uses <typeparamref name="TRepository"/> as the storage provider. Provider packages call this from their own
    /// <c>Use…</c> extension; a custom provider can be registered with it directly.
    /// </summary>
    /// <typeparam name="TRepository">The provider's class, derived from <see cref="ResourceRepository"/>.</typeparam>
    /// <param name="lifetime">How long one instance serves: singleton for a provider that holds its own state.</param>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    public ResourcefullOptionsBuilder UseProvider<TRepository>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TRepository : ResourceRepository
    {
        if (ProviderConfigured)
        {
            throw new InvalidOperationException("Provider has already been configured.");
        }

        Replace(new ServiceDescriptor(typeof(ResourceRepository), StoreName, typeof(TRepository), lifetime));
        ProviderConfigured = true;
        return this;
    }

    /// <summary>
    /// Uses the provider that <paramref name="create"/> makes, for a provider that needs services registered for this
    /// store; resolve them with <see cref="StoreName"/> as their key. Otherwise works like
    /// <see cref="UseProvider{TRepository}(ServiceLifetime)"/>.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <typeparam name="TRepository">The provider's class, derived from <see cref="ResourceRepository"/>.</typeparam>
    /// <param name="create">Makes the provider from the application's services.</param>
    /// <param name="lifetime">How long one instance serves: singleton for a provider that holds its own state.</param>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="create"/> is null.</exception>
    public ResourcefullOptionsBuilder UseProvider<TRepository>(Func<IServiceProvider, TRepository> create,
        ServiceLifetime lifetime = ServiceLifetime.Scoped) where TRepository : ResourceRepository
    {
        ArgumentNullException.ThrowIfNull(create);
        if (ProviderConfigured)
        {
            throw new InvalidOperationException("Provider has already been configured.");
        }

        Replace(new ServiceDescriptor(typeof(ResourceRepository), StoreName, (services, _) => create(services), lifetime));
        ProviderConfigured = true;
        return this;
    }

    /// <summary>
    /// Registers a service of this store's, replacing the one it had. With no store name, the descriptor isn't keyed, as
    /// before stores had names.
    /// </summary>
    private void Replace(ServiceDescriptor descriptor)
    {
        Services.RemoveAllKeyed(descriptor.ServiceType, StoreName);
        Services.Add(descriptor);
    }

    /// <summary>
    /// Uses <typeparamref name="T"/> to give containers and resources their <c>Uri</c>, replacing any URI provider set
    /// before. It is registered as a scoped service, so it can depend on other services.
    /// </summary>
    /// <typeparam name="T">The <see cref="IResourceUriProvider"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder UseUriProvider<T>() where T : class, IResourceUriProvider
    {
        Replace(new ServiceDescriptor(typeof(IResourceUriProvider), StoreName, typeof(T), ServiceLifetime.Scoped));
        return this;
    }

    /// <summary>
    /// Turns on versioning: each name keeps up to <paramref name="maxVersions"/> versions, so an upload with
    /// <see cref="ResourceWriteMode.Replace"/> keeps the version it replaces as an older one, and the oldest past that
    /// limit are pruned. It's the store-wide default for <see cref="ContainerPolicy.MaxVersions"/>: a container's
    /// policy, or the defaults given to <see cref="UseContainerPolicies"/>, take precedence. Without versioning, a name
    /// keeps only its current version.
    /// </summary>
    /// <param name="maxVersions">How many versions of each name to keep, including the current one; 100 when null.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxVersions"/> is zero or negative.</exception>
    public ResourcefullOptionsBuilder UseVersioning(int? maxVersions = null)
    {
        if (maxVersions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxVersions), maxVersions, "Keep at least one version.");
        }

        Services.Configure<ResourcefullOptions>(OptionsName, options => options.MaxVersions = maxVersions ?? 100);
        return this;
    }

    /// <summary>
    /// Configures what Resourcefull's spans and metrics record. Telemetry is published whether or not this is called,
    /// under the names in <see cref="ResourcefullTelemetry"/>; this only changes what goes into it.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="configure">A callback to set <see cref="ResourcefullTelemetryOptions"/>.</param>
    public ResourcefullOptionsBuilder UseTelemetry(Action<ResourcefullTelemetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure<ResourcefullOptions>(OptionsName, options => configure(options.Telemetry));
        return this;
    }

    /// <summary>
    /// Turns on container policies, with <paramref name="defaults"/> for every container, replacing any set before.
    /// Add <see cref="UsePolicyProvider{TProvider}"/> to give containers their own. Without either, no policy is checked.
    /// <para>Coming in 4.0.</para>
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
    public ResourcefullOptionsBuilder UseContainerPolicies(ContainerPolicy? defaults = null, TimeSpan? cacheFor = null,
        TimeSpan? keepLastKnownFor = null)
    {
        if (cacheFor < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheFor), cacheFor, "The cache time can't be negative.");
        if (keepLastKnownFor < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(keepLastKnownFor), keepLastKnownFor,
                "The time can't be negative.");
        }

        _defaultsKeepDeleted = defaults?.KeepDeletedFor > TimeSpan.Zero;
        Replace(new ServiceDescriptor(typeof(ContainerPolicySettings), StoreName, (_, _) => new ContainerPolicySettings(
            defaults ?? new ContainerPolicy(), cacheFor ?? TimeSpan.FromSeconds(5), keepLastKnownFor ?? TimeSpan.FromMinutes(5)),
            ServiceLifetime.Singleton));
        string? storeName = StoreName;
        Replace(new ServiceDescriptor(typeof(ContainerPolicies), StoreName, (services, _) => new ContainerPolicies(
            services.GetRequiredStoreService<ContainerPolicySettings>(storeName),
            services.GetRequiredStoreService<ResourceRepository>(storeName),
            services.GetStoreService<IResourcePolicyProvider>(storeName)), ServiceLifetime.Scoped));
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="TProvider"/> to give containers their own policies, replacing any policy provider set
    /// before, and turns on container policies with no defaults if <see cref="UseContainerPolicies"/> hasn't. It's
    /// registered as a scoped service, so it can depend on other services, such as the application's database context.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <typeparam name="TProvider">The <see cref="IResourcePolicyProvider"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder UsePolicyProvider<TProvider>() where TProvider : class, IResourcePolicyProvider
    {
        Replace(new ServiceDescriptor(typeof(IResourcePolicyProvider), StoreName, typeof(TProvider), ServiceLifetime.Scoped));
        if (!Services.Any(d => d.ServiceType == typeof(ContainerPolicySettings) && Equals(d.ServiceKey, StoreName)))
            UseContainerPolicies();
        return this;
    }

    /// <summary>
    /// Deletes expired versions in the background: a hosted service calls
    /// <see cref="IResourceServiceClient.DeleteExpiredAsync"/> when the application starts and every
    /// <paramref name="interval"/> after, so a version is gone within one interval of its
    /// <see cref="ResourceInfo.ExpiresAt"/>. Every instance of the application can run it. With a provider that doesn't
    /// support expiry (<see cref="ResourceRepository.SupportsExpiry"/>), the service throws
    /// <see cref="InvalidOperationException"/> when the application starts. It also changes pruning: a version past
    /// the container's <see cref="ContainerPolicy.MaxVersions"/> is set to expire five minutes later
    /// instead of being deleted at once, so a download already reading it can finish.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="interval">How often to look for expired versions; one minute when null.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> isn't positive.</exception>
    public ResourcefullOptionsBuilder UseExpiry(TimeSpan? interval = null)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");

        Replace(new ServiceDescriptor(typeof(ExpirySettings), StoreName, new ExpirySettings(interval ?? TimeSpan.FromMinutes(1))));
        Services.Configure<ResourcefullOptions>(OptionsName,
            options => options.AbandonedUploadsDeletedAfter = ResourceServiceClient.AbandonedUploadAge);
        if (!_expiryRegistered)
        {
            // One sweep for each store, so not TryAddEnumerable, which would keep only the first store's.
            string? storeName = StoreName;
            Services.AddSingleton<IHostedService>(services => new ExpiryService(services.GetRequiredService<IServiceScopeFactory>(),
                services.GetRequiredStoreService<ExpirySettings>(storeName), storeName,
                services.GetRequiredService<ILogger<ExpiryService>>()));
            _expiryRegistered = true;
        }
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="THandler"/> to hear about every version Resourcefull deletes by itself, before it's
    /// deleted, replacing any cleanup handler set before. It's registered as a scoped service, so it can depend on other
    /// services, such as the application's audit client.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <typeparam name="THandler">The <see cref="IResourceCleanupHandler"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder UseCleanupHandler<THandler>() where THandler : class, IResourceCleanupHandler
    {
        Replace(new ServiceDescriptor(typeof(IResourceCleanupHandler), StoreName, typeof(THandler), ServiceLifetime.Scoped));
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="TInspector"/> to look at the content of every upload and import before it's stored,
    /// and refuse it if it must, replacing any upload inspector set before. It's registered as a scoped service, so it
    /// can depend on other services, such as the application's malware scanner.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <typeparam name="TInspector">The <see cref="IResourceUploadInspector"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder UseUploadInspector<TInspector>() where TInspector : class, IResourceUploadInspector
    {
        Replace(new ServiceDescriptor(typeof(IResourceUploadInspector), StoreName, typeof(TInspector), ServiceLifetime.Scoped));
        return this;
    }

    /// <summary>
    /// Makes listings able to find resources by these metadata keys, through <see cref="ResourceListOptions.MetadataMatch"/>:
    /// the provider keeps an index of each current version's value for each key, so a search reads only what matches.
    /// A value of one of these keys can be at most <see cref="ResourceMetadataMatch.MaxValueLength"/> characters, and a
    /// write that gives a longer one throws <see cref="ArgumentException"/>. Versions stored before a key was added
    /// aren't found by it until <see cref="IResourceContainerClient.IndexMetadataAsync"/> indexes their container. Calling
    /// this method again adds more keys.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="keys">The keys, compared ordinally (case-sensitive); each at most <see cref="ResourceMetadataMatch.MaxKeyLength"/> characters.</param>
    /// <exception cref="ArgumentException">A key is empty, too long, or starts or ends with whitespace.</exception>
    public ResourcefullOptionsBuilder UseMetadataSearch(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (string key in keys)
        {
            ArgumentException.ThrowIfNullOrEmpty(key, nameof(keys));
            // Some databases compare as if the shorter string were padded with spaces, so such keys wouldn't stay apart.
            if (char.IsWhiteSpace(key[0]) || char.IsWhiteSpace(key[^1]))
                throw new ArgumentException($"The metadata key '{key}' starts or ends with whitespace, so it can't be searched.", nameof(keys));
            if (key.Length > ResourceMetadataMatch.MaxKeyLength)
            {
                throw new ArgumentException(
                    $"The metadata key {key} is longer than the {ResourceMetadataMatch.MaxKeyLength} characters a searchable key can be.",
                    nameof(keys));
            }
        }
        Services.Configure<ResourcefullOptions>(OptionsName, options => options._searchableMetadataKeys.UnionWith(keys));
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
    /// <c>Defaults</c>, which takes the properties of <see cref="ContainerPolicy"/>. Sizes are written as
    /// <see cref="ResourceSize.Parse(string, IFormatProvider?)"/> reads them, such as <c>100 MB</c>.
    /// </item>
    /// </list>
    /// The storage provider, the policy provider, the cleanup handler and the upload inspector are types rather than
    /// settings, so they're set in code.
    /// <para>Coming in 4.0.</para>
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
                nameof(ContainerPolicy.MaxResourceSize), nameof(ContainerPolicy.MaxContainerSize),
                nameof(ContainerPolicy.MaxResourceCount), nameof(ContainerPolicy.AllowedContentTypes),
                nameof(ContainerPolicy.ReadOnly), nameof(ContainerPolicy.MaxVersions),
                nameof(ContainerPolicy.RetainFor), nameof(ContainerPolicy.KeepDeletedFor));
            int? maxVersions = defaults is null ? null : Read<int>(defaults, nameof(ContainerPolicy.MaxVersions));
            TimeSpan? cacheFor = Read<TimeSpan>(policies, "CacheFor");
            TimeSpan? keepLastKnownFor = Read<TimeSpan>(policies, "KeepLastKnownFor");
            // Built in Apply, so a value the policy refuses, such as MaxVersions of 0, names the section.
            Apply(policies, () => UseContainerPolicies(defaults is null ? null : new ContainerPolicy
            {
                MaxResourceSize = Read<ResourceSize>(defaults, nameof(ContainerPolicy.MaxResourceSize)),
                MaxContainerSize = Read<ResourceSize>(defaults, nameof(ContainerPolicy.MaxContainerSize)),
                MaxResourceCount = Read<long>(defaults, nameof(ContainerPolicy.MaxResourceCount)),
                AllowedContentTypes = ReadList(defaults, nameof(ContainerPolicy.AllowedContentTypes)),
                ReadOnly = Read<bool>(defaults, nameof(ContainerPolicy.ReadOnly)),
                MaxVersions = maxVersions,
                RetainFor = Read<TimeSpan>(defaults, nameof(ContainerPolicy.RetainFor)),
                KeepDeletedFor = Read<TimeSpan>(defaults, nameof(ContainerPolicy.KeepDeletedFor))
            }, cacheFor, keepLastKnownFor));
        }
        return this;
    }

    /// <summary>
    /// Checks the settings that only make sense together, once they're all made: a default policy that keeps deleted
    /// versions needs <see cref="UseExpiry"/>, whose sweep purges them.
    /// </summary>
    /// <exception cref="InvalidOperationException">They don't fit together.</exception>
    internal void ThrowIfInconsistent()
    {
        if (_defaultsKeepDeleted && !_expiryRegistered)
        {
            throw new InvalidOperationException($"The default container policy sets {nameof(ContainerPolicy.KeepDeletedFor)}, " +
                $"but nothing would purge the deleted versions it keeps: call {nameof(UseExpiry)} too, whose sweep does.");
        }
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