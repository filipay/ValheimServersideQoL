Server-only cartography-table sharing for unmodified clients, including PS5. The current changes compile against the matching Valheim 1.0.20 references; live multiplayer/PS5 behavior has not yet been verified.

### Travel, publication and reading

- The server quietly samples circular areas around each character's observed outdoor positions, using validated runtime minimap settings. This approximates observed travel: it does not copy exact personal-map fog, reconstruct historical travel, reveal whole islands, or interpolate corridors between teleports. Sampling pauses if runtime settings are unavailable or invalid.
- Travel stays pending for that character. Only that same character passing within `MapTableRange` of a table with vanilla-equivalent ward write access publishes their pending travel and a complete snapshot of their generated discoveries. Incomplete discovery collection retries without consuming that character's pending data. Enabled overlapping wards permit writing when any covering ward permits the character; inactive wards do not restrict it. This does not add ward-based read privacy.
- Published exploration and generated portal, ship, ore-deposit and dungeon pins are retained world-wide. An eligible approach lazily refreshes that nearby table with the published union, even when the visitor has nothing new. Distant tables are not pushed updates. Existing table exploration can seed the union during an eligible interaction.
- Players still use the normal manual **Read** action to import table data. There is no direct broadcast of shared exploration to PS5/player maps. Existing ore/dungeon `DiscoveringPlayer` delivery options are unchanged.

### Pins and builder names

- Ship labels use `<ship> (<builder>)`, identifying the original creator character, not the current sailor or network owner. Last-known names survive normal saves/restarts and work while the builder is offline. Unknown builders keep the ordinary ship label; newly learned or renamed characters update labels on subsequent eligible reconciliation. Pin ownership encoding is unchanged.
- Manual player pins remain local to their own tables and are preserved, even with the legacy `DiscardPlayerPins=true` setting. They are not collected into a global pin registry. Normal manual table writes remain available.
- Legacy generated pins remain local until their owner supplies a complete authoritative discovery snapshot; old table blobs are not blindly trusted as global generated discoveries. Destroyed-source removal and later owner snapshots replace retained generated entries.

### Persistence and limitations

- Per-character pending travel, published data and cached names use persistent server state retained by normal world saves. Logging off does not publish pending travel; a normal save/restart retains it separately until that same character makes an eligible pass-by. Disconnect or restart alone never unions it into world-shared data. A crash can lose changes since the last world save; this is not immediate crash-proof disk persistence.
- If a vanilla writer replaces a table with stale data, retained published data is reapplied on a later eligible nearby reconciliation. Manual reads/writes are not atomic with server reconciliation, and manual pins already lost in a vanilla write race cannot be recovered from the global state.
- Vanilla imported-pin deduplication within about one metre can retain previously imported labels on PS5. A server-side rename does not promise immediate replacement of an existing client pin; this needs live PS5 testing.
- Startup/re-enabling can synchronously reseed ward membership from world objects. Persistence still serializes the full pending/offline-character mask snapshot when dirty; large worlds or many retained characters may cause pauses. No live performance guarantee is made.
