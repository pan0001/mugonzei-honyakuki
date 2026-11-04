using Valve.VR;

namespace SteamVrTranslator;

/// <summary>An overlay operation failed; this alone does not imply that SteamVR input disconnected.</summary>
public sealed class VrOverlayException : InvalidOperationException
{
    public string Operation { get; }
    public EVROverlayError Error { get; }

    public VrOverlayException(string operation, EVROverlayError error)
        : base($"{operation}失败：{error}")
    {
        Operation = operation;
        Error = error;
    }
}
