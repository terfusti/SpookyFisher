using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SpookyFisher;

internal static class Program
{
    private static int Main()
    {
        Action[] tests = { FifoAndBudget, BoundedReentrancy, FailureIsolation, ClearAndClose,
            CloseDuringDrain, ConcurrentCapacity, ConcurrentDelivery, ControlHandshake, PartialOpenCleanup };
        try
        {
            foreach (var test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine(tests.Length + " tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void Unexpected(Exception ex) { throw new Exception("Unexpected command failure.", ex); }

    private static void FifoAndBudget()
    {
        var queue = new CommandQueue<List<int>>(3);
        var values = new List<int>();
        for (int i = 0; i < 3; i++)
        {
            int value = i;
            Assert(queue.TryEnqueue(list => list.Add(value)), "Queue rejected available capacity.");
        }
        Assert(!queue.TryEnqueue(list => list.Add(99)), "Queue exceeded capacity.");
        queue.Drain(values, 2, Unexpected);
        Assert(string.Join(",", values) == "0,1", "FIFO order or frame budget changed.");
        queue.Drain(values, 2, Unexpected);
        Assert(string.Join(",", values) == "0,1,2", "Deferred command was lost.");
    }

    private static void BoundedReentrancy()
    {
        var queue = new CommandQueue<List<int>>(2);
        var values = new List<int>();
        queue.TryEnqueue(list =>
        {
            list.Add(1);
            queue.TryEnqueue(next => next.Add(2));
        });
        queue.Drain(values, 16, Unexpected);
        Assert(values.Count == 1, "Reentrant work ran in the same batch.");
        queue.Drain(values, 16, Unexpected);
        Assert(string.Join(",", values) == "1,2", "Reentrant work was lost.");
    }

    private static void FailureIsolation()
    {
        var queue = new CommandQueue<List<int>>(2);
        var values = new List<int>();
        var failure = new InvalidOperationException("Test failure");
        Exception observed = null;
        queue.TryEnqueue(list => { throw failure; });
        queue.TryEnqueue(list => list.Add(1));
        queue.Drain(values, 2, ex => observed = ex);
        Assert(ReferenceEquals(failure, observed) && values.Count == 1, "A command failure stopped later work.");
    }

    private static void ClearAndClose()
    {
        var queue = new CommandQueue<List<int>>(2);
        var values = new List<int>();
        queue.TryEnqueue(list => list.Add(1));
        queue.Clear();
        Assert(queue.TryEnqueue(list => list.Add(2)), "Clear closed the queue.");
        queue.Close();
        queue.Close();
        Assert(!queue.TryEnqueue(list => list.Add(3)), "Closed queue accepted work.");
        queue.Drain(values, 16, Unexpected);
        Assert(values.Count == 0, "Discarded work ran after shutdown.");
    }

    private static void CloseDuringDrain()
    {
        var queue = new CommandQueue<List<int>>(2);
        var values = new List<int>();
        queue.TryEnqueue(list => { list.Add(1); queue.Close(); });
        queue.TryEnqueue(list => list.Add(2));
        queue.Drain(values, 16, Unexpected);
        Assert(string.Join(",", values) == "1", "Shutdown failed to cancel the rest of the batch.");
    }

    private static void ConcurrentCapacity()
    {
        var queue = new CommandQueue<List<int>>(16);
        int accepted = 0;
        Parallel.For(0, 256, i =>
        {
            if (queue.TryEnqueue(list => list.Add(i))) Interlocked.Increment(ref accepted);
        });
        var values = new List<int>();
        queue.Drain(values, 256, Unexpected);
        Assert(accepted == 16 && values.Count == 16, "Concurrent producers violated capacity.");
    }

    private static void ConcurrentDelivery()
    {
        var queue = new CommandQueue<HashSet<int>>(256);
        Parallel.For(0, 256, i =>
        {
            Assert(queue.TryEnqueue(values =>
                Assert(values.Add(i), "Command executed twice.")), "Available slot was lost.");
        });
        var seen = new HashSet<int>();
        for (int i = 0; i < 16; i++) queue.Drain(seen, 16, Unexpected);
        Assert(seen.Count == 256, "Concurrent command was lost.");
    }

    private static void ControlHandshake()
    {
        string prefix = "Local\\SpookyFisher.Tests." + Guid.NewGuid().ToString("N");
        using (var toggle = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Toggle"))
        using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Stop"))
        using (var detached = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Detached"))
        using (var controls = new ControlEvents(prefix))
        {
            Assert(!controls.TakeToggle() && !controls.StopRequested, "Events started signaled.");
            toggle.Set();
            Assert(controls.TakeToggle() && !controls.TakeToggle(), "Toggle was not consumed once.");
            stop.Set();
            Assert(controls.StopRequested && controls.StopRequested, "Stop request was consumed.");
            Assert(!detached.WaitOne(0), "Detach acknowledged before cleanup.");
            controls.AcknowledgeDetach();
            Assert(detached.WaitOne(0), "Native side did not receive acknowledgement.");
        }
    }

    private static void PartialOpenCleanup()
    {
        string prefix = "Local\\SpookyFisher.Tests." + Guid.NewGuid().ToString("N");
        using (var toggle = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Toggle"))
        {
            bool failed = false;
            try { using (new ControlEvents(prefix)) { } }
            catch (WaitHandleCannotBeOpenedException) { failed = true; }
            Assert(failed, "Missing control event did not fail initialization.");
        }
        EventWaitHandle leaked;
        bool found = EventWaitHandle.TryOpenExisting(prefix + ".Toggle", out leaked);
        leaked?.Dispose();
        Assert(!found, "Partial initialization leaked an event handle.");
    }
}
