namespace SteamVrTranslator;

/// <summary>One combined atlas survives hiding and stage changes; animation changes only its sampled tile.</summary>
internal sealed class HoldAtlasPresentationState
{
    public bool IsUploaded { get; private set; }
    public bool IsReady { get; private set; }
    public bool? CancelStage { get; private set; }
    public int? Bucket { get; private set; }

    public bool Prepare(Action upload, Func<bool> textureReady)
    {
        if (!IsUploaded)
        {
            upload();
            IsUploaded = true;
        }
        if (!IsReady) IsReady = textureReady();
        return IsReady;
    }

    public void Update(bool cancelStage, int bucket, Action<int> setBounds)
    {
        if (bucket is < 0 or > 60) throw new ArgumentOutOfRangeException(nameof(bucket));
        if (!IsReady) throw new InvalidOperationException("圆环纹理尚未加载完成。");
        if (CancelStage != cancelStage || Bucket != bucket)
        {
            setBounds(bucket);
            CancelStage = cancelStage;
            Bucket = bucket;
        }
    }

    public void Reset()
    {
        IsUploaded = false;
        IsReady = false;
        CancelStage = null;
        Bucket = null;
    }
}
