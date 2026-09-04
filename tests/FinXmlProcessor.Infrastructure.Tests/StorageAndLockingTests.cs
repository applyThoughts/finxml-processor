using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Infrastructure.Locking;
using FinXmlProcessor.Infrastructure.Paths;
using FinXmlProcessor.Infrastructure.Persistence;
using FinXmlProcessor.Infrastructure.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinXmlProcessor.Infrastructure.Tests;

public class StorageAndLockingTests : IDisposable
{
    private readonly TempRoot _root = new();

    [Fact]
    public async Task Duplicate_set_detects_repeats_and_cleans_up_its_file()
    {
        var factory = new SqliteRecordDuplicateSetFactory(_root.Paths, NullLogger<SqliteRecordDuplicateSetFactory>.Instance);
        Guid jobId = Guid.NewGuid();
        string file = Path.Combine(_root.Paths.Staging, $"dupkeys-{jobId:N}.sqlite");
        await using (IRecordDuplicateSet set = await factory.CreateAsync(jobId, CancellationToken.None))
        {
            File.Exists(file).Should().BeTrue();
            for (int i = 0; i < 5000; i++)
            {
                (await set.IsDuplicateAsync($"key-{i}", CancellationToken.None)).Should().BeFalse();
            }

            (await set.IsDuplicateAsync("key-42", CancellationToken.None)).Should().BeTrue();
            (await set.IsDuplicateAsync("key-4999", CancellationToken.None)).Should().BeTrue();
            (await set.IsDuplicateAsync("KEY-42", CancellationToken.None)).Should().BeFalse("keys are case-sensitive");
        }

        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task Lock_is_exclusive_across_instances_and_released_on_dispose()
    {
        var first = new FileProcessingLock(_root.Paths, NullLogger<FileProcessingLock>.Instance);
        var second = new FileProcessingLock(_root.Paths, NullLogger<FileProcessingLock>.Instance);

        IAsyncDisposable? lease = await first.TryAcquireAsync("test job", CancellationToken.None);
        lease.Should().NotBeNull();
        (await second.TryAcquireAsync("other", CancellationToken.None)).Should().BeNull();
        (await first.TryAcquireAsync("same process again", CancellationToken.None)).Should().BeNull();
        string? holder = await second.DescribeHolderAsync(CancellationToken.None);
        holder.Should().Contain("test job").And.Contain($"pid {Environment.ProcessId}");

        await lease!.DisposeAsync();
        IAsyncDisposable? again = await second.TryAcquireAsync("other", CancellationToken.None);
        again.Should().NotBeNull();
        await again!.DisposeAsync();
        (await first.DescribeHolderAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void ResolveInside_blocks_traversal()
    {
        AppPaths paths = _root.Paths;
        paths.ResolveInside(paths.Quarantine, "file.xml").Should().StartWith(paths.Quarantine);
        paths.ResolveInside(paths.Quarantine, Path.Combine(paths.Quarantine, "sub", "file.xml")).Should().StartWith(paths.Quarantine);
        FluentActions.Invoking(() => paths.ResolveInside(paths.Quarantine, "../database/history.sqlite")).Should().Throw<UnauthorizedAccessException>();
        FluentActions.Invoking(() => paths.ResolveInside(paths.Quarantine, paths.Root)).Should().Throw<UnauthorizedAccessException>();
        FluentActions.Invoking(() => paths.ResolveInside(paths.Quarantine, Path.GetTempPath())).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Default_root_follows_platform_conventions_and_env_override()
    {
        string root = AppPaths.ResolveDefaultRoot();
        if (OperatingSystem.IsMacOS())
        {
            root.Should().EndWith(Path.Combine("Library", "Application Support", "FinXmlProcessor"));
        }
        else if (OperatingSystem.IsWindows())
        {
            root.Should().Be(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FinXmlProcessor"));
        }

        string previous = Environment.GetEnvironmentVariable(AppPaths.HomeEnvironmentVariable) ?? string.Empty;
        try
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeEnvironmentVariable, _root.Paths.Root);
            AppPaths.ResolveDefaultRoot().Should().Be(_root.Paths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.HomeEnvironmentVariable, previous.Length == 0 ? null : previous);
        }
    }

    [Fact]
    public async Task In_memory_secret_store_round_trips()
    {
        var store = new InMemorySecretStore();
        (await store.RetrieveAsync("s", "a", CancellationToken.None)).Should().BeNull();
        await store.StoreAsync("s", "a", "p@ss", CancellationToken.None);
        (await store.RetrieveAsync("s", "a", CancellationToken.None)).Should().Be("p@ss");
        (await store.DeleteAsync("s", "a", CancellationToken.None)).Should().BeTrue();
        (await store.DeleteAsync("s", "a", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Dpapi_secret_store_never_writes_plaintext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DpapiSecretStore(_root.Paths);
        await store.StoreAsync(SecretNames.Service, SecretNames.SftpPassword, "hunter2-plaintext", CancellationToken.None);
        string file = Path.Combine(_root.Paths.Settings, "secrets.dpapi.json");
        File.Exists(file).Should().BeTrue();
        (await File.ReadAllTextAsync(file)).Should().NotContain("hunter2");
        (await store.RetrieveAsync(SecretNames.Service, SecretNames.SftpPassword, CancellationToken.None)).Should().Be("hunter2-plaintext");
        (await store.DeleteAsync(SecretNames.Service, SecretNames.SftpPassword, CancellationToken.None)).Should().BeTrue();
        (await store.RetrieveAsync(SecretNames.Service, SecretNames.SftpPassword, CancellationToken.None)).Should().BeNull();
    }

    public void Dispose() => _root.Dispose();
}
