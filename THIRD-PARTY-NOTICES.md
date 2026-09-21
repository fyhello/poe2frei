# Third-Party Notices

## POE2Radar

FreiAtlas references and may adapt portions of POE2Radar v0.17.1, commit
`2615bec65caf62589c4e80dca9b6f75425c9c014`, under the MIT License.

Reference or planned adaptation scope:

- `Poe2Offsets.cs`
- `Poe2Live.cs`
- `EntityNameResolver.cs`
- `CustomLandmarks.json`
- `entity_names.json`

The complete license text is preserved in `licenses/POE2Radar-MIT.txt`.

## Path of Exile 2 Rune Icons

FreiAtlas embeds 34 Path of Exile 2 runeshape icons for the Expedition recipe
panel. The game artwork was retrieved from the following Poe2DB page and CDN,
then converted from WebP to PNG without redrawing:

- `https://poe2db.tw/tw/Runeshape_Combinations`
- `https://cdn.poe2db.tw/`

The source page exposes 33 distinct image files for rune indices 0 through 32.
Rune index 33 (`Bait`) uses the same `RemnantRareRunePower.webp` artwork as
index 32 (`Power`), so the two embedded PNG files are intentionally identical.

Path of Exile 2 and its game artwork are property of Grinding Gear Games.
Poe2DB is recorded as the retrieval source; this notice does not imply that
Poe2DB granted a separate open-source license for the game artwork.

## Path of Exile 2 Reward Icons

FreiAtlas embeds Path of Exile 2 item and item-category icons for the
Expedition recipe panel. The game artwork was retrieved from the following
Poe2DB page, item hover responses, category pages, and CDN, then centered in
48-by-48 transparent PNG files without redrawing:

- `https://poe2db.tw/us/Runeshape_Combinations`
- `https://poe2db.tw/us/hover`
- `https://cdn.poe2db.tw/`

The embedded manifest maps 231 exact reward item identifiers and 29
descriptive reward categories to local resources. The update script preserves
a separate, reviewable source manifest under `scripts/`.

Path of Exile 2 and its game artwork are property of Grinding Gear Games.
Poe2DB is recorded as the retrieval source; this notice does not imply that
Poe2DB granted a separate open-source license for the game artwork.
