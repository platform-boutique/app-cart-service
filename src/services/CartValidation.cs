using Grpc.Core;
using Hipstershop;

namespace cartservice.services
{
    /// <summary>Input rules of the cart RPCs. A violation is INVALID_ARGUMENT; the message names the field, never echoes the value.</summary>
    public static class CartValidation
    {
        /// <summary>Longest accepted user_id / product_id (characters). Same as the session id limit of checkout-service.</summary>
        public const int MaxIdLength = 128;

        /// <summary>Largest quantity of one AddItem call. The shop UI sends at most 10.</summary>
        public const int MaxQuantityPerAdd = 100;

        /// <summary>Largest total quantity of one product in a cart (sum over several AddItem calls).</summary>
        public const int MaxQuantityPerLine = 1000;

        public static void ValidateUserId(string? userId) => ValidateId(userId, "user_id");

        public static void ValidateAddItem(AddItemRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateUserId(request.UserId);
            if (request.Item is null)
            {
                throw Invalid("item is required");
            }

            ValidateId(request.Item.ProductId, "item.product_id");
            if (request.Item.Quantity is < 1 or > MaxQuantityPerAdd)
            {
                throw Invalid($"item.quantity must be between 1 and {MaxQuantityPerAdd}");
            }
        }

        private static void ValidateId(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw Invalid($"{field} is required");
            }

            if (value.Length > MaxIdLength)
            {
                throw Invalid($"{field} must be at most {MaxIdLength} characters");
            }
        }

        private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));
    }
}
