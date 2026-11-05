namespace Mugonzei.Server;

public sealed class TranslationUploadGate(TimeSpan timeout)
{
    private const int GlobalLimit = 4;
    private const int AccountLimit = 2;
    private readonly object gate = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private int total;

    public async Task<TranslationInput> ReadAsync(string userId, HttpContext context)
    {
        var aborted = context.RequestAborted;
        aborted.ThrowIfCancellationRequested();
        lock (gate)
        {
            int count = counts.GetValueOrDefault(userId);
            if (count >= AccountLimit || total >= GlobalLimit)
                throw new ApiException(429, "uploads_busy", "上传通道繁忙，请稍后重试同一请求编号。");
            counts[userId] = count + 1;
            total++;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            deadline.CancelAfter(timeout);
            try
            {
                var input = await TranslationInput.ReadRequestAsync(context.Request.Body, context.Request.ContentLength, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                return input;
            }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw UploadTimeout(); }
            catch (BadHttpRequestException error) when (error.StatusCode == 408)
            { throw UploadTimeout(); }
            catch (IOException) when (aborted.IsCancellationRequested)
            { throw new OperationCanceledException(aborted); }
        }
        finally
        {
            lock (gate)
            {
                if (--counts[userId] == 0) counts.Remove(userId);
                total--;
            }
        }
    }

    private static ApiException UploadTimeout() => new(408, "upload_timeout", "上传超时，请重试同一请求编号。");
}
