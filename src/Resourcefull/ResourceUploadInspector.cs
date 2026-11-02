using System.Diagnostics;
using System.IO.Pipelines;

namespace Resourcefull;

/// <summary>
/// Looks at each upload's content before it's stored, and can refuse it: to scan it for malware, for example, or to
/// check that it really is the type it claims to be. Register one with
/// <see cref="ResourcefullOptionsBuilder.UseUploadInspector{TInspector}"/>; it's a scoped service, so it can depend on
/// the application's own services.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <remarks>
/// <para>
/// The inspector reads the content while the provider stores it, so the content is read only once and never held
/// whole. The new version stays out of sight until the inspector answers: when it refuses the content or throws, the
/// upload fails with a <see cref="ResourceContentRefusedException"/> or the inspector's exception, and nothing is
/// stored. An upload that finishes before the inspector does waits for its answer.
/// </para>
/// <para>
/// Uploads and imports are inspected. Copies, moves and restores aren't, since their content is already stored.
/// </para>
/// </remarks>
public interface IResourceUploadInspector
{
    /// <summary>Inspects an upload's content, and says whether it may be stored.</summary>
    /// <param name="upload">
    /// The upload: where it's going, what it declares, and its content, which the inspector can read as far as it needs.
    /// </param>
    /// <param name="cancellationToken">
    /// Canceled when the upload fails or is canceled; stop then, as the upload waits for the inspector to return.
    /// </param>
    /// <returns><see cref="ResourceInspectionResult.Accepted"/>, or a refusal with its reason.</returns>
    ValueTask<ResourceInspectionResult> InspectAsync(ResourceUploadInspection upload, CancellationToken cancellationToken);
}

/// <summary>
/// An upload for an <see cref="IResourceUploadInspector"/> to inspect.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="ContainerName">The container it's going into.</param>
/// <param name="ResourceName">The resource's name.</param>
/// <param name="VersionId">The ID the new version will have if it's stored.</param>
/// <param name="ContentType">The content type the upload declares, which nothing has checked against the content.</param>
/// <param name="ContentLength">The length the upload declares, if it declares one.</param>
/// <param name="Metadata">The upload's metadata.</param>
/// <param name="Content">
/// The content, as it arrives: read-only and forward-only. Read it as far as the inspection needs; there's no need to
/// read it to its end.
/// </param>
public sealed record ResourceUploadInspection(string ContainerName, string ResourceName, string VersionId,
    string? ContentType, long? ContentLength, IReadOnlyDictionary<string, string> Metadata, Stream Content);

/// <summary>
/// An <see cref="IResourceUploadInspector"/>'s answer: the content may be stored, or it's refused, with the reason.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceInspectionResult
{
    private ResourceInspectionResult(string? reason) => Reason = reason;

    /// <summary>The content may be stored.</summary>
    public static ResourceInspectionResult Accepted { get; } = new(null);

    /// <summary>The content is refused, and nothing is stored.</summary>
    /// <param name="reason">
    /// Why, for the <see cref="ResourceContentRefusedException"/> the upload fails with; ASP.NET Core apps may send it
    /// to the client, so it shouldn't give away anything the client mustn't see.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null, empty or only white space.</exception>
    public static ResourceInspectionResult Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ResourceInspectionResult(reason);
    }

    /// <summary>Whether the content may be stored.</summary>
    public bool IsAccepted => Reason is null;

    /// <summary>Why the content was refused, or <see langword="null"/> when it was accepted.</summary>
    public string? Reason { get; }
}

/// <summary>
/// Content being stored, which the upload inspector reads at the same time, through a pipe that holds up to a MiB, so
/// neither runs far ahead of the other. At the content's end, before the provider can make the version current, it
/// waits for the inspector's answer and throws if the content was refused; a refusal or failure that comes sooner
/// throws at the next read. It can't seek, so a provider can't read the content twice.
/// </summary>
internal sealed class InspectedContentStream : Stream
{
    private const int PipeSize = 1024 * 1024;

    private readonly Stream _content;
    private readonly ResourceLocation _location;
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: PipeSize, resumeWriterThreshold: PipeSize / 2,
        useSynchronizationContext: false));
    private readonly CancellationTokenSource _stop;
    private readonly Task<ResourceInspectionResult> _inspection;
    private bool _feeding = true;
    private bool _ended;

    private InspectedContentStream(IResourceUploadInspector inspector, NewResourceVersion version, long? contentLength, Stream content,
        ClientTelemetry telemetry, string operation, CancellationToken cancellationToken)
    {
        _content = content;
        _location = version.Location;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var upload = new ResourceUploadInspection(version.Location.ContainerName, version.Location.ResourceName, version.VersionId,
            version.ContentType, contentLength, version.Metadata.ToDictionary().AsReadOnly(), _pipe.Reader.AsStream());
        // On the thread pool, so an inspector that reads synchronously can't block the upload that feeds it.
        _inspection = Task.Run(() => InspectAsync(inspector, upload, telemetry, operation, _stop.Token));
    }

    /// <summary>
    /// Starts the inspector reading <paramref name="content"/> as it's read through the stream returned, telling it
    /// <paramref name="contentLength"/>, the length the upload declares, if any.
    /// </summary>
    public static InspectedContentStream Start(IResourceUploadInspector inspector, NewResourceVersion version, long? contentLength,
        Stream content, ClientTelemetry telemetry, string operation, CancellationToken cancellationToken) =>
        new(inspector, version, contentLength, content, telemetry, operation, cancellationToken);

    private async Task<ResourceInspectionResult> InspectAsync(IResourceUploadInspector inspector, ResourceUploadInspection upload,
        ClientTelemetry telemetry, string operation, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        using Activity? activity = telemetry.StartSpan("inspect", upload.ContainerName);
        string outcome = "failed";
        Exception? failure = null;
        try
        {
            ResourceInspectionResult result = await inspector.InspectAsync(upload, cancellationToken);
            outcome = result.IsAccepted ? "accepted" : "refused";
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        catch (Exception exception)
        {
            failure = exception;
            telemetry.RecordFailure(activity, exception);
            throw;
        }
        finally
        {
            // Whatever the inspector didn't read is no longer fed to it.
            await _pipe.Reader.CompleteAsync();
            telemetry.RecordInspection(activity, operation, outcome, failure, start);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Awaits don't resume on the caller's context: Read blocks on ReadAsync, and would deadlock a single-threaded one.
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_inspection.IsCompleted) await ThrowIfRefusedAsync(cancellationToken).ConfigureAwait(false);
        int read = await _content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            _ended = true;
            await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
            await ThrowIfRefusedAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_feeding)
        {
            // Completed once the inspector has answered, so it's fed no further.
            _feeding = !(await _pipe.Writer.WriteAsync(buffer[..read], cancellationToken).ConfigureAwait(false)).IsCompleted;
        }
        return read;
    }

    /// <summary>
    /// After the provider has stored the version, waits for the inspector's answer on what the provider read, and throws
    /// as the read at the end would have. Does nothing when the provider did read to the end, since that read already
    /// waited for the answer. A provider must read to the end, so the version isn't current before the answer; this
    /// catches one that doesn't.
    /// </summary>
    public async Task ThrowIfStoredUnansweredAsync(CancellationToken cancellationToken)
    {
        if (_ended) return;
        await _pipe.Writer.CompleteAsync();
        await ThrowIfRefusedAsync(cancellationToken);
    }

    /// <summary>Waits for the inspector's answer, throwing its exception, or one for a refusal.</summary>
    private async Task ThrowIfRefusedAsync(CancellationToken cancellationToken)
    {
        ResourceInspectionResult result = await _inspection.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsAccepted)
        {
            throw new ResourceContentRefusedException(
                $"The resource {_location.ResourceName} wasn't stored in the container {_location.ContainerName}, because the " +
                $"upload inspector refused it: {result.Reason}", result.Reason!)
            {
                ContainerName = _location.ContainerName, ResourceName = _location.ResourceName
            };
        }
    }

    /// <summary>Stops an inspector that hasn't answered, as the upload failed, and waits for it to return.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (!_inspection.IsCompleted)
        {
            await _stop.CancelAsync();
            await _pipe.Writer.CompleteAsync(new OperationCanceledException("The upload failed before the inspector answered."));
        }
        try
        {
            await _inspection;
        }
        catch
        {
            // Already recorded on the inspection's span, and the upload has failed or been told why.
        }
        _stop.Dispose();
        await base.DisposeAsync();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
