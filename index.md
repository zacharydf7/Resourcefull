---
_layout: landing
title: Resourcefull
description: Store and serve files of any type and size from infrastructure you run yourself, through one .NET API.
---

<div class="rf-home">

<section class="rf-hero">
<div class="rf-wrap">
<div>
<h1>Files for .NET apps, <span>stored where you choose</span></h1>
<p class="rf-lede">Resourcefull stores and serves files of any type and size from infrastructure you run yourself.
Upload, download, list and version them through one client API, and keep the bytes in SQL Server, in memory for
tests, or in a storage provider of your own.</p>
<div class="rf-actions">
<a class="rf-btn rf-btn-primary" href="docs/getting-started.md">Get started <i class="bi bi-arrow-right"></i></a>
<a class="rf-btn rf-btn-secondary" href="docs/index.md">Read the docs</a>
<a class="rf-btn rf-btn-secondary" href="https://github.com/zacharydf7/Resourcefull"><i class="bi bi-github"></i> GitHub</a>
</div>
<div class="rf-install">$ <b>dotnet add package</b> Resourcefull.SqlServer</div>
</div>
<div class="rf-code">
<div class="rf-code-bar"><i></i><i></i><i></i><span>Program.cs</span></div>

<pre><code class="lang-csharp">builder.Services.AddResourcefull(options =&gt; options
    .UseSqlServer(connectionString)
    .UseVersioning(maxVersions: 10));

// Streams the body into storage, at any size.
app.MapPut("/files/{container}/{**name}",
    (string container, string name,
     HttpRequest request, IResourceServiceClient files) =&gt;
        files.GetResourceContainerClient(container)
             .UploadFromRequestAsync(name, request));

// Sends the file with ETags and range requests (3.2).
app.MapGet("/files/{container}/{**name}",
    (string container, string name,
     IResourceServiceClient files) =&gt;
        Results.Resource(
            files.GetResourceContainerClient(container),
            name));</code></pre>

</div>
</div>
</section>

<section>
<div class="rf-wrap">
<div class="rf-section-head">
<h2>Everything files need, in one library</h2>
<p>The parts every application ends up writing around file storage, built once, tested against every provider, and
documented for people who aren't storage specialists.</p>
</div>
<div class="rf-grid">
<a class="rf-card" href="docs/uploading.md">
<h3><i class="bi bi-arrow-down-up"></i>Any size, flat memory</h3>
<p>Uploads and downloads stream, so a 5 GB video costs the same memory as a thumbnail. Every file's length and
SHA-256 hash are measured as it's stored.</p>
</a>
<a class="rf-card" href="docs/versioning.md">
<h3><i class="bi bi-clock-history"></i>Versions you can restore</h3>
<p>Keep the last few versions of every file, list them, and bring an older one back. Pruning keeps storage in
check by itself.</p>
</a>
<a class="rf-card" href="docs/concurrency.md">
<h3><i class="bi bi-shield-check"></i>Safe under load</h3>
<p>Many servers can write the same names at once. Conditional writes let exactly one of several editors save,
without locks of your own.</p>
</a>
<a class="rf-card" href="docs/aspnet-core/index.md">
<h3><i class="bi bi-globe2"></i>Made for ASP.NET Core</h3>
<p>Serve downloads with ETags, 304s and range requests <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>, accept uploads with
preconditions, and turn errors into the right status codes.</p>
</a>
<a class="rf-card" href="docs/container-policies.md">
<h3><i class="bi bi-sliders"></i>Quotas and rules <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span></h3>
<p>Limit each container's file sizes, total size and file count, allow only certain file types, or make a
container read-only.</p>
</a>
<a class="rf-card" href="docs/sharing-links.md">
<h3><i class="bi bi-link-45deg"></i>Links that expire <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span></h3>
<p>Let someone without an account download one file, until a time you choose, with a signed link your
application creates.</p>
</a>
<a class="rf-card" href="docs/expiry.md">
<h3><i class="bi bi-hourglass-split"></i>Files that clean up after themselves <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span></h3>
<p>Give uploads an expiry and they're deleted when it passes, with a hook that lets you record every deletion
for auditing.</p>
</a>
<a class="rf-card" href="docs/backup-and-move.md">
<h3><i class="bi bi-archive"></i>Backups in one call <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span></h3>
<p>Export a whole container, every version, to a zip file, and import it into another store with its IDs and
history intact.</p>
</a>
<a class="rf-card" href="docs/telemetry.md">
<h3><i class="bi bi-activity"></i>Observable from day one <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span></h3>
<p>OpenTelemetry spans and metrics for every operation, and health checks that tell your orchestrator when
storage is down.</p>
</a>
</div>
</div>
</section>

<section class="rf-paths">
<div class="rf-wrap">
<div class="rf-section-head">
<h2>Find your way</h2>
<p>The documentation is grouped by what you're trying to do.</p>
</div>
<div class="rf-grid">
<div class="rf-path">
<h3>Working with files</h3>
<ul>
<li><a href="docs/containers.md">Containers</a> and <a href="docs/names.md">names</a></li>
<li><a href="docs/uploading.md">Uploading</a> and <a href="docs/downloading.md">downloading</a></li>
<li><a href="docs/versioning.md">Versioning</a> and <a href="docs/copying-and-moving.md">copying</a></li>
</ul>
</div>
<div class="rf-path">
<h3>Web apps</h3>
<ul>
<li><a href="docs/aspnet-core/securing-endpoints.md">Securing file endpoints</a></li>
<li><a href="docs/aspnet-core/serving-downloads.md">Serving downloads</a></li>
<li><a href="docs/aspnet-core/accepting-uploads.md">Accepting uploads</a></li>
<li><a href="docs/sharing-links.md">Sharing files with links</a></li>
</ul>
</div>
<div class="rf-path">
<h3>Managing storage</h3>
<ul>
<li><a href="docs/container-policies.md">Container policies</a></li>
<li><a href="docs/expiry.md">Expiring files</a></li>
<li><a href="docs/backup-and-move.md">Backing up and moving</a></li>
</ul>
</div>
<div class="rf-path">
<h3>Running in production</h3>
<ul>
<li><a href="docs/providers/sql-server.md">Setting up SQL Server</a></li>
<li><a href="docs/health-checks.md">Health checks</a> and <a href="docs/telemetry.md">telemetry</a></li>
<li><a href="docs/troubleshooting.md">Troubleshooting</a></li>
</ul>
</div>
<div class="rf-path">
<h3>Testing and extending</h3>
<ul>
<li><a href="docs/providers/in-memory.md">Testing with the in-memory provider</a></li>
<li><a href="docs/providers/writing-a-provider.md">Writing a provider</a></li>
</ul>
</div>
<div class="rf-path">
<h3>Reference</h3>
<ul>
<li><a href="api/index.md">API reference</a></li>
<li><a href="docs/errors.md">Errors</a> and <a href="CHANGELOG.md">changelog</a></li>
<li><a href="docs/releases.md">Releases and roadmap</a></li>
<li><a href="docs/upgrading.md">Upgrading and versions</a></li>
</ul>
</div>
</div>
</div>
</section>

<section>
<div class="rf-wrap">
<div class="rf-section-head">
<h2>Your storage, your choice</h2>
<p>Your code talks to one API. The provider decides where the bytes go, and you can change it without touching the
rest of your application.</p>
</div>
<div class="rf-providers">
<a class="rf-card" href="docs/providers/sql-server.md">
<h3><i class="bi bi-database"></i>SQL Server</h3>
<p><code>Resourcefull.SqlServer</code> for production: streamed in chunks, transactional, with migrations and
retries built in.</p>
</a>
<a class="rf-card" href="docs/providers/in-memory.md">
<h3><i class="bi bi-cpu"></i>In memory</h3>
<p><code>Resourcefull.InMemory</code> for tests and local development: the same behavior, nothing to set up.</p>
</a>
<a class="rf-card" href="docs/providers/writing-a-provider.md">
<h3><i class="bi bi-plug"></i>Your own</h3>
<p>Implement one interface to store files anywhere else, and check it with the same contract tests the built-in
providers pass.</p>
</a>
</div>
</div>
</section>

</div>
