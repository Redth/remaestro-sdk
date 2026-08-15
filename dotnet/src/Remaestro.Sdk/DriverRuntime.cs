using Remaestro.Grpc;

namespace Remaestro.Sdk;

/// <summary>
/// The driver process's account of its own runtime, taken for the heartbeat frame on <c>StreamEvents</c>.
/// <para>
/// <b>Only what the process alone can know.</b> The hub reads RSS, anonymous and file-backed resident pages,
/// virtual size, swap, peak RSS and the OS thread count out of one <c>/proc/&lt;pid&gt;/status</c> per process
/// per tick — for any pid, no cooperation needed. None of that is here. What is here is the managed heap, the
/// allocation counter, the collection counts and the thread pool, none of which any outside observer can
/// take, and the last of which is the one that actually catches a driver wedged on a blocking socket.
/// </para>
/// <para>
/// <b>The zero trap, which is the reason this class exists rather than a few inline reads.</b> Every figure
/// <see cref="GCMemoryInfo"/> carries is <i>literally zero</i> until the first collection has run — not just
/// <c>TotalCommittedBytes</c>, which is the one <c>#170</c> recorded, but <c>HeapSizeBytes</c> and
/// <c>FragmentedBytes</c> with it. Measured on .NET 10 under both CoreCLR and Native AOT, which behave
/// identically here. <c>GCMemoryInfo.Index</c> is 0 in exactly that window and non-zero after, so it is the
/// sentinel: while it reads 0 this class sends none of the four, and the hub renders an absence instead of a
/// heap of nothing. <see cref="GC.GetTotalMemory(bool)"/> is not affected and is sent from the first frame.
/// </para>
/// </summary>
public static class DriverRuntime
{
    /// <summary>
    /// The counters exactly as the runtime handed them over, before any judgement about which of them mean
    /// anything. Split out from <see cref="Sample()"/> so the rule that turns them into presence can be
    /// tested — including the pre-first-collection case, which cannot be reproduced inside a test host that
    /// has already collected.
    /// </summary>
    public readonly record struct GcFacts(
        long ManagedHeapBytes,
        long TotalAllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long GcIndex,
        long GcHeapSizeBytes,
        long CommittedBytes,
        long FragmentedBytes,
        int ThreadPoolThreads,
        long ThreadPoolPendingItems);

    /// <summary>
    /// Ask the runtime. Every call here is a counter read: no allocation beyond the struct, no file, no
    /// syscall, no lock. Measured at ~43 ns (Native AOT) / ~76 ns (CoreCLR) for the set.
    /// </summary>
    public static GcFacts Read()
    {
        var info = GC.GetGCMemoryInfo();
        return new GcFacts(
            // Valid from the first instruction — this one does not wait for a collection.
            ManagedHeapBytes: GC.GetTotalMemory(false),
            // Monotonic. Sent raw so the hub can difference two frames into a rate; sending a rate here
            // would bake this frame's interval into the number.
            TotalAllocatedBytes: GC.GetTotalAllocatedBytes(false),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            GcIndex: info.Index,
            GcHeapSizeBytes: info.HeapSizeBytes,
            CommittedBytes: info.TotalCommittedBytes,
            FragmentedBytes: info.FragmentedBytes,
            ThreadPoolThreads: ThreadPool.ThreadCount,
            ThreadPoolPendingItems: ThreadPool.PendingWorkItemCount);
    }

    /// <summary>Take one reading of this process.</summary>
    public static DriverRuntimeMessage Sample() => Sample(Read());

    /// <summary>
    /// Which of the counters go on the wire. <b>This is the whole presence rule and it is why a zero here is
    /// never a lie.</b> A figure that is set is a figure that was measured, including when it was measured
    /// as zero; a figure that is unset was not on offer.
    /// </summary>
    public static DriverRuntimeMessage Sample(GcFacts f)
    {
        var msg = new DriverRuntimeMessage
        {
            ManagedHeapBytes = f.ManagedHeapBytes,
            TotalAllocatedBytes = f.TotalAllocatedBytes,
            Gen0Collections = f.Gen0Collections,
            Gen1Collections = f.Gen1Collections,
            Gen2Collections = f.Gen2Collections,
            ThreadPoolThreads = f.ThreadPoolThreads,
            ThreadPoolPendingItems = f.ThreadPoolPendingItems,
        };

        // Everything below is zero until a collection has happened — all four of them, measured, on both
        // CoreCLR and Native AOT. Index is the sentinel, and setting any of the others while it reads 0
        // would put a zero on the wire that no reader could tell from a measurement.
        if (f.GcIndex > 0)
        {
            msg.GcIndex = f.GcIndex;
            msg.GcHeapSizeBytes = f.GcHeapSizeBytes;
            msg.CommittedBytes = f.CommittedBytes;
            msg.FragmentedBytes = f.FragmentedBytes;
        }

        return msg;
    }

    /// <summary>The heartbeat frame as it goes on the wire: a device event that is about no device.</summary>
    public static DeviceEventMessage Frame() => new()
    {
        DeviceId = "",
        Type = DeviceEvents.DriverHeartbeat,
        TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Runtime = Sample(),
    };
}
