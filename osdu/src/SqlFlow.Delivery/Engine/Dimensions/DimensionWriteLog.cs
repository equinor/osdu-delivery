using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// A dimension build's write as its trace tells it (osdu/docs/reference/flow/dimension-build.md, The write): each step's
/// start and end with what it did and how long it took, a wait for another build's write lock, and, while a step runs long,
/// what its statement is doing. A write that works, one that waits and one that is stuck read apart from the trace alone.
/// </summary>
internal sealed class DimensionWriteLog(ILogger log, string dimension) : IDimensionWriteListener
{
    public void Started(DimensionWriteStep writeStep, string what)
        => log.LogInformation("dimension {Dimension}: {Step}: {What}", dimension, DimensionWriteClock.Named(writeStep), what);

    public void Waiting(DimensionWriteStep writeStep)
        => log.LogInformation(
            "dimension {Dimension}: {Step} waits for the dimension's write lock, which another build of it holds, for up to two minutes",
            dimension, DimensionWriteClock.Named(writeStep));

    public void Locked(DimensionWriteStep writeStep, TimeSpan waited)
        => log.LogInformation(
            "dimension {Dimension}: took the write lock for {Step} after waiting {Waited}", dimension, DimensionWriteClock.Named(writeStep), DimensionWriteClock.Seconds(waited));

    public void Running(DimensionWriteStep writeStep, TimeSpan elapsed, string? state)
        => log.LogInformation(
            "dimension {Dimension}: still {Step} after {Elapsed}{State}", dimension, DimensionWriteClock.Named(writeStep), DimensionWriteClock.Seconds(elapsed),
            state is null ? string.Empty : "; " + state);

    public void Ended(DimensionWriteStep writeStep, TimeSpan took, string what)
        => log.LogInformation(
            "dimension {Dimension}: {Step} took {Took}: {What}", dimension, DimensionWriteClock.Named(writeStep), DimensionWriteClock.Seconds(took), what);
}
