namespace Boson;

public static class ExitCodes
{
    public const int Success = 0;
    public const int UserError = 1;
    public const int RuntimeFailure = 2;
    public const int LockContention = 3;
    public const int InternalBug = 99;
}
