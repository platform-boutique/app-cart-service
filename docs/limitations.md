# Known limitations

- Carts never expire (no TTL) and `AddItem` is not atomic.
- Health is Redis-coupled; see probe guidance above.
- Logs are one JSON object per line on stdout through `JsonLogFormatter` (the one log shape of the standards); framework (`Microsoft.*`) categories stay at `warning` or above.
