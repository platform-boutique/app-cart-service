# API

Package `hipstershop`, service `CartService` ([`src/protos/cart.proto`](https://github.com/platform-boutique/app-cart-service/blob/main/src/protos/cart.proto)).

| Method | Request | Response | Behaviour |
|---|---|---|---|
| `AddItem` | `AddItemRequest{ user_id, CartItem{ product_id, quantity } }` | `Empty` | Creates the cart if absent; otherwise adds `quantity` to an existing line or appends a new one |
| `GetCart` | `GetCartRequest{ user_id }` | `Cart{ user_id, repeated CartItem items }` | Returns the cart, or an empty cart if none exists |
| `EmptyCart` | `EmptyCartRequest{ user_id }` | `Empty` | Overwrites the cart with an empty one |

Input rules (violations are `INVALID_ARGUMENT`; the message names the field and never echoes the value): `user_id` and `item.product_id` are non-empty (not only whitespace) and at most 128 characters; `item` is present; `item.quantity` is 1 to 100 per `AddItem` call; the total of one product in a cart is at most 1000 (`AddItem` beyond that is `INVALID_ARGUMENT` and writes nothing). The `GetCart`/`EmptyCart` calls need a non-empty `user_id` too.

Errors from the storage layer carry a generic text, never the Redis exception (the detail is in the server log and on the span):

| Situation | gRPC status | Text |
|---|---|---|
| Redis unreachable or failing | `UNAVAILABLE` | `cart storage unavailable` |
| Redis did not answer within `REDIS_TIMEOUT_MS` | `DEADLINE_EXCEEDED` | `cart storage did not answer in time` |
| The caller cancelled or its own deadline passed | `CANCELLED` | `request cancelled` |
| Stored value cannot be parsed | `INTERNAL` | `internal error` |

(Before this change every Redis failure was `FAILED_PRECONDITION` with the exception text appended.)

### Storage model

- Redis key = `user_id` (raw, no prefix — the `IDistributedCache` instance has no `InstanceName`).
- Value = protobuf-serialised `Cart` (binary).
- **No TTL.** Carts live until Redis is wiped. `EmptyCart` writes an empty cart rather than deleting the key.
- `AddItem` is read-modify-write and not atomic: two concurrent adds for the same user can lose an update.
