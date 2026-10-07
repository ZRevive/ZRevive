r"""ZRevive's OWN cosmetic items, as rows we author for a STOCK Z1 Battle Royale client.

WHY THIS EXISTS
---------------
On a stock baseline the CUSTOMIZE grid cannot list ZRevive's skins, and no packet can fix it.
Measured 2026-10-07 on C:\Games\ZRevive-Stock vs the ROTK-derived C:\Games\ZRevive:

    sheet                                 stock   ROTK   ROTK-only
    AcctItemConversions.txt               1,409  1,517         108
    AcctItemConversionGroupMappings.txt   1,406  1,514         108
    AcctItemConversionInputItems.txt      1,525  1,633         108
    ClientItemDefinitions.txt             4,452  4,671         219

The grid's driving table UiDbSkinItemSlotPrototypeAccountItems is NOT built from ItemDefinitions
(proved live in re\stock_grid_probe.py: 218 of its 1,506 rows name account items with no
ItemDefinitions row, and the SendSelf-time rebuild 0x141A66350 leaves it byte-identical while
ItemDefinitions goes 4,237 -> 0). Its source is the AcctItemConversion* datasheets, and datasheet
rows can only be patched into the pack - ItemDefinitionReply cannot create them.

WHAT CAN AND CANNOT BE AUTHORED FROM OUR OWN DATA  (this is the honest scope)
----------------------------------------------------------------------------
The 219-row gap CANNOT be closed from our committed data, and this module does not pretend to:

  * data\kotk\inventory\inventory_data.json knows 108 of the 219 ids - and every one of them is a
    WORLD item (factory EquippableContainer / InfantryEquipment / Weapon). It contains ZERO
    AccountRecipe rows, because re\inventory_data.py generates it for the match inventory and
    filters account-scope items out. The grid lists the ACCOUNT/skin ids, so our committed data
    does not describe a single row the grid needs.
  * No odd/even pair in that 108 is complete (measured: 70 odd, 38 even, 0 pairs), so the
    account -> world conversion rows cannot be reconstructed from it either.
  * ROTK's image sets (14101..14204) do not exist in stock at all (stock's ImageSets.txt stops at
    14018), and its NAME_IDs 800001..800111 are absent from the stock locale.
  * server-cs Features\Lobby\LobbyItemModels.Definitions.g.cs holds definition bodies captured from
    ROTK's running client. Those are ROTK-derived bytes in packet form, not sheet rows; using them
    to author sheet rows would put ROTK's data back into the delta, which is the thing the stock
    baseline exists to avoid.

So the remaining 111 ROTK items stay absent. That is the correct outcome: their ARTWORK is ROTK's
and is not in a stock install either, so defining them would only produce named grid entries that
render as an untextured base garment.

WHAT THIS MODULE DOES AUTHOR
----------------------------
The 14 skins ZRevive actually owns - re\zr_skins.py draws their textures procedurally from our own
logo and re\zr_pack.py ships them in our own pack2. This is exactly the "option A" that
zr_skins.py's own header asks for. Per skin we author one ACCOUNT (skin) row and one WORLD row,
plus the three conversion rows that put it in the grid.

PROVENANCE OF EVERY FIELD
-------------------------
  ID               OURS (an integer). We keep the id the server already references everywhere
                   (data\kotk\appearance\colour_overrides.json, DefaultItems.g.cs,
                   LobbyItemModels.cs), so no server change is needed. ACCOUNT id = WORLD id - 1,
                   which is the structural rule the conversion rows follow (ROTK's own row reads
                   `2401^9001^5201^9002^...`, and the live grid probe found skin id 9204 paired
                   with world 9205). Verified free in stock: the whole 9001..9219 band is absent.
  NAME_ID          OURS - a NEW locale string id in our own 900001.. band (NAME_IDS below), whose
                   TEXT is our own rebranded name from zr_skins.SKINS. re\patch_locale_stock.py
                   adds the record. ROTK's 800001..800111 are deliberately not reused.
  every other
  column (77 of
  them)            THE PLAYER'S OWN STOCK ROW, copied unchanged from the stock host item named in
                   zr_skins.SKINS[4] - a retail KotK cosmetic that already renders a full-sheet
                   MainUV print on the SAME base mesh. So ITEM_CLASS, IMAGE_SET_ID,
                   DESCRIPTION_ID, the equip slot, BULK and every flag are Daybreak values that are
                   known-valid for that garment, read out of the player's install at patch time.
                   Nothing is shipped and nothing is guessed.
  MODEL_NAME,
  TEXTURE_ALIAS    left exactly as the stock host has them - EMPTY. Verified on stock host 3526:
                   the print is not named by the item definition at all, it comes from the server's
                   appearance rows (data\kotk\appearance\colour_overrides.json) plus the shader
                   parameter group. That is why our own DDS can render under a cloned row.
  conversion rows  OURS, by rule: `<id>^<acct>^0^<world>^0^1^0^0^` for the conversion, the stock
                   host's own INPUT_GROUP_ID for the group mapping, and the world id as a grinder
                   input. Ids continue after the sheet's own maximum.

So the only ROTK-derived thing left is the choice of integer id, which our server already uses.
"""

# Our own locale string-id band. ROTK used 800001..800111; we deliberately do not reuse those.
NAME_ID_BASE = 900001


def items(skins):
    """[(acct_id, world_id, name, stock_host, name_id)] for re\\zr_skins.py's SKINS, in its order.

    `skins` is passed in rather than imported so this module stays free of PIL/numpy (zr_skins
    imports both) and can be read by the delta builder.
    """
    out = []
    for i, row in enumerate(skins):
        stem, family, name, world, host = row[0], row[1], row[2], row[3], row[4]
        out.append(dict(acct=world - 1, world=world, name=name, host=host,
                        name_id=NAME_ID_BASE + i, family=family, stem=stem))
    return out


def name_strings(skins):
    """{our locale string id: our name text} for re\\patch_locale_stock.py to ADD."""
    return {it["name_id"]: it["name"] for it in items(skins)}


# A stock donor row per ITEM_CLASS, used only when a stock host has no AccountRecipe partner of its
# own to clone (measured: 1 of the 14 - "Frog Military Backpack", stock host 8152, has no
# AcctItemConversions row rewarding it). The donor is another stock account item of the same class,
# so the cloned row is still entirely Daybreak-valid for that garment type.
ACCOUNT_DONOR_BY_CLASS = {
    "25000": 1827,   # helmet  (host 2063's partner)
    "25002": 3645,   # chest   (host 3526's partner)
    "25003": 6019,   # legs    (host 6018's partner)
    "25004": 3421,   # back    (host 3403's partner)
}
