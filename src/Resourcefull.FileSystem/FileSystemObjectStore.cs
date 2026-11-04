using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Resourcefull.ObjectStorage;

namespace Resourcefull.FileSystem;

/// <summary>A directory, as <see cref="ObjectStoreRepository"/> uses it: each key a file, at a path made of its segments.</summary>
/// <remarks>
/// <para>
/// Each segment is escaped so it means the same on every filesystem. Lowercase ASCII letters, digits and the punctuation
/// every filesystem takes stay as they are. An uppercase letter becomes <c>^</c> and the letter in lowercase, so a
/// filesystem that ignores case, as Windows and macOS do by default, keeps names that differ only in case apart. Any
/// other ASCII character becomes <c>~</c> and two hex digits, and anything beyond ASCII becomes <c>~u</c> and four hex
/// digits, or <c>~v</c> and six for a character beyond U+FFFF, so no filesystem changes its case or its Unicode
/// normalization. That at most triples a segment, so keys ask for segments of at most <see cref="KeySegmentBytes"/>,
/// which keeps every file name within the 255 characters filesystems allow. No escaped segment starts with a
/// <c>.</c>, so the store's own directories, <c>.tmp</c> and <c>.locks</c>, can't be taken for keys.
/// </para>
/// <para>
/// A write goes to a file under <c>.tmp</c>, flushed to disk, which is then moved into place, so no one sees a file
/// half-written. A conditional write holds one of <see cref="LockCount"/> lock files under <c>.locks</c> while it compares
/// and moves, so writers that share the directory, in this process or another, take turns; its ETag is a hash of the
/// file's bytes. Listings read a directory at a time and sort it, since filesystems list in no particular order.
/// </para>
/// </remarks>
/// <param name="root">The directory that holds everything, which must exist.</param>
internal sealed class FileSystemObjectStore(string root) : IObjectStore
{
    /// <summary>How many lock files conditional writes share, chosen by a hash of the key.</summary>
    internal const int LockCount = 256;

    /// <summary>How long a writer waits between tries for a lock, or to move or delete a file another process has open.</summary>
    private static readonly TimeSpan RetryWait = TimeSpan.FromMilliseconds(10);

    /// <summary>How long a writer keeps trying for a lock, or to move or delete a file, before it fails.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most UTF-8 bytes a path may have: macOS refuses one past 1,023, and Linux one past 4,095. Windows, given the
    /// long form .NET uses, takes about 32,000 characters. Tests set it lower.
    /// </summary>
    internal int MaxPathBytes { get; init; } = OperatingSystem.IsMacOS() ? 1023 : OperatingSystem.IsWindows() ? 32_000 : 4095;

    /// <summary>
    /// Called with the destination path before each attempt to move a file into place. Tests set it, to change what's on
    /// disk or to make the move fail.
    /// </summary>
    internal Action<string>? BeforeMove { get; init; }

    /// <summary>Windows' answers when another process has a file open in a way that keeps it from being replaced or deleted.</summary>
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    /// <summary>How old a file under <c>.tmp</c> must be before it's taken for one a stopped write left behind.</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromDays(1);

    /// <summary>How often a write looks for such files.</summary>
    private static readonly TimeSpan TempSweepInterval = TimeSpan.FromHours(1);

    /// <summary>How much of a file a copy reads at a time.</summary>
    private const int CopyBufferSize = 81920;

    private const FileShare ReadersShare = FileShare.ReadWrite | FileShare.Delete;

    private readonly string _temp = Path.Combine(root, ".tmp");
    private readonly string _locks = Path.Combine(root, ".locks");
    private readonly SemaphoreSlim[] _gates = [.. Enumerable.Range(0, LockCount).Select(_ => new SemaphoreSlim(1, 1))];
    private long _nextTempSweep;

    public string ConditionalWrites => "exclusive locks on the files under .locks";

    /// <summary>No limit of its own: a file can be as large as the disk has room for.</summary>
    public long MaxUploadLength => long.MaxValue;

    /// <inheritdoc cref="MaxUploadLength"/>
    public long MaxObjectLength => long.MaxValue;

    /// <summary>No limit of its own, since a join copies the parts itself.</summary>
    public int MaxJoinedParts => int.MaxValue;

    /// <summary>A third of the 255 characters a file name may have, less room for the <c>!</c> that ends a name.</summary>
    public int KeySegmentBytes => 80;

    public async Task<(byte[]? Body, string? ETag)> GetAsync(string key, CancellationToken cancellationToken)
    {
        // Checked first, since a file's absence is found without waiting on anything that would see the token.
        cancellationToken.ThrowIfCancellationRequested();
        string path = PathOf(key);
        byte[]? body = Fits(path) ? await ReadAllAsync(path, cancellationToken).ConfigureAwait(false) : null;
        return (body, body is null ? null : ETagOf(body));
    }

    public async Task<bool> TryPutAsync(string key, byte[] body, string? etag)
    {
        string path = WritablePathOf(key);
        using (await LockAsync(key).ConfigureAwait(false))
        {
            byte[]? current = await ReadAllAsync(path, CancellationToken.None).ConfigureAwait(false);
            if (etag is null ? current is not null : current is null || ETagOf(current) != etag) return false;
            await WriteAsync(path, null, file => file.WriteAsync(body).AsTask(), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
    }

    public Task UploadAsync(string key, Stream content, CancellationToken cancellationToken) =>
        WriteAsync(WritablePathOf(key), key, file => content.CopyToAsync(file, CopyBufferSize, cancellationToken), cancellationToken);

    /// <summary>Copies the parts into the file one after another, since the filesystem has no way to join files.</summary>
    public Task JoinAsync(string key, IReadOnlyList<ObjectRange> parts, CancellationToken cancellationToken) =>
        WriteAsync(WritablePathOf(key), key, async file =>
        {
            byte[] buffer = new byte[CopyBufferSize];
            foreach (ObjectRange part in parts)
            {
                var source = new FileStream(PathOf(part.Key), FileMode.Open, FileAccess.Read, ReadersShare, 1,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (source.ConfigureAwait(false))
                {
                    source.Seek(part.Offset, SeekOrigin.Begin);
                    for (long left = part.Length; left > 0;)
                    {
                        int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0) throw new EndOfStreamException($"{part.Key} is shorter than the part of it to join.");
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        left -= read;
                    }
                }
            }
        }, cancellationToken);

    public Task<ObjectRead?> OpenReadAsync(string key, long start, long end, long length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = PathOf(key);
        if (!Fits(path)) return Task.FromResult<ObjectRead?>(null);
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, ReadersShare, CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            ThrowIfRootMissing();
            return Task.FromResult<ObjectRead?>(null);
        }
        file.Seek(start, SeekOrigin.Begin);
        return Task.FromResult<ObjectRead?>(new ObjectRead(new RangeStream(file, end - start), file));
    }

    public IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
        ListFiles(prefix, startAfter, cancellationToken).ToAsyncEnumerable();

    private IEnumerable<ObjectListing> ListFiles(string prefix, string? startAfter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int slash = prefix.LastIndexOf('/');
        string directoryKey = prefix[..(slash + 1)];
        string directory = DirectoryOf(directoryKey);
        if (!Fits(directory)) yield break;
        if (!Directory.Exists(directory)) ThrowIfRootMissing();
        foreach (ObjectListing listing in List(directoryKey, prefix[(slash + 1)..], startAfter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return listing;
        }
    }

    /// <summary>
    /// The files under the directory of <paramref name="directoryKey"/>, a key prefix that ends with a <c>/</c> or is
    /// empty, whose next segment starts with <paramref name="start"/> and whose keys come after
    /// <paramref name="startAfter"/>, in key order. A directory sorts as its segment and a <c>/</c>, as its files' keys
    /// do.
    /// </summary>
    private IEnumerable<ObjectListing> List(string directoryKey, string start, string? startAfter)
    {
        List<(string Key, FileSystemInfo Entry)> entries = [];
        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(DirectoryOf(directoryKey)).EnumerateFileSystemInfos())
            {
                if (Unescape(entry.Name) is not { } segment || !segment.StartsWith(start, StringComparison.Ordinal)) continue;
                string key = directoryKey + segment + (entry is DirectoryInfo ? "/" : "");
                // A directory whose keys all come before startAfter is skipped, and one that holds it is read.
                bool after = startAfter is null || CodePointOrder.Compare(key, startAfter) > 0
                    || (entry is DirectoryInfo && startAfter.StartsWith(key, StringComparison.Ordinal));
                if (after) entries.Add((key, entry));
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Deleted since its parent was read, or never there.
        }
        entries.Sort((x, y) => CodePointOrder.Compare(x.Key, y.Key));
        foreach ((string key, FileSystemInfo entry) in entries)
        {
            if (entry is FileInfo file)
            {
                // Gone since the directory was read.
                if (!TryDescribe(file, out long size, out DateTimeOffset lastModified)) continue;
                yield return new ObjectListing(key, size, lastModified);
            }
            else
            {
                string? within = startAfter is not null && startAfter.StartsWith(key, StringComparison.Ordinal) ? startAfter : null;
                foreach (ObjectListing listing in List(key, "", within)) yield return listing;
            }
        }
    }

    private static bool TryDescribe(FileInfo file, out long size, out DateTimeOffset lastModified)
    {
        try
        {
            size = file.Length;
            lastModified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            return true;
        }
        catch (FileNotFoundException)
        {
            (size, lastModified) = (0, default);
            return false;
        }
    }

    /// <summary>
    /// Deletes the files, each holding its key's lock, so a conditional write that already compared can't put it back,
    /// then each directory left empty, up to the root.
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        foreach (string key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = PathOf(key);
            if (!Fits(path)) continue;
            try
            {
                using (await LockAsync(key).ConfigureAwait(false)) await DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            for (string? directory = Path.GetDirectoryName(path); directory is not null && directory.Length > root.Length;
                 directory = Path.GetDirectoryName(directory))
            {
                try
                {
                    Directory.Delete(directory);
                }
                catch (IOException)
                {
                    // Not empty, or gone already.
                    break;
                }
            }
        }
    }

    public bool IsStoreFailure(Exception exception) => exception is IOException or UnauthorizedAccessException;

    /// <summary>The whole file, or <see langword="null"/> when there's none.</summary>
    private async Task<byte[]?> ReadAllAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, ReadersShare, 1, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                byte[] body = new byte[file.Length];
                await file.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
                return body;
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            ThrowIfRootMissing();
            return null;
        }
    }

    private static string ETagOf(byte[] body) => Convert.ToHexString(SHA256.HashData(body));

    /// <summary>
    /// Writes a file under <c>.tmp</c>, flushes it to disk, then moves it to <paramref name="path"/>, replacing what's
    /// there, holding the lock of <paramref name="lockKey"/> unless the caller holds it already. Another writer may
    /// delete the directory it goes in once it's empty, in which case it's made again.
    /// </summary>
    private async Task WriteAsync(string path, string? lockKey, Func<FileStream, Task> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SweepTempIfDue();
        string temp = Path.Combine(_temp, Guid.NewGuid().ToString("N"));
        try
        {
            FileStream file;
            try
            {
                file = NewTempFile(temp);
            }
            catch (DirectoryNotFoundException)
            {
                CreateDirectory(_temp);
                file = NewTempFile(temp);
            }
            await using (file.ConfigureAwait(false))
            {
                await write(file).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            if (lockKey is null)
            {
                await MoveAsync(temp, path).ConfigureAwait(false);
            }
            else
            {
                using (await LockAsync(lockKey).ConfigureAwait(false)) await MoveAsync(temp, path).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception exception) when (IsStoreFailure(exception))
            {
                // Left for the sweep of .tmp, so the write's own failure is what's reported.
            }
            throw;
        }
    }

    /// <summary>
    /// Deletes a file, waiting while Windows refuses because another process has it open, until the file is released or
    /// <see cref="MaxWait"/> runs out. A virus scanner, for one, may still have a file open that was just written.
    /// </summary>
    private static async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception exception) when (IsBusy(exception) && Stopwatch.GetElapsedTime(started) < MaxWait)
            {
                await Task.Delay(RetryWait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static FileStream NewTempFile(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous);

    /// <summary>
    /// Moves a file into place, making its directory when it's missing, and waiting while Windows refuses to replace
    /// a file that's being read. Making the directory is tried again too, since another writer may delete a directory
    /// it goes in as it's made.
    /// </summary>
    private async Task MoveAsync(string temp, string path)
    {
        long started = Stopwatch.GetTimestamp();
        bool missing = false;
        while (true)
        {
            try
            {
                if (missing) CreateDirectory(Path.GetDirectoryName(path)!);
                BeforeMove?.Invoke(path);
                File.Move(temp, path, overwrite: true);
                return;
            }
            // When the move fails because the directory is gone, .NET reports the file as not found instead if another
            // writer has made the directory again by the time it checks. Checking that the file being moved is still there
            // tells that apart from a missing source, which no retry can fix.
            catch (IOException exception) when ((exception is DirectoryNotFoundException
                || (exception is FileNotFoundException && File.Exists(temp))) && Directory.Exists(root)
                && Stopwatch.GetElapsedTime(started) < MaxWait)
            {
                missing = true;
            }
            catch (Exception exception) when (IsBusy(exception) && Stopwatch.GetElapsedTime(started) < MaxWait)
            {
                await Task.Delay(RetryWait).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether Windows refused a file because another process has it open; anything else won't pass by waiting.</summary>
    private static bool IsBusy(Exception exception) => OperatingSystem.IsWindows()
        && (exception is UnauthorizedAccessException || exception is IOException { HResult: SharingViolation or LockViolation });

    /// <summary>Makes a directory under the root, which must exist: one that's gone is a mistake to report, not to paper over.</summary>
    private void CreateDirectory(string directory)
    {
        ThrowIfRootMissing();
        Directory.CreateDirectory(directory);
    }

    private void ThrowIfRootMissing()
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The directory {root}, which holds Resourcefull's files, doesn't exist.");
    }

    /// <summary>
    /// Deletes the files under <c>.tmp</c> that writes left behind when they stopped, at most once every
    /// <see cref="TempSweepInterval"/>.
    /// </summary>
    private void SweepTempIfDue()
    {
        long now = DateTime.UtcNow.Ticks;
        long due = Interlocked.Read(ref _nextTempSweep);
        if (now < due || Interlocked.CompareExchange(ref _nextTempSweep, now + TempSweepInterval.Ticks, due) != due) return;
        try
        {
            foreach (FileInfo file in new DirectoryInfo(_temp).EnumerateFiles())
            {
                if (DateTime.UtcNow - file.LastWriteTimeUtc < StaleTempAge) continue;
                try
                {
                    file.Delete();
                }
                catch (Exception exception) when (IsStoreFailure(exception))
                {
                    // Still open, or gone already.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing written yet.
        }
    }

    /// <summary>
    /// Takes the lock for <paramref name="key"/>: first among this store's writers, then, with a lock file no other
    /// process can open while it's held, among every process that shares the directory.
    /// </summary>
    private async Task<IDisposable> LockAsync(string key)
    {
        int lockNumber = (int)(Fnv1a(key) % LockCount);
        SemaphoreSlim gate = _gates[lockNumber];
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string path = Path.Combine(_locks, lockNumber.ToString("x2"));
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                try
                {
                    return new HeldLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), gate);
                }
                catch (DirectoryNotFoundException) when (Stopwatch.GetElapsedTime(started) < MaxWait)
                {
                    CreateDirectory(_locks);
                }
                // Another process holds it: Windows answers with a sharing violation, and .NET elsewhere with an IOException
                // when the file's lock is taken.
                catch (IOException) when (Stopwatch.GetElapsedTime(started) < MaxWait)
                {
                    await Task.Delay(RetryWait).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private static uint Fnv1a(string key)
    {
        uint hash = 2166136261;
        foreach (char c in key) hash = (hash ^ c) * 16777619;
        return hash;
    }

    private sealed class HeldLock(FileStream file, SemaphoreSlim gate) : IDisposable
    {
        public void Dispose()
        {
            file.Dispose();
            gate.Release();
        }
    }

    private string PathOf(string key) => Path.Combine([root, .. key.Split('/').Select(Escape)]);

    /// <summary>
    /// Whether the operating system accepts <paramref name="path"/>. A longer path can't name a file that exists, so
    /// callers treat it as absent.
    /// </summary>
    private bool Fits(string path) => Encoding.UTF8.GetByteCount(path) <= MaxPathBytes;

    /// <summary>The path of a file to write, which must be one the operating system takes.</summary>
    /// <exception cref="ResourceNameException">The path is too long.</exception>
    private string WritablePathOf(string key)
    {
        string path = PathOf(key);
        if (!Fits(path))
            throw new ResourceNameException($"The name is too long for this store: its file's path would be " +
                $"{Encoding.UTF8.GetByteCount(path)} bytes, more than the {MaxPathBytes} this operating system allows. Use a " +
                "shorter name, or a directory with a shorter path.", null);
        return path;
    }

    /// <summary>The directory of a key prefix that ends with a <c>/</c>, or of the root when it's empty.</summary>
    private string DirectoryOf(string directoryKey) =>
        directoryKey.Length == 0 ? root : Path.Combine([root, .. directoryKey[..^1].Split('/').Select(Escape)]);

    /// <summary>What every filesystem takes in a file name, case and all, besides lowercase letters and digits.</summary>
    private const string Kept = "!#$%&'()+,-;=@[]_`{}";

    /// <summary>A key's segment as a file name: see the remarks on <see cref="FileSystemObjectStore"/>.</summary>
    internal static string Escape(string segment)
    {
        var escaped = new StringBuilder(segment.Length * 2);
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' || Kept.Contains(c)) escaped.Append(c);
            else if (c is >= 'A' and <= 'Z') escaped.Append('^').Append(char.ToLowerInvariant(c));
            else if (c < 0x80) escaped.Append('~').Append(((int)c).ToString("x2"));
            else if (char.IsSurrogatePair(segment, i)) escaped.Append("~v").Append(char.ConvertToUtf32(c, segment[++i]).ToString("x6"));
            else escaped.Append("~u").Append(((int)c).ToString("x4"));
        }
        return escaped.ToString();
    }

    /// <summary>The key segment a file name stands for, or <see langword="null"/> for a name no key escapes to.</summary>
    internal static string? Unescape(string name)
    {
        var segment = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' || Kept.Contains(c))
            {
                segment.Append(c);
            }
            else if (c == '^' && i + 1 < name.Length && name[i + 1] is >= 'a' and <= 'z')
            {
                segment.Append(char.ToUpperInvariant(name[++i]));
            }
            else if (c == '~' && i + 1 < name.Length)
            {
                (int digits, int skip) = name[i + 1] switch { 'u' => (4, 2), 'v' => (6, 2), _ => (2, 1) };
                if (i + skip + digits > name.Length
                    || !int.TryParse(name.AsSpan(i + skip, digits), System.Globalization.NumberStyles.AllowHexSpecifier, null, out int code)
                    || code > 0x10FFFF)
                    return null;
                if (code > 0xFFFF) segment.Append(char.ConvertFromUtf32(code));
                else segment.Append((char)code);
                i += skip + digits - 1;
            }
            else
            {
                return null;
            }
        }
        return segment.Length == 0 ? null : segment.ToString();
    }

    /// <summary>Unicode code point order, which is the order of UTF-8 bytes, and so the order object stores list keys in.</summary>
    private static class CodePointOrder
    {
        public static int Compare(string x, string y)
        {
            int length = Math.Min(x.Length, y.Length);
            for (int i = 0; i < length; i++)
            {
                if (x[i] != y[i]) return Rank(x[i]) - Rank(y[i]);
            }
            return x.Length - y.Length;
        }

        /// <summary>
        /// A UTF-16 unit's place in code point order: surrogates, which stand for characters beyond U+FFFF, after the rest.
        /// </summary>
        private static int Rank(char c) => c switch { >= '\uE000' => c - 0x800, >= '\uD800' => c + 0x2000, _ => c };
    }

    /// <summary>A file read from where it was opened at, for <paramref name="length"/> bytes.</summary>
    private sealed class RangeStream(FileStream file, long length) : Stream
    {
        private readonly long _length = length;
        private long _left = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _length - _left;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read = file.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            _left -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await file.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancellationToken).ConfigureAwait(false);
            _left -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }
    }
}
