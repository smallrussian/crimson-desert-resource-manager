import sys, json, os
from cdbody import load, Body

CAMP = {11: "funds", 12: "food", 13: "timber", 14: "stone", 15: "armaments"}


def extract(path):
    raw, info, types, entries = load(path)
    b = Body(raw, types)
    out = {"file": path, "hmac_ok": info["hmac_ok"]}
    for i, ci, cname, off, size in entries:
        if cname == "InventorySaveData":
            inv, _ = b.block(ci, off, size)
            res = {}
            for e in inv["_inventorylist"]:
                if e["_inventoryKey"] == 1:
                    for it in e.get("_itemList", []):
                        if it.get("_itemKey") in CAMP:
                            res[CAMP[it["_itemKey"]]] = it.get("_stackCount")
            out["reserves"] = res
        elif cname == "FactionSaveData":
            f, _ = b.block(ci, off, size)
            out["lastRewardPushedDay"] = f.get("_lastRewardPushedDay")
            out["storedRewards"] = f.get("_factionStoredOperationRewardList")
            ops = []
            for n in f["_factionNodeElementSaveDataList"]:
                if "_operationKey" in n:
                    ops.append({k: n.get(k) for k in (
                        "_ownerFactionKey", "_operationKey", "_operationStateType", "_operationStartTimeRaw",
                        "_operationLastUpdateTime", "_operationCurrentProgress", "_operationShouldRepeat",
                        "_operatorWorkerCount", "_totalCombatPower")} | {
                        "mercs": [m["_mercenaryNo"] for m in n.get("_operatorMercenaryList", [])]})
            out["operations"] = ops
        elif cname == "TransformSaveData":
            t, _ = b.block(ci, off, size)
            out["transform"] = t
        elif cname == "MercenaryClanSaveData":
            m, _ = b.block(ci, off, size)
            out["mercCounts"] = {k: len(v) for k, v in m.items() if isinstance(v, list)}
    return out


if __name__ == "__main__":
    for p in sys.argv[1:]:
        print(json.dumps(extract(p), default=str))
