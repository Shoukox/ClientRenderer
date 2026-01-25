namespace ClientRenderer.Models
{
    public record Result<T>
    {
        public bool Success { get; init; }
        public T? Output { get; init; }
        public Exception? Exception { get; init; }

        public static Result<T> FromSuccess(T output) => new Result<T>
        {
            Success = true,
            Output = output,
            Exception = null
        };

        public static Result<T> FromFailure(Exception exception) => new Result<T>
        {
            Success = false,
            Output = default,
            Exception = exception
        };
    }

    public record Result
    {
        public bool Success { get; init; }
        public Exception? Exception { get; init; }

        public static Result FromSuccess() => new Result
        {
            Success = true,
            Exception = null
        };

        public static Result FromFailure(Exception exception) => new Result
        {
            Success = false,
            Exception = exception
        };
    }
}
