# Hide Teammates for CounterStrikeSharp
Hides Teammates on the entire map or distance

## Required packages:
1. [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/)
2. [ClientPrefs_CS2](https://github.com/darkerz7/ClientPrefs_CS2)

## Installation:
1. Install `ClientPrefs_CS2`
2. Compile or copy CS2-HideTeammates to `counterstrikesharp/plugins/CS2-HideTeammates` folger
3. Restart server

## CVARs:
Cvar | Parameter | Description
--- | --- | ---
`css_ht_enabled` | <0/1> | Enable/Disable plugin
`css_ht_maximum` | <1000-8000> | The maximum distance a player can choose
`css_ht_hide_activeweapon` | <0/1> | Disable/enable hiding active weapons [0/1]. Client crash tests needed
`css_ht_hide_weapon` | <0/1> | Disable/enable hiding active weapons [0/1]. Client crash tests needed

## Commands:
Client Command | Description
--- | ---
`css_ht [<-1-CVAR_MAX_Distance>]` | (-1 - Disable, 0 - Enable on the entire map, 1-CVAR_MAX_Distance - Enable ont the Distance)
`css_ht` | Toggle hide teammates on the entire map. Maybe replaced by menu later
`css_htall` | Toggle hide teammates on the entire map
