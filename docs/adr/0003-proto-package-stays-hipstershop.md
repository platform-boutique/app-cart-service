# 3. The proto package stays `hipstershop`

Status: accepted exception (owner informed 2026-11-22; application standards section 3, rule CODE-PROTO).

## Context

`cart.proto` declares `package hipstershop`, the package of the shop's original services; the callers `frontend` and `checkout-service` keep their own copies of the file with the same package, so every call uses the gRPC method names `/hipstershop.CartService/...`. The package name is part of that name on the wire.

## Decision

Keep `package hipstershop`. Contract changes stay additive (new fields or RPCs, no renumbering, no removals), server first. Renaming the package would change the name of every method, so old clients and a new server would no longer find each other (`UNIMPLEMENTED`), and the `buf breaking` check of the pipeline (rule TEST-03) would report it as a breaking change.

## Consequences

- The package does not follow the `hipstershop.<name>` pattern of newer services. This is a recorded exception, not an oversight; metric routes and the health service name carry it (`/hipstershop.CartService/AddItem`).
- A rename would need cart-service, the frontend and checkout-service to change in one coordinated release with a deprecation window.
