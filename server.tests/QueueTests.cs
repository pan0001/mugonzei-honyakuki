using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Mugonzei.Server;

internal static partial class Program
{
    private static async Task QueuedCancellationReleasesCapacity()
    {
        using var fixture = new Fixture();
        string user = fixture.User(20), other = fixture.User(5);
        var processor = new QueueGateProcessor(1);
        using var service = new TranslationService(fixture.Store, processor,
            new TranslationOptions { Concurrency = 1, QueueCapacity = 4 }, NullLogger<TranslationService>.Instance);
        await service.StartAsync(default);
        try
        {
            Guid running = Guid.NewGuid(), first = Guid.NewGuid(), cancelled = Guid.NewGuid(), replacement = Guid.NewGuid();
            service.Submit(user, running, TextInput("running"));
            await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal("queued", service.Submit(user, first, TextInput("first queued")).Status, "First waiting task is queued");
            Equal("queued", service.Submit(user, cancelled, TextInput("cancelled queued")).Status, "Second waiting task fills this account's waiting allowance");
            Guid rejectedId = Guid.NewGuid();
            var rejected = service.Submit(user, rejectedId, TextInput("overflow"));
            Equal("queue_full", rejected.Error, "One account cannot occupy every waiting place");
            Equal("queue_full", service.Submit(user, rejectedId, TextInput("overflow")).Error, "An already-rejected job has stable retry semantics");
            Equal("queued", service.Submit(user, first, TextInput("first queued")).Status, "Existing job replay bypasses queue admission");
            Guid[] otherIds = [Guid.NewGuid(), Guid.NewGuid()];
            foreach (Guid id in otherIds) Equal("queued", service.Submit(other, id, TextInput("other")).Status, "A second account can use the other half");
            string third = fixture.User(1);
            Equal("queue_full", service.Submit(third, Guid.NewGuid(), TextInput("full")).Error, "Global queue capacity still blocks a third account when full");
            Equal(0L, fixture.Store.GetAccount(third).Reserved, "Full queue does not tie up a rejected account's credit");
            foreach (Guid id in otherIds) service.Cancel(other, id);
            Throws(404, () => service.Get(other, cancelled));
            service.Cancel(other, cancelled); // Same identifier in another account must not remove this user's task.
            Equal("queued", service.Get(user, cancelled).Status, "Cross-account cancellation does not remove queued work");
            Equal("cancelled", service.Cancel(user, cancelled).Status, "Cancellation terminates the queued task immediately");
            Equal("queued", service.Submit(user, replacement, TextInput("replacement")).Status,
                "Cancelling a non-head queued task frees its slot before the running model completes");
            Equal(3L, fixture.Store.GetAccount(user).Reserved, "Only running and live queued tasks retain reservations");
            processor.Release.TrySetResult();
            foreach (Guid id in new[] { running, first, replacement }) Equal("succeeded", (await Terminal(service, user, id)).Status, "Surviving queue work completes");
            Check(processor.Texts.SequenceEqual(["running", "first queued", "replacement"]), "Cancelled work never reaches the model; surviving FIFO order is preserved");
            Equal(17L, fixture.Store.GetAccount(user).Balance, "Only three successful tasks debit credits");
            Equal(0L, fixture.Store.GetAccount(user).Reserved, "Queue drain releases every reservation");
        }
        finally { processor.Release.TrySetResult(); await service.StopAsync(default); }
    }

    private static async Task QueueWorkersAndShutdown()
    {
        using var fixture = new Fixture(); string user = fixture.User(20);
        var processor = new QueueGateProcessor(2);
        using var service = new TranslationService(fixture.Store, processor,
            new TranslationOptions { Concurrency = 2, QueueCapacity = 5 }, NullLogger<TranslationService>.Instance);
        await service.StartAsync(default);
        try
        {
            Guid[] running = [Guid.NewGuid(), Guid.NewGuid()];
            foreach (Guid id in running) service.Submit(user, id, TextInput("running"));
            await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Guid[] cancelled = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
            foreach (Guid id in cancelled) service.Submit(user, id, TextInput("cancelled"));
            await Task.WhenAll(cancelled.Select(id => Task.Run(() => service.Cancel(user, id))));
            Guid[] replacements = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
            foreach (Guid id in replacements)
                Equal("queued", service.Submit(user, id, TextInput("replacement")).Status, "Concurrent cancellations release every waiting slot");
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Equal(2, processor.Texts.Count, "Stopping never processes the waiting screenshots");
            foreach (Guid id in running.Concat(replacements)) Equal("failed", service.Get(user, id).Status, "Stop releases running and waiting work as failed");
            foreach (Guid id in cancelled) Equal("cancelled", service.Get(user, id).Status, "Stop preserves earlier explicit cancellations");
            Equal(20L, fixture.Store.GetAccount(user).Balance, "Shutdown does not debit unfinished work");
            Equal(0L, fixture.Store.GetAccount(user).Reserved, "Shutdown leaves no abandoned queue reservations");
            Equal("server_stopped", service.Submit(user, Guid.NewGuid(), TextInput("after stop")).Error, "Stopped service rejects new work without retaining credit");
            Equal(0L, fixture.Store.GetAccount(user).Reserved, "Submission after stop reserves no lasting credit");
        }
        finally { processor.Release.TrySetResult(); await service.StopAsync(default); }
    }

    private static async Task QueueSubmissionCancellationRaces()
    {
        using var fixture = new Fixture(); string user = fixture.User(30);
        using var service = new TranslationService(fixture.Store, new FakeProcessor(),
            new TranslationOptions { Concurrency = 3, QueueCapacity = 40 }, NullLogger<TranslationService>.Instance);
        await service.StartAsync(default);
        try
        {
            Guid[] ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
            await Task.WhenAll(ids.SelectMany(id => new Task[]
            {
                Task.Run(() => service.Submit(user, id, TextInput("race"))),
                Task.Run(() => service.Cancel(user, id))
            }));
            var results = await Task.WhenAll(ids.Select(id => Terminal(service, user, id)));
            Check(results.All(result => result.Status is "succeeded" or "cancelled"), "Submission/insertion/cancellation races do not strand queued work");
            Equal(30L - results.Count(result => result.Status == "succeeded"), fixture.Store.GetAccount(user).Balance,
                "Concurrent worker completion charges exactly the winning successful tasks");
            Equal(0L, fixture.Store.GetAccount(user).Reserved, "Cancellation racing queue insertion releases all reservations");
            Guid next = Guid.NewGuid(); service.Submit(user, next, TextInput("still usable"));
            Equal("succeeded", (await Terminal(service, user, next)).Status, "Workers still wake correctly after concurrent cancellations");
        }
        finally { await service.StopAsync(default); }
    }

    private sealed class QueueGateProcessor(int blockedCalls) : ITranslationProcessor
    {
        private int calls;
        public bool IsAvailable => true;
        public ConcurrentQueue<string> Texts { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranslationResult> ProcessAsync(TranslationInput input, CancellationToken token)
        {
            Texts.Enqueue(input.Text ?? "image");
            int call = Interlocked.Increment(ref calls);
            if (call == blockedCalls) Started.TrySetResult();
            if (call <= blockedCalls) await Release.Task.WaitAsync(token);
            return new(false, input.Text ?? "image", "synthetic translation");
        }
    }
}
