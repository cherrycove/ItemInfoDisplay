# ItemInfoDisplayForkedCNPlus (PEAK)

Forked from jkqt and chuxiaaaa, maintained for current PEAK versions.

## Features
- Displays consumable status changes (e.g. Hunger, Poison, Curse, Stamina)
- Displays item weight and remaining uses
- Displays cookability, cooked count, and next-cook delta hints
- Displays more readable cooking behavior text in CN localization
- Shows extra action/affliction hints for cooking-related effects

## Cooking Text Update (v1.1.2)
- Improved readability for cooking on-trigger effects and action names
- Improved consistency of `NEXT COOK DELTA` line formatting
- Better handling for explosion/wreck/no-effect cooking outcomes
- Added clearer CN texts for thorn add/remove and affliction time changes

## Config
- Font Size
- Text Outline Width
- Line Spacing
- Size Delta X (horizontal text offset workaround)
- Force Update Time
- Enable Test Mode (cycle items with F1/F2, log with F3)

## Installation
1. Install `BepInEx-BepInExPack_PEAK`.
2. Place the mod DLL into `BepInEx/plugins`.

## Credits
Thanks to:
- jkqt for the original ItemInfoDisplay mod (https://github.com/jkqt/ItemInfoDisplay)
- chuxiaaaa for CN localization and maintenance (https://github.com/chuxiaaaa/ItemInfoDisplay)
