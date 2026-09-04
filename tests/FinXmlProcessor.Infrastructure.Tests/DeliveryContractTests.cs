using System.Security.Cryptography;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Domain.Jobs;
using FinXmlProcessor.Infrastructure.Delivery;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinXmlProcessor.Infrastructure.Tests;

/// <summary>
/// Contract every <see cref="IOutputDelivery"/> implementation must satisfy. A future internal-system adapter
/// inherits this class and supplies its own factory; the local-folder provider is the reference implementation.
/// </summary>
public abstract class DeliveryContractTests : IDisposable
{
    protected TempRoot Root { get; } = new();

    protected abstract IOutputDelivery CreateConfigured();

    protected abstract IOutputDelivery CreateUnconfigured();

    /// <summary>Verifies the artifact reached its destination and returns true when it is byte-identical.</summary>
    protected abstract Task<bool> DestinationHoldsAsync(DeliveryResult result, byte[] originalContent);

    [Fact]
    public async Task Unconfigured_provider_reports_not_configured()
    {
        CreateUnconfigured().IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Configured_provider_delivers_and_reports_hash()
    {
        (ProcessingJob job, string artifact, byte[] content) = CreateArtifact();
        IOutputDelivery delivery = CreateConfigured();
        delivery.IsConfigured.Should().BeTrue();
        DeliveryResult result = await delivery.DeliverAsync(job, artifact, CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.SanitizedError);
        result.DeliveredSha256.Should().Be(job.OutputSha256);
        result.DeliveredPath.Should().NotBeNullOrEmpty();
        (await DestinationHoldsAsync(result, content)).Should().BeTrue();
    }

    [Fact]
    public async Task Repeated_delivery_never_silently_overwrites()
    {
        (ProcessingJob job, string artifact, _) = CreateArtifact();
        IOutputDelivery delivery = CreateConfigured();
        DeliveryResult first = await delivery.DeliverAsync(job, artifact, CancellationToken.None);
        DeliveryResult second = await delivery.DeliverAsync(job, artifact, CancellationToken.None);
        first.Succeeded.Should().BeTrue();
        if (second.Succeeded)
        {
            second.DeliveredPath.Should().NotBe(first.DeliveredPath, "collisions must version, not overwrite");
        }
    }

    protected (ProcessingJob Job, string ArtifactPath, byte[] Content) CreateArtifact()
    {
        byte[] content = new byte[4096];
        Random.Shared.NextBytes(content);
        string path = Path.Combine(Root.Paths.DefaultOutput, $"artifact_{Guid.NewGuid():N}.xlsx");
        File.WriteAllBytes(path, content);
        var job = new ProcessingJob(Guid.NewGuid(), "in.xml", "sha", "demo", "1.0.0", "h", DateTimeOffset.UtcNow)
        {
            OutputPath = path,
            OutputSha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
        };
        return (job, path, content);
    }

    public void Dispose() => Root.Dispose();
}

public sealed class LocalFolderDeliveryTests : DeliveryContractTests
{
    private string TargetFolder => Path.Combine(Root.Paths.Root, "delivered");

    protected override IOutputDelivery CreateConfigured() =>
        new LocalFolderDelivery(Options.Monitor(new DeliveryOptions { Provider = "local-folder", LocalFolder = TargetFolder }), NullLogger<LocalFolderDelivery>.Instance);

    protected override IOutputDelivery CreateUnconfigured() =>
        new LocalFolderDelivery(Options.Monitor(new DeliveryOptions { Provider = "none" }), NullLogger<LocalFolderDelivery>.Instance);

    protected override Task<bool> DestinationHoldsAsync(DeliveryResult result, byte[] originalContent) =>
        Task.FromResult(File.Exists(result.DeliveredPath) && File.ReadAllBytes(result.DeliveredPath!).AsSpan().SequenceEqual(originalContent));

    [Fact]
    public async Task Collision_policies()
    {
        (ProcessingJob job, string artifact, _) = CreateArtifact();
        var fail = new LocalFolderDelivery(Options.Monitor(new DeliveryOptions { Provider = "local-folder", LocalFolder = TargetFolder, CollisionPolicy = "fail" }), NullLogger<LocalFolderDelivery>.Instance);
        (await fail.DeliverAsync(job, artifact, CancellationToken.None)).Succeeded.Should().BeTrue();
        DeliveryResult second = await fail.DeliverAsync(job, artifact, CancellationToken.None);
        second.Succeeded.Should().BeFalse();
        second.SanitizedError.Should().Contain("already exists");

        var version = CreateConfigured();
        DeliveryResult third = await version.DeliverAsync(job, artifact, CancellationToken.None);
        third.Succeeded.Should().BeTrue();
        Path.GetFileName(third.DeliveredPath!).Should().EndWith(" (2).xlsx");
        Directory.EnumerateFiles(TargetFolder, "*.tmp").Should().BeEmpty("temp files are renamed or removed");
    }

    [Fact]
    public async Task Hash_mismatch_is_detected_and_copy_removed()
    {
        (ProcessingJob job, string artifact, _) = CreateArtifact();
        job.OutputSha256 = new string('0', 64);
        DeliveryResult result = await CreateConfigured().DeliverAsync(job, artifact, CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        result.SanitizedError.Should().Contain("hash");
        Directory.Exists(TargetFolder).Should().BeTrue();
        Directory.EnumerateFiles(TargetFolder).Should().BeEmpty();
    }
}

/// <summary>Demonstrates the internal-system adapter template with a fake transport, and that it passes the contract.</summary>
public sealed class InternalSystemTemplateContractTests : DeliveryContractTests
{
    private sealed class FakeInternalSystemDelivery : InternalSystemDeliveryBase
    {
        private readonly string _inbox;
        private readonly bool _configured;

        public FakeInternalSystemDelivery(string inbox, bool configured)
        {
            _inbox = inbox;
            _configured = configured;
        }

        public override string ProviderId => "fake-internal";

        public override bool IsConfigured => _configured;

        protected override async Task<string> TransmitAsync(ProcessingJob job, string artifactPath, string sha256, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_inbox);
            string reference = $"{sha256[..12]}-{Guid.NewGuid():N}";
            await using FileStream source = File.OpenRead(artifactPath);
            await using FileStream target = File.Create(Path.Combine(_inbox, reference));
            await source.CopyToAsync(target, cancellationToken);
            return reference;
        }
    }

    private string Inbox => Path.Combine(Root.Paths.Root, "internal-inbox");

    protected override IOutputDelivery CreateConfigured() => new FakeInternalSystemDelivery(Inbox, true);

    protected override IOutputDelivery CreateUnconfigured() => new FakeInternalSystemDelivery(Inbox, false);

    protected override Task<bool> DestinationHoldsAsync(DeliveryResult result, byte[] originalContent) =>
        Task.FromResult(File.ReadAllBytes(Path.Combine(Inbox, result.DeliveredPath!)).AsSpan().SequenceEqual(originalContent));

    [Fact]
    public async Task Template_sanitizes_transport_failures()
    {
        (ProcessingJob job, string artifact, _) = CreateArtifact();
        var broken = new ThrowingDelivery();
        DeliveryResult result = await broken.DeliverAsync(job, artifact, CancellationToken.None);
        result.Succeeded.Should().BeFalse();
        result.SanitizedError.Should().Be("Delivery provider 'throwing' failed with InvalidOperationException.");
        result.SanitizedError.Should().NotContain("secret");
    }

    private sealed class ThrowingDelivery : InternalSystemDeliveryBase
    {
        public override string ProviderId => "throwing";

        public override bool IsConfigured => true;

        protected override Task<string> TransmitAsync(ProcessingJob job, string artifactPath, string sha256, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("connection string secret=abc");
    }
}
