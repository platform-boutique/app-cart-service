using cartservice.services;
using cartservice.store;
using Grpc.Core;
using Hipstershop;
using Moq;
using Xunit;
using CartService = cartservice.services.CartService;

namespace cartservice.tests
{
    /// <summary>Negative tests (TEST-04): bad input is INVALID_ARGUMENT and never reaches the store.</summary>
    public class CartValidationTests
    {
        private static AddItemRequest Add(string user, string product, int quantity) =>
            new() { UserId = user, Item = new CartItem { ProductId = product, Quantity = quantity } };

        private static async Task<RpcException> Rejected(Func<CartService, Task> call, Mock<ICartStore> store)
        {
            RpcException ex = await Assert.ThrowsAsync<RpcException>(() => call(new CartService(store.Object)));
            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
            store.VerifyNoOtherCalls(); // nothing was read or written
            return ex;
        }

        [Theory]
        [InlineData("", "p1", 1)]
        [InlineData("   ", "p1", 1)]
        [InlineData("u1", "", 1)]
        [InlineData("u1", " \t", 1)]
        [InlineData("u1", "p1", 0)]
        [InlineData("u1", "p1", -1)]
        [InlineData("u1", "p1", int.MinValue)]
        [InlineData("u1", "p1", CartValidation.MaxQuantityPerAdd + 1)]
        [InlineData("u1", "p1", int.MaxValue)]
        public async Task AddItem_InvalidInput_IsInvalidArgument(string user, string product, int quantity)
        {
            Mock<ICartStore> store = new();
            _ = await Rejected(svc => svc.AddItem(Add(user, product, quantity), TestServerCallContext.Create()), store);
        }

        [Fact]
        public async Task AddItem_MissingItem_IsInvalidArgument()
        {
            Mock<ICartStore> store = new();
            _ = await Rejected(svc => svc.AddItem(new AddItemRequest { UserId = "u1" }, TestServerCallContext.Create()), store);
        }

        [Fact]
        public async Task AddItem_TooLongIds_AreInvalidArgument()
        {
            string tooLong = new('x', CartValidation.MaxIdLength + 1);
            Mock<ICartStore> store = new();
            _ = await Rejected(svc => svc.AddItem(Add(tooLong, "p1", 1), TestServerCallContext.Create()), store);
            _ = await Rejected(svc => svc.AddItem(Add("u1", tooLong, 1), TestServerCallContext.Create()), store);
        }

        [Fact]
        public async Task AddItem_ErrorMessage_DoesNotEchoTheInput()
        {
            Mock<ICartStore> store = new();
            RpcException ex = await Rejected(svc => svc.AddItem(Add("u1", new string('Z', 500), 1), TestServerCallContext.Create()), store);
            Assert.DoesNotContain("ZZZ", ex.Status.Detail, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(CartValidation.MaxQuantityPerAdd)]
        public async Task AddItem_QuantityAtTheBounds_IsAccepted(int quantity)
        {
            Mock<ICartStore> store = new();
            _ = await new CartService(store.Object).AddItem(Add("u1", "p1", quantity), TestServerCallContext.Create());
            store.Verify(s => s.AddItemAsync("u1", "p1", quantity, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AddItem_IdsAtTheLengthLimit_AreAccepted()
        {
            string longest = new('x', CartValidation.MaxIdLength);
            Mock<ICartStore> store = new();
            _ = await new CartService(store.Object).AddItem(Add(longest, longest, 1), TestServerCallContext.Create());
            store.Verify(s => s.AddItemAsync(longest, longest, 1, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        public async Task GetCart_EmptyUserId_IsInvalidArgument(string user)
        {
            Mock<ICartStore> store = new();
            _ = await Rejected(svc => svc.GetCart(new GetCartRequest { UserId = user }, TestServerCallContext.Create()), store);
        }

        [Theory]
        [InlineData("")]
        [InlineData("  ")]
        public async Task EmptyCart_EmptyUserId_IsInvalidArgument(string user)
        {
            Mock<ICartStore> store = new();
            _ = await Rejected(svc => svc.EmptyCart(new EmptyCartRequest { UserId = user }, TestServerCallContext.Create()), store);
        }

        [Fact]
        public async Task GetCart_TooLongUserId_IsInvalidArgument()
        {
            Mock<ICartStore> store = new();
            _ = await Rejected(svc => svc.GetCart(new GetCartRequest { UserId = new string('x', CartValidation.MaxIdLength + 1) }, TestServerCallContext.Create()), store);
        }

        [Fact]
        public async Task CallCancellationToken_IsPassedToTheStore()
        {
            using CancellationTokenSource cts = new();
            Mock<ICartStore> store = new();
            store.Setup(s => s.GetCartAsync("u1", cts.Token)).ReturnsAsync(new Cart { UserId = "u1" });
            _ = await new CartService(store.Object).GetCart(new GetCartRequest { UserId = "u1" }, TestServerCallContext.Create(cts.Token));
            store.Verify(s => s.GetCartAsync("u1", cts.Token), Times.Once);
        }
    }
}
