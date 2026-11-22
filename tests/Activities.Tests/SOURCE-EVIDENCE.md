# Gold Saucer reader evidence

Reviewed 2026-10-24. These checks read installed files and synthetic structures; they do not attach to the game, request server state, open a menu, or invoke a model.

## Five playable decks

The [official Triple Triad guide](https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/tripletriad/) specifies five deck slots under Creating a Deck. The user's 0.40.0 test independently confirmed that only five decks are possible. The [pinned GoldSaucerModule definition](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/UI/Misc/GoldSaucerModule.cs) allocates ten entries. Allocation is not playable capacity. The reader checks that allocation for layout compatibility, then copies only indices 0..4, each with five cards. Fixtures deliberately populate indices 5 and 9 with unsupported card IDs and confirm they never reach the observation.

## Displayed race statistics

The [pinned RaceChocoboManager definition](https://github.com/aers/FFXIVClientStructs/blob/dd57986b8287e7c6ed9c27198f8c76cb04a81dfe/FFXIVClientStructs/FFXIV/Client/Game/RaceChocoboManager.cs) identifies the stored bytes as percentages, provides the current/maximum/star getters, and maps color to Stain and available training sessions to a byte. Its states distinguish invalid, requested, and loaded records.

The installed `ffxiv_dx11.exe` reviewed here is 51,976,960 bytes, SHA-256 `46A52AB1DBE80B9A8D6C4D4531BF66693904200C5135FACACD053EF28A5DE14A`. Each pinned getter signature matched once in its executable text section. Offline Iced disassembly covered each complete function:

| Getter | Function virtual address | Last return |
| --- | --- | --- |
| GetAttributesCurrent | 0x140E554C0 | 0x140E55790 |
| GetAttributesMaximum | 0x140E557A0 | 0x140E55A30 |
| GetAttributesStars | 0x140E55A40 | 0x140E55B71 |
| GetRating | 0x140E55C20 | 0x140E55F6B |
| GetPedigreeLevel | 0x140E553D0 | 0x140E55425 |

These functions only read their receiver, use stack locals, write the provided five-value output when applicable, and call the stack-cookie check at 0x141DA00B0. They do not request data or update the manager. The reader invokes them on a detached stack copy after checking loaded state, nonzero rank/name, and percentage bounds; the enclosing capture repeats copies and verifies the menu, player, manager, and public-sheet identities.

The reviewed calculation clamps maximums to 100..500; current values to 0..their maximum; star outputs to 0..3 (displayed as 1..4); pedigree to 1..9 for a nonzero rank; and rating to at most 500. The production reader uses getters rather than reimplementing integer rounding. In particular, the rating rounds the five per-attribute contributions before averaging; a simple aggregate formula can differ by one.

## Parentage order

The pinned `FatherCunning`/`MotherCunning` bitfield labels name the low two bits, but the installed star getter places those bits in its first output, whose typed name is MaximumSpeed. The same ordering appears in the installed menu-update path. Using the generated parent property names would reverse the parentage columns.

The reader instead runs GetAttributesStars on two further detached copies: low five selection bits all zero for father, all one for mother. It preserves the other parameter bits and original live structure. This produces parentage in the same output order as the own-star getter. Synthetic callbacks verify the three selection combinations, displayed +1 conversion, distinct parent columns, and that no callback receives the original structure's address. These genes belong only to the observed current racer; they are never associated with inventory papers.

## Public rating class and color

The installed menu updater calls GetRating at 0x140E5504D and searches ChocoboRaceRank rows 2..14 at 0x140E55060..0x140E55087, testing both inclusive rating bounds. It copies current/max stats, available sessions (manager offset 0x24), rating, class ID, color, and pedigree into the Gold Saucer update payload. This update routine is inspected only, never invoked by the reader.

The installed public sheets have thirteen uniquely named rated classes: Maiden 1..20; R-40 21..40; successive twenty-rating ranges through R-240 221..240; R-300 241..300. Their names resolve through GoldSaucerTextData. Other rows include training/open/challenge categories and unnamed overlapping ranges; they are excluded. Missing, ambiguous, or out-of-range class matches remain explicitly unresolved. A missing Stain name retains its bounded numeric reference with an unresolved label; zero is an absent reference, not a guessed color.

`--public-catalog <sqpack path>` checks these public mappings in English, German, French, and Japanese using synthetic personal state. No primary label-level evidence was established for a separate "racing capacity" value, so the row is named Race chocobo rating; there is no invented additional capacity. Available training sessions are exposed as Training capacity, not as a timer.

## Verification limits

The fixture callbacks are synthetic and do not execute the game's code. Offline disassembly supports the getter behavior and public-data mapping; it does not replace an in-game comparison of the resulting attributes, stars, color, rating/class, and training count on the user's current racer after reload.
