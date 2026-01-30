#nullable enable
using System.Diagnostics.CodeAnalysis;

namespace Phoenix
{
    /// <summary>
    /// Result of a channel push operation.
    /// </summary>
    public readonly struct PushResult
    {
        public bool IsSuccess { get; }
        public Reply? Reply { get; }
        public ReplyStatus Status { get; }

        private PushResult(bool isSuccess, Reply? reply, ReplyStatus status)
        {
            IsSuccess = isSuccess;
            Reply = reply;
            Status = status;
        }

        public static PushResult Success(Reply reply) => new(true, reply, ReplyStatus.Ok);
        public static PushResult Failure(Reply reply, ReplyStatus status) => new(false, reply, status);
        public static PushResult Timeout() => new(false, null, ReplyStatus.Timeout);

        public static implicit operator bool(PushResult result) => result.IsSuccess;
    }

    /// <summary>
    /// Result of a channel push operation with typed response.
    /// </summary>
    public readonly struct PushResult<T>
    {
        public bool IsSuccess { get; }

        [MaybeNull]
        public T Response { get; }

        public Reply? Reply { get; }
        public ReplyStatus Status { get; }

        private PushResult(bool isSuccess, T response, Reply? reply, ReplyStatus status)
        {
            IsSuccess = isSuccess;
            Response = response;
            Reply = reply;
            Status = status;
        }

        public static PushResult<T> Success(T response, Reply reply) => new(true, response, reply, ReplyStatus.Ok);
        public static PushResult<T> Failure(Reply reply, ReplyStatus status) => new(false, default!, reply, status);
        public static PushResult<T> Timeout() => new(false, default!, null, ReplyStatus.Timeout);

        public static implicit operator bool(PushResult<T> result) => result.IsSuccess;
    }
}
