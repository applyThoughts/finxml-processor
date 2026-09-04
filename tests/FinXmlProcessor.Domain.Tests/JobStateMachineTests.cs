using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Jobs;

namespace FinXmlProcessor.Domain.Tests;

public class JobStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 23, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(JobStatus.Discovered, JobStatus.Ready)]
    [InlineData(JobStatus.Ready, JobStatus.Validating)]
    [InlineData(JobStatus.Validating, JobStatus.Processing)]
    [InlineData(JobStatus.Processing, JobStatus.GeneratingOutput)]
    [InlineData(JobStatus.GeneratingOutput, JobStatus.Completed)]
    [InlineData(JobStatus.GeneratingOutput, JobStatus.CompletedWithWarnings)]
    [InlineData(JobStatus.Completed, JobStatus.Delivering)]
    [InlineData(JobStatus.CompletedWithWarnings, JobStatus.Delivering)]
    [InlineData(JobStatus.Delivering, JobStatus.Delivered)]
    [InlineData(JobStatus.Processing, JobStatus.Failed)]
    [InlineData(JobStatus.Validating, JobStatus.Quarantined)]
    [InlineData(JobStatus.Delivering, JobStatus.Cancelled)]
    [InlineData(JobStatus.Discovered, JobStatus.Failed)]
    public void Allowed_transitions(JobStatus from, JobStatus to)
    {
        JobStateMachine.CanTransition(from, to).Should().BeTrue();
    }

    [Theory]
    [InlineData(JobStatus.Discovered, JobStatus.Processing)]
    [InlineData(JobStatus.Ready, JobStatus.Completed)]
    [InlineData(JobStatus.Processing, JobStatus.Completed)]
    [InlineData(JobStatus.Completed, JobStatus.Delivered)]
    [InlineData(JobStatus.Failed, JobStatus.Ready)]
    [InlineData(JobStatus.Failed, JobStatus.Cancelled)]
    [InlineData(JobStatus.Cancelled, JobStatus.Failed)]
    [InlineData(JobStatus.Delivered, JobStatus.Failed)]
    [InlineData(JobStatus.Quarantined, JobStatus.Ready)]
    [InlineData(JobStatus.Processing, JobStatus.Processing)]
    [InlineData(JobStatus.Delivered, JobStatus.Delivering)]
    public void Forbidden_transitions(JobStatus from, JobStatus to)
    {
        JobStateMachine.CanTransition(from, to).Should().BeFalse();
        FluentActions.Invoking(() => JobStateMachine.EnsureCanTransition(from, to))
            .Should().Throw<InvalidJobTransitionException>()
            .Which.Should().Match<InvalidJobTransitionException>(e => e.From == from && e.To == to);
    }

    [Fact]
    public void Job_records_each_transition_with_time_and_reason()
    {
        var job = NewJob();
        job.TransitionTo(JobStatus.Ready, T0);
        job.TransitionTo(JobStatus.Validating, T0.AddSeconds(1));
        job.TransitionTo(JobStatus.Failed, T0.AddSeconds(2), "FILE-001: missing");

        job.Status.Should().Be(JobStatus.Failed);
        job.Transitions.Should().HaveCount(3);
        job.Transitions[2].Should().Be(new JobStateTransition(JobStatus.Validating, JobStatus.Failed, T0.AddSeconds(2), "FILE-001: missing"));
        job.StartedAt.Should().Be(T0.AddSeconds(1));
        job.FinishedAt.Should().Be(T0.AddSeconds(2));
    }

    [Fact]
    public void Job_rejects_invalid_transition_without_changing_state()
    {
        var job = NewJob();
        FluentActions.Invoking(() => job.TransitionTo(JobStatus.Processing, T0)).Should().Throw<InvalidJobTransitionException>();
        job.Status.Should().Be(JobStatus.Discovered);
        job.Transitions.Should().BeEmpty();
    }

    [Fact]
    public void Terminal_and_active_classification()
    {
        JobStatus.Delivered.IsTerminal().Should().BeTrue();
        JobStatus.Completed.IsTerminal().Should().BeFalse();
        JobStatus.Completed.IsActive().Should().BeTrue();
        JobStatus.Failed.IsActive().Should().BeFalse();
        JobStatus.CompletedWithWarnings.IsSuccessful().Should().BeTrue();
        JobStatus.Quarantined.IsSuccessful().Should().BeFalse();
    }

    [Fact]
    public void Rehydrate_restores_state_without_validation()
    {
        var job = ProcessingJob.Rehydrate(Guid.NewGuid(), "a.xml", "abc", 10, "p", "1.0.0", "hash", T0, null, "cli", JobStatus.Delivered,
            new ProcessingCounts(1, 1, 0, 0, 1, 0), "out.xlsx", "sha", "report.json", new DateOnly(2026, 9, 3),
            [new JobStateTransition(JobStatus.Discovered, JobStatus.Ready, T0, null)],
            [RecordIssue.Warning("X-1", null, "w")]);
        job.Status.Should().Be(JobStatus.Delivered);
        job.Transitions.Should().ContainSingle();
        job.Issues.Should().ContainSingle();
        job.BusinessDate.Should().Be(new DateOnly(2026, 9, 3));
    }

    private static ProcessingJob NewJob() => new(Guid.NewGuid(), "input.xml", null, "demo", "1.0.0", "hash", T0);
}
