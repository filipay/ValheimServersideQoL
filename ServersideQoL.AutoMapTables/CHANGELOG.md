### Unreleased
- Added per-character pending observed-travel exploration, published with generated discoveries only at an eligible same-character pass-by. Nearby tables lazily receive the world-shared union; reading remains manual.
- Added original-builder names to ship labels using the persistent last-known character-name cache, with ordinary ship labels for unknown builders and unchanged pin ownership.
- Preserved table-local manual pins regardless of the legacy `DiscardPlayerPins` option. Legacy generated pins stay local until their owner supplies a complete authoritative snapshot.
- Retained pending/published state separately across normal world saves/restarts and reapplied published data after stale table writes on later eligible approaches. Concurrent manual writes are not made lossless; imported PS5 labels may remain unchanged by vanilla position deduplication.

### v2.4.1
- Fixed floating ships near map tables constantly updating the map tables causing performance issues [#369](https://github.com/ArgusMagnus/ValheimServersideQoL/pull/369)

### v2.3.2
- Fixed ArgumentException [#354](https://github.com/ArgusMagnus/ValheimServersideQoL/pull/354)

### v2.2.1
- Fixed NullReferenceException when all wards in range of a map table are disabled [#334](https://github.com/ArgusMagnus/ValheimServersideQoL/pull/334)
- Fixed ship positions not being updated

### v2.2.0
- Fixed ore deposits/dungeons sometimes not being recognized [#239](https://github.com/ArgusMagnus/ValheimServersideQoL/issues/239)

### v2.1.1
- Fixed logic error in ward processing

### v2.1.0
- Compatibility patch

### v2.0.14
- Added config options `DungeonsPinTarget`/`OreDepositsPinTarget` which can be set to add pins directly to player maps in addition to (or instead of) map tables

### v2.0.11
- Support for the new core options `UnifiedConfig` and `ConfigPerWorld`

### v2.0.0
- Initial release
