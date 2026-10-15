# -*- coding: utf-8 -*-
"""Kurulu DLL icinden cikarilan bundle'in konteynerini yazdirir.
PlayerAssetNpc.MODEL_PATH ile karsilastirmak icin."""
import sys
import UnityPy

path = sys.argv[1]
env = UnityPy.load(path)
want = "assets/mpnpc/mpnpc.prefab"
keys = sorted(env.container.keys())
print("konteyner girdileri: %d" % len(keys))
for k in keys:
    v = env.container[k]
    print("  %-46s %s" % (k, v.type.name if v else "?"))
print()
print("PlayerAssetNpc.MODEL_PATH = '%s'" % want)
print("konteynerde var mi        : %s" % (want in env.container))