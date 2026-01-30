#nullable enable
namespace Phoenix
{
    /// <summary>
    /// Result of a channel join operation.
    /// </summary>
    public readonly struct JoinResult
    {
        public bool IsSuccess { get; }
        public Reply? Reply { get; }
        public string? Error { get; }

        private JoinResult(bool isSuccess, Reply? reply, string? error)
        {
            IsSuccess = isSuccess;
            Reply = reply;
            Error = error;
        }

        public static JoinResult Success(Reply reply) => new(true, reply, null);
        public static JoinResult Failure(Reply reply) => new(false, reply, reply.Status);
        public static JoinResult Timeout() => new(false, null, "Timeout");

        public static implicit operator bool(JoinResult result) => result.IsSuccess;
    }
}
