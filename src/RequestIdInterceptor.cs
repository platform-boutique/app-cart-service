using Grpc.Core;
using Grpc.Core.Interceptors;

namespace cartservice
{
    /// <summary>
    /// Opens a logging scope with request_id when the caller sent an x-request-id header, so every log line of that call
    /// carries it. No header, no field (callers today do not forward one; trace_id is the correlation key).
    /// </summary>
    public sealed class RequestIdInterceptor(ILogger<RequestIdInterceptor> logger) : Interceptor
    {
        public const string Header = "x-request-id";
        private const int MaxLength = 64;

        public static string? Valid(string? value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= MaxLength && value.All(c => c > ' ' && c < '\u007f')
                ? value
                : null;

        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(continuation);

            string? id = Valid(context.RequestHeaders.GetValue(Header));
            if (id is null)
            {
                return await continuation(request, context);
            }

            using IDisposable? scope = logger.BeginScope(new KeyValuePair<string, object?>[] { new("request_id", id) });
            return await continuation(request, context);
        }
    }
}
