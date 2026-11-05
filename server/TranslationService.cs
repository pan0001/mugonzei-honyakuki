using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mugonzei.Server;

public sealed class TranslationService : BackgroundService
{
    private sealed record Pending(string UserId, string Id, TranslationInput Input);
    private readonly Store store;
    private readonly ITranslationProcessor processor;
    private readonly TranslationOptions options;
    private readonly Channel<Pending> queue;
    private readonly ConcurrentDictionary<(string UserId, string Id), CancellationTokenSource> active = new();
    private readonly ILogger<TranslationService> logger;

    public TranslationService(Store store, ITranslationProcessor processor, TranslationOptions options, ILogger<TranslationService> logger)
    {
        this.store = store;
        this.processor = processor;
        this.options = options;
        this.logger = logger;
        queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(Math.Clamp(options.QueueCapacity, 1, 64))
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = options.Concurrency <= 1 });
    }

    public TranslationTaskDto Submit(string userId, Guid id, TranslationInput input)
    {
        string key = id.ToString("D"), hash = input.BodyHash();
        try
        {
            var existing = store.GetTranslation(userId, key);
            if (Store.IsPreCancelled(existing)) return TranslationTaskDto.From(existing);
            if (existing.BodyHash != hash) throw new ApiException(409, "request_conflict", "该请求编号已用于不同的翻译内容。");
            return TranslationTaskDto.From(existing);
        }
        catch (ApiException error) when (error.StatusCode == 404) { }
        if (!processor.IsAvailable) throw new ApiException(503, "service_unavailable", "官方翻译服务尚未配置，请稍后再试。");
        var reservation = store.ReserveTranslation(userId, key, hash, input.PersistenceJson());
        if (!reservation.Created) return TranslationTaskDto.From(reservation.Job);
        if (!queue.Writer.TryWrite(new(userId, key, input)))
            return TranslationTaskDto.From(store.FailTranslation(userId, key, "failed", "queue_full"));
        return TranslationTaskDto.From(reservation.Job);
    }

    public TranslationTaskDto Get(string userId, Guid id) => TranslationTaskDto.From(store.GetTranslation(userId, id.ToString("D")));

    public TranslationTaskDto Cancel(string userId, Guid id)
    {
        string key = id.ToString("D");
        // The transaction decides the winner. Cancelling a completed job never refunds it.
        var job = store.CancelTranslation(userId, key);
        if (job.Status == "cancelled" && active.TryGetValue((userId, key), out var cancellation))
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return TranslationTaskDto.From(job);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Images are deliberately not persisted, so interrupted work is never resumed or charged.
        store.RecoverTranslations();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(Enumerable.Range(0, Math.Clamp(options.Concurrency, 1, 8)).Select(_ => WorkAsync(stoppingToken)));
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var pending in queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var key = (pending.UserId, pending.Id);
                if (!active.TryAdd(key, cancellation)) continue;
                try
                {
                    if (!store.StartTranslation(pending.UserId, pending.Id)) continue;
                    cancellation.Token.ThrowIfCancellationRequested();
                    var result = await processor.ProcessAsync(pending.Input, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    result.Validate();
                    store.CompleteTranslation(pending.UserId, pending.Id,
                        JsonSerializer.Serialize(result, TranslationInput.Json), charge: !result.NoText);
                }
                catch (OperationCanceledException)
                {
                    store.FailTranslation(pending.UserId, pending.Id, stoppingToken.IsCancellationRequested ? "failed" : "cancelled",
                        stoppingToken.IsCancellationRequested ? "server_stopped" : "cancelled");
                }
                catch (TranslationProcessingException error)
                {
                    store.FailTranslation(pending.UserId, pending.Id, "failed", error.Code);
                }
                catch (Exception)
                {
                    // Never log source text, images, provider response bodies, API keys or raw exception messages.
                    logger.LogWarning("Translation task failed: {TaskId}", pending.Id);
                    store.FailTranslation(pending.UserId, pending.Id, "failed", "processing_failed");
                }
                finally { active.TryRemove(key, out _); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        store.RecoverTranslations();
        // Release queued screenshots even if this service object remains referenced after shutdown.
        while (queue.Reader.TryRead(out _)) { }
    }
}

public static class TranslationEndpoints
{
    public static void AddTranslationServices(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = new TranslationOptions();
        configuration.GetSection("Translation").Bind(options);
        if (options.Mock && !environment.IsDevelopment())
            throw new InvalidOperationException("Translation:Mock 只能在 Development 环境启用。");
        services.TryAddSingleton(options);
        services.TryAddSingleton<ITranslationProcessor>(_ => options.Mock
            ? new DevelopmentTranslationProcessor() : new TranslationProcessor(options));
        services.AddSingleton<TranslationService>();
        services.AddHostedService(provider => provider.GetRequiredService<TranslationService>());
    }

    public static void MapTranslationEndpoints(WebApplication app)
    {
        app.MapPut("/v1/translations/{requestId:guid}", async (Guid requestId, HttpContext context, TranslationService service) =>
        {
            string userId = AuthService.UserId(context);
            if (context.Request.ContentLength > TranslationInput.MaxRequestBytes)
                throw new ApiException(413, "request_too_large", "截图请求过大。");
            TranslationRequest? request;
            try
            {
                byte[] body = await TranslationStreams.ReadBoundedAsync(context.Request.Body, TranslationInput.MaxRequestBytes, context.RequestAborted);
                request = JsonSerializer.Deserialize<TranslationRequest>(body, TranslationInput.Json);
            }
            catch (TranslationProcessingException)
            { throw new ApiException(413, "request_too_large", "截图请求过大。"); }
            catch (JsonException)
            { throw new ApiException(400, "invalid_request", "翻译请求格式无效。"); }
            if (request is null) throw new ApiException(400, "invalid_request", "翻译请求不能为空。");
            var result = service.Submit(userId, requestId, TranslationInput.Validate(request));
            // RequestAborted is intentionally never forwarded to accepted background work.
            return Results.Json(result, statusCode: result.Status is "queued" or "running" ? 202 : 200);
        }).RequireRateLimiting("translation-submit");
        app.MapGet("/v1/translations/{requestId:guid}", (Guid requestId, HttpContext context, TranslationService service)
            => Results.Ok(service.Get(AuthService.UserId(context), requestId)));
        app.MapPost("/v1/translations/{requestId:guid}/cancel", (Guid requestId, HttpContext context, TranslationService service)
            => Results.Ok(service.Cancel(AuthService.UserId(context), requestId))).RequireRateLimiting("translation-cancel");
    }
}
