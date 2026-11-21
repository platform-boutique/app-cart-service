# 2. No continuous profiler in cart-service

Status: accepted as a technical exception (OBS-40 of the application standards). The standards also ask for the owner's name and the date in this record; they are not filled in because the owner has not confirmed them yet.

## Context

The platform contract has profiles for every service (Pyroscope, `PYROSCOPE_ADDR`). There is no continuous profiler for .NET that fits that contract.

## Decision

cart-service does not profile. `PYROSCOPE_ADDR` is accepted by the platform contract but not used. Traces, metrics and logs follow the contract unchanged (OTLP, `OTEL_SDK_DISABLED`, resource attributes).

## Consequences

- No profiles for cart-service in Pyroscope.
- This is a recorded exception, not a gap to fix with a workaround.
- To be completed by the owner: name and date of the decision.
