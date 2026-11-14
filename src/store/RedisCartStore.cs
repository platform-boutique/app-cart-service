using System.Diagnostics;
using Google.Protobuf;
using Grpc.Core;
using Hipstershop;
using Microsoft.Extensions.Caching.Distributed;
using cartservice.services;

namespace cartservice.store
{
    public partial class RedisCartStore(IDistributedCache cache, ILogger<RedisCartStore> logger, CartStoreOptions? options = null) : ICartStore
    {
        /// <summary>Name of the <see cref="ActivitySource"/> that carries the Redis client spans (registered in <see cref="TelemetryExtensions"/>).</summary>
        public const string ActivitySourceName = "Everly.Cart";

        private static readonly ActivitySource Source = new(ActivitySourceName);

        private readonly TimeSpan _timeout = (options ?? new CartStoreOptions()).RedisTimeout;

        // LoggerMessage delegates — CA1848
        [LoggerMessage(Level = LogLevel.Information, Message = "AddItem called for userId={UserId} productId={ProductId} quantity={Quantity}")]
        private static partial void LogAddItem(ILogger logger, string userId, string productId, int quantity);

        [LoggerMessage(Level = LogLevel.Information, Message = "GetCart called for userId={UserId}")]
        private static partial void LogGetCart(ILogger logger, string userId);

        [LoggerMessage(Level = LogLevel.Information, Message = "EmptyCart called for userId={UserId}")]
        private static partial void LogEmptyCart(ILogger logger, string userId);

        // The detail (Redis host, exception text) stays here; the client only gets the generic status of ToClientError.
        [LoggerMessage(Level = LogLevel.Error, Message = "Cart storage {Operation} failed for userId={UserId}")]
        private static partial void LogStoreError(ILogger logger, Exception ex, string operation, string userId);

        public async Task AddItemAsync(string userId, string productId, int quantity, CancellationToken cancellationToken)
        {
            LogAddItem(logger, userId, productId, quantity);

            await RunAsync("AddItem", userId, cancellationToken, async ct =>
            {
                Cart cart;
                byte[]? bytes = await cache.GetAsync(userId, ct);

                if (bytes == null)
                {
                    cart = new Cart { UserId = userId };
                    cart.Items.Add(new CartItem { ProductId = productId, Quantity = quantity });
                }
                else
                {
                    cart = Cart.Parser.ParseFrom(bytes);
                    CartItem? existing = cart.Items.FirstOrDefault(i => i.ProductId == productId);
                    if (existing == null)
                    {
                        cart.Items.Add(new CartItem { ProductId = productId, Quantity = quantity });
                    }
                    else
                    {
                        // long: the sum of two int quantities must not wrap around
                        if ((long)existing.Quantity + quantity > CartValidation.MaxQuantityPerLine)
                        {
                            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                $"the quantity of one product in a cart is limited to {CartValidation.MaxQuantityPerLine}"));
                        }

                        existing.Quantity += quantity;
                    }
                }

                await cache.SetAsync(userId, cart.ToByteArray(), ct);
                return true;
            });
        }

        public Task<Cart> GetCartAsync(string userId, CancellationToken cancellationToken)
        {
            LogGetCart(logger, userId);

            return RunAsync("GetCart", userId, cancellationToken, async ct =>
            {
                byte[]? bytes = await cache.GetAsync(userId, ct);
                return bytes != null ? Cart.Parser.ParseFrom(bytes) : new Cart { UserId = userId };
            });
        }

        public async Task EmptyCartAsync(string userId, CancellationToken cancellationToken)
        {
            LogEmptyCart(logger, userId);

            await RunAsync("EmptyCart", userId, cancellationToken, async ct =>
            {
                await cache.SetAsync(userId, new Cart { UserId = userId }.ToByteArray(), ct);
                return true;
            });
        }

        public async Task<bool> PingAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            try
            {
                _ = await cache.GetAsync("health-ping", deadline.Token).WaitAsync(deadline.Token);
                return true;
            }
#pragma warning disable CA1031 // any failure (including the deadline) means "not serving"
            catch (Exception)
#pragma warning restore CA1031
            {
                return false;
            }
        }

        /// <summary>
        /// Runs the Redis work of one RPC under ONE deadline (<see cref="CartStoreOptions.RedisTimeout"/>, shortened by the caller's token),
        /// inside a client span, and maps every failure to a status without internals.
        /// <c>WaitAsync</c> makes the await return at the deadline even when the cache client does not observe the token mid-command.
        /// </summary>
        private async Task<T> RunAsync<T>(string operation, string userId, CancellationToken callerToken, Func<CancellationToken, Task<T>> work)
        {
            using Activity? span = Source.StartActivity($"redis {operation}", ActivityKind.Client);
            _ = span?.SetTag("db.system.name", "redis");
            _ = span?.SetTag("db.operation.name", operation);

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            deadline.CancelAfter(_timeout);
            try
            {
                return await work(deadline.Token).WaitAsync(deadline.Token);
            }
            catch (RpcException ex)
            {
                // a status we raised ourselves (quantity limit): the client may see it as it is
                _ = span?.SetStatus(ActivityStatusCode.Error, ex.Status.Detail);
                throw;
            }
#pragma warning disable CA1031 // every other failure becomes one generic status below
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogStoreError(logger, ex, operation, userId);
                _ = span?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                span?.AddException(ex);
                throw ToClientError(ex, callerToken);
            }
        }

        private static RpcException ToClientError(Exception ex, CancellationToken callerToken) => ex switch
        {
            OperationCanceledException when callerToken.IsCancellationRequested => new RpcException(new Status(StatusCode.Cancelled, "request cancelled")),
            OperationCanceledException or TimeoutException => new RpcException(new Status(StatusCode.DeadlineExceeded, "cart storage did not answer in time")),
            InvalidProtocolBufferException => new RpcException(new Status(StatusCode.Internal, "internal error")),
            _ => new RpcException(new Status(StatusCode.Unavailable, "cart storage unavailable")),
        };
    }
}
