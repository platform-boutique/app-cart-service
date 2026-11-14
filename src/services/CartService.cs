using Grpc.Core;
using Hipstershop;
using cartservice.store;

namespace cartservice.services
{
    public class CartService(ICartStore store) : Hipstershop.CartService.CartServiceBase
    {
        private static readonly Empty Empty = new();

        public override async Task<Empty> AddItem(AddItemRequest request, ServerCallContext context)
        {
            CartValidation.ValidateAddItem(request);
            await store.AddItemAsync(request.UserId, request.Item.ProductId, request.Item.Quantity, context.CancellationToken);
            return Empty;
        }

        public override async Task<Cart> GetCart(GetCartRequest request, ServerCallContext context)
        {
            CartValidation.ValidateUserId(request.UserId);
            return await store.GetCartAsync(request.UserId, context.CancellationToken);
        }

        public override async Task<Empty> EmptyCart(EmptyCartRequest request, ServerCallContext context)
        {
            CartValidation.ValidateUserId(request.UserId);
            await store.EmptyCartAsync(request.UserId, context.CancellationToken);
            return Empty;
        }
    }
}
