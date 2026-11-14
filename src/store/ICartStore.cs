using Hipstershop;

namespace cartservice.store
{
    /// <summary>
    /// Abstraction over the cart persistence layer.
    /// Implemented by <see cref="RedisCartStore"/>; can be replaced with an
    /// in-memory stub in unit tests without touching infrastructure.
    /// The token is the gRPC call's cancellation token (client deadline or disconnect); the store adds its own upper bound.
    /// </summary>
    public interface ICartStore
    {
        Task AddItemAsync(string userId, string productId, int quantity, CancellationToken cancellationToken);
        Task<Cart> GetCartAsync(string userId, CancellationToken cancellationToken);
        Task EmptyCartAsync(string userId, CancellationToken cancellationToken);
        Task<bool> PingAsync(CancellationToken cancellationToken);
    }
}
