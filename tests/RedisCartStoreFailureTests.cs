using System.Diagnostics;
using cartservice.services;
using cartservice.store;
using Google.Protobuf;
using Grpc.Core;
using Hipstershop;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace cartservice.tests
{
    /// <summary>
    /// Failure paths of <see cref="RedisCartStore"/> against a stubbed cache (no Redis needed): what the client sees,
    /// the deadline, the caller's cancellation and the client span. Waits are bounded by a generous guard, never measured.
    /// </summary>
    public class RedisCartStoreFailureTests
    {
        private const string Secret = "redis-secret-host.internal:6379 password=hunter2";
        private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

        private static RedisCartStore Store(IDistributedCache cache, int timeoutMs = 100) =>
            new(cache, NullLogger<RedisCartStore>.Instance, new CartStoreOptions { RedisTimeout = TimeSpan.FromMilliseconds(timeoutMs) });

        private static Mock<IDistributedCache> Failing(Exception ex)
        {
            Mock<IDistributedCache> cache = new();
            _ = cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(ex);
            _ = cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>())).ThrowsAsync(ex);
            return cache;
        }

        /// <summary>A cache whose calls never complete and ignore the token (the worst case for a deadline).</summary>
        private static Mock<IDistributedCache> Hanging()
        {
            Mock<IDistributedCache> cache = new();
            TaskCompletionSource<byte[]?> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(never.Task);
            _ = cache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>())).Returns(new TaskCompletionSource().Task);
            return cache;
        }

        private static async Task<RpcException> Fails(Func<RedisCartStore, Task> call, RedisCartStore store) =>
            await Assert.ThrowsAsync<RpcException>(() => call(store).WaitAsync(Guard));

        // ── No internals to the client ────────────────────────────────────────────

        [Fact]
        public async Task AddItem_RedisFails_IsUnavailableWithoutTheExceptionText()
        {
            RedisCartStore store = Store(Failing(new InvalidOperationException(Secret)).Object);
            RpcException ex = await Fails(s => s.AddItemAsync("u1", "p1", 1, CancellationToken.None), store);
            Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
            AssertGeneric(ex);
        }

        [Fact]
        public async Task GetCart_RedisFails_IsUnavailableWithoutTheExceptionText()
        {
            RedisCartStore store = Store(Failing(new InvalidOperationException(Secret)).Object);
            RpcException ex = await Fails(s => s.GetCartAsync("u1", CancellationToken.None), store);
            Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
            AssertGeneric(ex);
        }

        [Fact]
        public async Task EmptyCart_RedisFails_IsUnavailableWithoutTheExceptionText()
        {
            RedisCartStore store = Store(Failing(new InvalidOperationException(Secret)).Object);
            RpcException ex = await Fails(s => s.EmptyCartAsync("u1", CancellationToken.None), store);
            Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
            AssertGeneric(ex);
        }

        [Fact]
        public async Task GetCart_CorruptValue_IsInternalWithoutDetail()
        {
            Mock<IDistributedCache> cache = new();
            _ = cache.Setup(c => c.GetAsync("u1", It.IsAny<CancellationToken>())).ReturnsAsync([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
            RpcException ex = await Fails(s => s.GetCartAsync("u1", CancellationToken.None), Store(cache.Object));
            Assert.Equal(StatusCode.Internal, ex.StatusCode);
            AssertGeneric(ex);
        }

        private static void AssertGeneric(RpcException ex)
        {
            Assert.DoesNotContain("hunter2", ex.Status.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("redis-secret-host", ex.Status.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperation", ex.Status.Detail, StringComparison.Ordinal);
            Assert.Null(ex.Status.DebugException);
        }

        // ── Deadline and cancellation ─────────────────────────────────────────────

        [Fact]
        public async Task GetCart_RedisNeverAnswers_IsDeadlineExceeded()
        {
            RpcException ex = await Fails(s => s.GetCartAsync("u1", CancellationToken.None), Store(Hanging().Object));
            Assert.Equal(StatusCode.DeadlineExceeded, ex.StatusCode);
        }

        [Fact]
        public async Task AddItem_RedisNeverAnswers_IsDeadlineExceeded()
        {
            RpcException ex = await Fails(s => s.AddItemAsync("u1", "p1", 1, CancellationToken.None), Store(Hanging().Object));
            Assert.Equal(StatusCode.DeadlineExceeded, ex.StatusCode);
        }

        [Fact]
        public async Task EmptyCart_RedisNeverAnswers_IsDeadlineExceeded()
        {
            RpcException ex = await Fails(s => s.EmptyCartAsync("u1", CancellationToken.None), Store(Hanging().Object));
            Assert.Equal(StatusCode.DeadlineExceeded, ex.StatusCode);
        }

        [Fact]
        public async Task GetCart_CallerCancels_IsCancelledNotDeadlineExceeded()
        {
            using CancellationTokenSource caller = new();
            // the configured bound is the maximum allowed, so only the caller can end the call here
            RedisCartStore store = Store(Hanging().Object, CartStoreOptions.MaxRedisTimeoutMs);
            Task<Cart> call = store.GetCartAsync("u1", caller.Token);
            await caller.CancelAsync();
            RpcException ex = await Assert.ThrowsAsync<RpcException>(() => call.WaitAsync(Guard));
            Assert.Equal(StatusCode.Cancelled, ex.StatusCode);
        }

        [Fact]
        public async Task Ping_RedisNeverAnswers_ReturnsFalse() =>
            Assert.False(await Store(Hanging().Object).PingAsync(CancellationToken.None).WaitAsync(Guard));

        [Fact]
        public async Task Ping_RedisFails_ReturnsFalse() =>
            Assert.False(await Store(Failing(new InvalidOperationException(Secret)).Object).PingAsync(CancellationToken.None).WaitAsync(Guard));

        [Fact]
        public async Task TheTokenOfTheCallReachesTheCache()
        {
            using CancellationTokenSource caller = new();
            CancellationToken seen = default;
            Mock<IDistributedCache> cache = new();
            _ = cache.Setup(c => c.GetAsync("u1", It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((_, ct) => seen = ct).ReturnsAsync((byte[]?)null);
            _ = await Store(cache.Object).GetCartAsync("u1", caller.Token);
            Assert.True(seen.CanBeCanceled);
        }

        // ── Quantity limit of one line ────────────────────────────────────────────

        [Fact]
        public async Task AddItem_LineWouldExceedTheLimit_IsInvalidArgumentAndNothingIsWritten()
        {
            Cart existing = new() { UserId = "u1" };
            existing.Items.Add(new CartItem { ProductId = "p1", Quantity = CartValidation.MaxQuantityPerLine });
            Mock<IDistributedCache> cache = new();
            _ = cache.Setup(c => c.GetAsync("u1", It.IsAny<CancellationToken>())).ReturnsAsync(existing.ToByteArray());

            RpcException ex = await Fails(s => s.AddItemAsync("u1", "p1", 1, CancellationToken.None), Store(cache.Object));

            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
            cache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task AddItem_SumOfTwoInts_DoesNotWrapAround()
        {
            Cart existing = new() { UserId = "u1" };
            existing.Items.Add(new CartItem { ProductId = "p1", Quantity = int.MaxValue });
            Mock<IDistributedCache> cache = new();
            _ = cache.Setup(c => c.GetAsync("u1", It.IsAny<CancellationToken>())).ReturnsAsync(existing.ToByteArray());

            RpcException ex = await Fails(s => s.AddItemAsync("u1", "p1", 100, CancellationToken.None), Store(cache.Object));
            Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        }

        // ── Options ───────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null, 2000)]
        [InlineData("", 2000)]
        [InlineData("abc", 2000)]
        [InlineData("-5", 2000)]
        [InlineData("0", 2000)]
        [InlineData("49", 2000)]
        [InlineData("30001", 2000)]
        [InlineData("99999999999", 2000)]
        [InlineData("50", 50)]
        [InlineData(" 750 ", 750)]
        [InlineData("30000", 30000)]
        public void RedisTimeout_FromEnvironment_FallsBackToTheDefault(string? value, int expectedMs)
        {
            CartStoreOptions o = CartStoreOptions.FromEnvironment(name => name == "REDIS_TIMEOUT_MS" ? value : null);
            Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), o.RedisTimeout);
        }

        // ── Client span ───────────────────────────────────────────────────────────

        [Fact]
        public async Task RedisFailure_IsRecordedOnAClientSpanWithErrorStatus()
        {
            List<Activity> done = [];
            using ActivityListener listener = new()
            {
                ShouldListenTo = s => s.Name is RedisCartStore.ActivitySourceName or "cartservice.tests",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => { lock (done) { done.Add(a); } },
            };
            ActivitySource.AddActivityListener(listener);

            // other tests run in parallel and create spans of the same source: only the children of this test's own parent count
            using ActivitySource testSource = new("cartservice.tests");
            using Activity parent = testSource.StartActivity("test")!;
            _ = await Fails(s => s.GetCartAsync("span-user", CancellationToken.None), Store(Failing(new InvalidOperationException(Secret)).Object));

            Activity span = Assert.Single(done, a => a.OperationName == "redis GetCart" && a.ParentSpanId == parent.SpanId);
            Assert.Equal(ActivityKind.Client, span.Kind);
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            Assert.Equal("redis", span.GetTagItem("db.system.name"));
            Assert.Contains(span.Events, e => e.Name == "exception");
            Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains("span-user", StringComparison.Ordinal) == true); // no ids on spans
        }
    }
}
