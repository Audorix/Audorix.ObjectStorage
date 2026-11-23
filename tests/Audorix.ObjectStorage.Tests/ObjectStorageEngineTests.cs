using System.Text;
using Microsoft.Extensions.Options;

namespace Audorix.ObjectStorage.Tests;

public sealed class ObjectStorageEngineTests
{
    [Fact]
    public async Task PutAndReadRoundTripPreservesBytesAndMetadata()
    {
        using TestStorage storage = await TestStorage.CreateAsync();
        await storage.Engine.CreateBucketAsync("docs");

        byte[] bytes = "hello world"u8.ToArray();
        ObjectInfo written = await storage.Engine.PutObjectAsync(new PutObjectRequest(
            "docs",
            "a/b/hello.txt",
            new MemoryStream(bytes),
            "text/plain",
            new Dictionary<string, string> { ["owner"] = "audorix" }));

        await using ObjectReadResult read = await storage.Engine.OpenReadAsync("docs", "a/b/hello.txt");
        using MemoryStream copy = new();
        await read.Content.CopyToAsync(copy);

        Assert.Equal(bytes, copy.ToArray());
        Assert.Equal("text/plain", read.Object.ContentType);
        Assert.Equal("audorix", read.Object.Metadata["owner"]);
        Assert.Equal(bytes.Length, written.Size);
    }

    [Fact]
    public async Task PersistsAfterEngineDisposalAndReopen()
    {
        string root = CreateTempRoot();
        await using (FileSystemObjectStorageEngine engine = CreateEngine(root))
        {
            await engine.CreateBucketAsync("docs");
            await engine.PutObjectAsync(new PutObjectRequest("docs", "keep", new MemoryStream("value"u8.ToArray())));
        }

        await using FileSystemObjectStorageEngine reopened = CreateEngine(root);
        await using ObjectReadResult read = await reopened.OpenReadAsync("docs", "keep");
        using StreamReader reader = new(read.Content, Encoding.UTF8);
        Assert.Equal("value", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SupportsEmptyAndSpecialCharacterKeys()
    {
        using TestStorage storage = await TestStorage.CreateAsync();
        await storage.Engine.CreateBucketAsync("docs");

        string key = "../unicodé/space key/#?.txt";
        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", key, Stream.Null));

        ObjectInfo? metadata = await storage.Engine.GetObjectAsync("docs", key);
        Assert.NotNull(metadata);
        Assert.Equal(key, metadata.Key);
        Assert.Equal(0, metadata.Size);
        Assert.All(Directory.EnumerateFiles(Path.Combine(storage.Root, "blobs")), path =>
            Assert.Equal(Path.Combine(storage.Root, "blobs"), Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task RejectsUnboundedOrHeaderUnsafeObjectPropertiesBeforeWriting()
    {
        using TestStorage storage = await TestStorage.CreateAsync();
        await storage.Engine.CreateBucketAsync("docs");

        await Assert.ThrowsAsync<ArgumentException>(() => storage.Engine.PutObjectAsync(new PutObjectRequest(
            "docs", new string('k', 1025), Stream.Null)));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.Engine.PutObjectAsync(new PutObjectRequest(
            "docs", "metadata", Stream.Null, "text/plain\r\nX-Injected: yes")));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.Engine.PutObjectAsync(new PutObjectRequest(
            "docs", "metadata", Stream.Null, Metadata: new Dictionary<string, string> { ["name"] = "value\r\nX-Injected: yes" })));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(storage.Root, "staging"), "*.staging"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(storage.Root, "blobs"), "*.blob"));
    }

    [Fact]
    public async Task ListsByPrefixWithPaginationWithoutDuplicates()
    {
        using TestStorage storage = await TestStorage.CreateAsync();
        await storage.Engine.CreateBucketAsync("docs");
        foreach (string key in new[] { "a/1", "a/2", "a/3", "b/1" })
        {
            await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", key, new MemoryStream([1])));
        }

        ObjectListPage first = await storage.Engine.ListObjectsAsync("docs", "a/", pageSize: 2);
        ObjectListPage second = await storage.Engine.ListObjectsAsync("docs", "a/", pageSize: 2, first.ContinuationToken);

        Assert.Equal(["a/1", "a/2"], first.Objects.Select(o => o.Key));
        Assert.Equal(["a/3"], second.Objects.Select(o => o.Key));
        Assert.Null(second.ContinuationToken);
    }

    [Fact]
    public async Task OverwriteWhilePreviousReadIsActivePreservesReaderSnapshot()
    {
        using TestStorage storage = await TestStorage.CreateAsync(retention: TimeSpan.Zero);
        await storage.Engine.CreateBucketAsync("docs");
        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "file", new MemoryStream("old"u8.ToArray())));

        await using ObjectReadResult oldRead = await storage.Engine.OpenReadAsync("docs", "file");
        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "file", new MemoryStream("new"u8.ToArray())));
        await storage.Engine.CleanupUnreferencedBlobsAsync();

        using StreamReader oldReader = new(oldRead.Content, Encoding.UTF8);
        Assert.Equal("old", await oldReader.ReadToEndAsync());

        await using ObjectReadResult newRead = await storage.Engine.OpenReadAsync("docs", "file");
        using StreamReader newReader = new(newRead.Content, Encoding.UTF8);
        Assert.Equal("new", await newReader.ReadToEndAsync());
    }

    [Fact]
    public async Task RangeReadsResolveExplicitOpenEndedSuffixAndBoundaries()
    {
        using TestStorage storage = await TestStorage.CreateAsync();
        await storage.Engine.CreateBucketAsync("docs");
        byte[] content = Enumerable.Range(0, 256).Select(x => (byte)x).ToArray();
        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "range", new MemoryStream(content), "application/octet-stream"));

        await AssertRangeAsync(storage.Engine, ObjectByteRange.FromStartAndEnd(10, 19), content[10..20], 10, 19, 256);
        await AssertRangeAsync(storage.Engine, ObjectByteRange.FromStart(250), content[250..], 250, 255, 256);
        await AssertRangeAsync(storage.Engine, ObjectByteRange.FromSuffix(7), content[^7..], 249, 255, 256);
        await AssertRangeAsync(storage.Engine, ObjectByteRange.FromStartAndEnd(0, 0), content[..1], 0, 0, 256);
        await AssertRangeAsync(storage.Engine, ObjectByteRange.FromStartAndEnd(255, 999), content[^1..], 255, 255, 256);
    }

    [Fact]
    public async Task RangeReadsRejectEmptyAndUnsatisfiableSelections()
    {
        using TestStorage storage = await TestStorage.CreateAsync();
        await storage.Engine.CreateBucketAsync("docs");
        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "empty", Stream.Null));
        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "short", new MemoryStream([1, 2, 3])));

        ObjectRangeNotSatisfiableException empty = await Assert.ThrowsAsync<ObjectRangeNotSatisfiableException>(
            () => storage.Engine.OpenReadRangeAsync("docs", "empty", ObjectByteRange.FromSuffix(1)));
        ObjectRangeNotSatisfiableException beyond = await Assert.ThrowsAsync<ObjectRangeNotSatisfiableException>(
            () => storage.Engine.OpenReadRangeAsync("docs", "short", ObjectByteRange.FromStart(3)));
        Assert.Equal(0, empty.FullLength);
        Assert.Equal(3, beyond.FullLength);
    }

    [Fact]
    public async Task ActiveRangeReaderKeepsItsCommittedBlobAndNeverReadsPastTheRange()
    {
        using TestStorage storage = await TestStorage.CreateAsync(retention: TimeSpan.Zero);
        await storage.Engine.CreateBucketAsync("docs");
        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "range", new MemoryStream("0123456789"u8.ToArray())));
        await using ObjectRangeReadResult oldRange = await storage.Engine.OpenReadRangeAsync("docs", "range", ObjectByteRange.FromStartAndEnd(2, 5));

        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "range", new MemoryStream("abcdefghij"u8.ToArray())));
        await storage.Engine.DeleteObjectAsync("docs", "range");
        await storage.Engine.CleanupUnreferencedBlobsAsync();

        using var copy = new MemoryStream();
        await oldRange.Content.CopyToAsync(copy);
        Assert.Equal("2345", Encoding.UTF8.GetString(copy.ToArray()));
        Assert.Equal(0, await oldRange.Content.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task RangeReadHonorsCancellationAndDisposalReleasesBlob()
    {
        using TestStorage storage = await TestStorage.CreateAsync(retention: TimeSpan.Zero);
        await storage.Engine.CreateBucketAsync("docs");
        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "range", new MemoryStream(new byte[1024])));
        ObjectRangeReadResult result = await storage.Engine.OpenReadRangeAsync("docs", "range", ObjectByteRange.FromStartAndEnd(0, 511));
        await storage.Engine.DeleteObjectAsync("docs", "range");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await result.Content.ReadExactlyAsync(new byte[16], cancelled.Token));
        await result.DisposeAsync();
        Assert.Equal(1, await storage.Engine.CleanupUnreferencedBlobsAsync());
    }

    [Fact]
    public async Task FailedOverwritePreservesPreviouslyCommittedObject()
    {
        using TestStorage storage = await TestStorage.CreateAsync();
        await storage.Engine.CreateBucketAsync("docs");
        await storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "file", new MemoryStream("old"u8.ToArray())));

        await Assert.ThrowsAsync<IOException>(() => storage.Engine.PutObjectAsync(new PutObjectRequest(
            "docs",
            "file",
            new ThrowingReadStream("broken"u8.ToArray(), 2))));

        await using ObjectReadResult read = await storage.Engine.OpenReadAsync("docs", "file");
        using StreamReader reader = new(read.Content, Encoding.UTF8);
        Assert.Equal("old", await reader.ReadToEndAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(storage.Root, "staging"), "*.staging"));
    }

    [Fact]
    public async Task ConcurrentMutationsToSameKeySerializeToACommittedObject()
    {
        using TestStorage storage = await TestStorage.CreateAsync();
        await storage.Engine.CreateBucketAsync("docs");
        Task[] writes = Enumerable.Range(0, 20)
            .Select(i => storage.Engine.PutObjectAsync(new PutObjectRequest("docs", "same", new MemoryStream(Encoding.UTF8.GetBytes(i.ToString())))))
            .ToArray();

        await Task.WhenAll(writes);

        await using ObjectReadResult read = await storage.Engine.OpenReadAsync("docs", "same");
        using StreamReader reader = new(read.Content, Encoding.UTF8);
        string value = await reader.ReadToEndAsync();
        Assert.Contains(value, Enumerable.Range(0, 20).Select(i => i.ToString()));
    }

    [Fact]
    public async Task RejectsSecondEngineForSameStorageRoot()
    {
        string root = CreateTempRoot();
        await using FileSystemObjectStorageEngine first = CreateEngine(root);
        await first.GetBucketAsync("missing");

        await using FileSystemObjectStorageEngine second = CreateEngine(root);
        await Assert.ThrowsAsync<ObjectStorageException>(() => second.GetBucketAsync("missing"));
    }

    private static FileSystemObjectStorageEngine CreateEngine(string root, TimeSpan? retention = null) =>
        new(new JsonFileMetadataStore(Path.Combine(root, "metadata.json")), Options.Create(new ObjectStorageOptions
        {
            StorageRootPath = root,
            UnreferencedBlobRetention = retention ?? TimeSpan.FromMinutes(10)
        }));

    private static async Task AssertRangeAsync(
        IObjectStorageEngine engine,
        ObjectByteRange request,
        byte[] expected,
        long start,
        long end,
        long fullLength)
    {
        await using ObjectRangeReadResult result = await engine.OpenReadRangeAsync("docs", "range", request);
        using var copy = new MemoryStream();
        await result.Content.CopyToAsync(copy);
        Assert.Equal(expected, copy.ToArray());
        Assert.Equal(new ResolvedObjectByteRange(fullLength, start, end, expected.Length), result.Range);
        Assert.Equal(expected.Length, result.Content.Length);
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "Audorix.ObjectStorage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TestStorage : IDisposable
    {
        private TestStorage(string root, FileSystemObjectStorageEngine engine)
        {
            Root = root;
            Engine = engine;
        }

        public string Root { get; }

        public FileSystemObjectStorageEngine Engine { get; }

        public static async Task<TestStorage> CreateAsync(TimeSpan? retention = null)
        {
            string root = CreateTempRoot();
            FileSystemObjectStorageEngine engine = CreateEngine(root, retention);
            await engine.GetBucketAsync("warmup");
            return new TestStorage(root, engine);
        }

        public void Dispose()
        {
            Engine.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
