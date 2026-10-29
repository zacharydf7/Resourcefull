# In-memory

`Resourcefull.InMemory` keeps resources in process memory. Use it for tests, local development and demos; it isn't
meant for production.

```csharp
builder.Services.AddResourcefull(options => options.UseInMemory());
```

- **One store per service provider.** It's registered as a singleton, so everything resolved from one service
  provider shares one store, and the store disappears with the process. Separate processes, or separate test hosts,
  don't see each other's resources.
- **The whole content is held in memory.** Each version's bytes are kept in full, so large files cost their full size.
- **It behaves like SQL Server where code could notice.** Names follow the same rules, versions are ordered and pruned
  the same way, conflicts and missing containers raise the same exceptions, and download streams can't seek either.
  The repository checks this by running one shared test suite against both providers. One difference: a download that
  has started reads its version to the end even if the version is deleted meanwhile, where SQL Server throws
  `IOException`.

## In tests

Build a service provider per test, so each test starts with an empty store. With `Invoices` from
[Getting started](../getting-started.md#store-and-read-a-file):

```csharp
[Fact]
public async Task SaveAsync_ShouldStoreTheReport_WhenGivenAFile()
{
    var services = new ServiceCollection();
    services.AddResourcefull(options => options.UseInMemory());
    await using ServiceProvider provider = services.BuildServiceProvider();
    var resources = provider.GetRequiredService<IResourceServiceClient>();

    await new Invoices(resources).SaveAsync("q3.pdf", new MemoryStream("%PDF"u8.ToArray()), CancellationToken.None);

    ResourceInfo? stored = await resources.GetResourceContainerClient("invoices").GetResourceInfoAsync("q3.pdf");
    Assert.Equal(4, stored?.ContentLength);
}
```

For tests of time-dependent code, register a `TimeProvider` before building the provider; the in-memory store stamps
`LastModified` with it.
