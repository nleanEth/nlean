namespace Lean.Network;

public static class LeanRpcResponseCodes
{
    public const byte Success = 0;
    public const byte InvalidRequest = 1;
    public const byte ServerError = 2;
    public const byte ResourceUnavailable = 3;
}
