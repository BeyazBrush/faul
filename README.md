<<<<<<< HEAD
# faul
kkkkkkk
=======
# MSCSteamMP

Two-player Steam P2P sync for **My Summer Car**, as a plain MSCLoader mod. No injector,
no asset bundle *file*, no dependency on WreckMP or MSCMP. The player model is a Unity
asset bundle embedded in the DLL (`assets\mpplayer`), so the install folder still holds
a single file.

This release rebuilds the Satsuma part and bolt replication against the **current game
assets**, which is where all the design decisions below come from.

## What is synced

| Area | Detail |
|---|---|
| Satsuma parts | `ASSEMBLE` and `REMOVE` on every installable part entry |
| Bolt tightness | Scene-wide, 541 bolts (not just the 137 under the Satsuma), walked to the peer's stage |
| Doors and panels | `OPEN`/`CLOSE` **events** replayed on the peer, 57 panels across three tiers |
| Wiring parts | The 52 `Assemble` FSMs under `SATSUMA/Wiring/Triggers` |
| Bolt tightness | Per-bolt `Stage` and `Tightness` (0-8), plus the part's `Bolted` roll-up |
| Doors, hoods, hatches | `OPEN`/`CLOSE` FSM **events**, not variables. See "Doors are events" below |
| Initial sync | Full part and bolt state, spread over frames on connect |
| Vehicles | Drivable vehicle transforms, switches, starter and dashboard states, fuel |
| Remote player | Peer position/rotation/speed at 20 Hz (unreliable) and flag packets (reliable), drawn with the embedded `MPPlayerModel` body |

## Verified against the shipped game build

Everything below was read out of `mysummercar_Data/level2` (the Satsuma scene) rather
than taken from modding folklore. Counts are for the build on this machine.

**Scene root:** `SATSUMA(557kg, 248)`, a scene root with 40 direct children and 1142
descendants.

**218 installable part entries** (a GameObject carrying at least one tracked FSM):

| FSMs on the object | Objects | Role |
|---|---|---|
| `Assembly` | 102 | install trigger, normally on a `trigger_*` object |
| `Assemble` | 52 | wiring part, start state `Init` |
| `Removal` | 60 | the part itself; also declares `ASSEMBLE` |
| `BoltCheck` | 50 | part-level bolt roll-up |
| `Removal` + `BoltCheck` | 46 | both, i.e. the common chassis part |
| `BoltCheck` only | 4 | bolt group with no removal handler |

`Installed` is declared by 213 of the 218. Of the FSM names, only `Assembly`, `Removal`
and `Assemble` have it consistently; `BoltCheck` almost never does (2 of 50).

**Events.** All 102 `Assembly` FSMs declare `ASSEMBLE`; all 60 `Removal` FSMs declare
both `ASSEMBLE` and `REMOVE`; all 52 `Assemble` FSMs declare `ASSEMBLE`. `HOTINSTALL`
does **not** exist in this build. It appeared in an earlier `Assembly` FSM I had read
from a different scene, so it was dropped: forwarding an event no FSM declares is a
no-op on the peer and only makes the filter look authoritative when it is not.

**Bolts: 137**, all on GameObjects named `BoltPM` carrying the FSM `Screw`, with
variables `Stage` (FsmInt), `Tightness` (FsmFloat) and `ThisBolt` (FsmGameObject).
Their events are `TIGHTEN`, `UNTIGHTEN`, `ASSEMBLE`, `SAVEGAME` and `OFF`. Children are
named `bolt0`…`bolt10`.

**Bolt ownership, and why discovery is two-pass.** 99 of the 137 bolts sit under a real
part; the other 38 hang off a bare container (`bolts_shock_fl`, `Bolts`,
`shock_bottom_mesh`) that has no part FSM of its own. So:

1. Pass 1 creates every part entry.
2. Pass 2 walks each bolt up to the nearest **installable** ancestor, falling back to the
   direct parent, which then becomes a non-installable entry of its own.

Doing this in one pass mis-assigns 82 of the 137 bolts, because
`GetComponentsInChildren` does not guarantee a part is visited before the bolts beneath
it. The fallback parent is what keeps those 38 bolts addressable instead of dropping them.

Result: **234 table entries** (218 installable + 16 bolt containers), 137 bolts across
59 owners.

**Initial sync cost**, with the shipped batch sizes: 218 / 8 per frame = 28 part packets,
137 / 12 per frame = 12 bolt packets, plus one `SyncDone` = **41 packets over 41 frames**.
Bolt containers are excluded from the part batches, which is why 234 entries become 218
records.

## Bolts are 541, not 137

The README used to claim 137 bolts and treat that as the whole story. It is the
number **under the Satsuma**, and it was the wrong number, because
`Install()` scanned `Root.GetComponentsInChildren<PlayMakerFSM>()` where
`Root = GameObject.Find("SATSUMA(557kg, 248)")`.

Re-counted out of level2:

| owner tree | bolts with `Stage`/`Tightness` |
|---|---|
| `SATSUMA(557kg, 248)` | 137 |
| `CARPARTS` | 217 |
| `STORE` | 169 |
| `REPAIRSHOP` | 8 |
| `YARD` | 7 |
| `ITEMS` | 3 |
| **total** | **541** |

So the parts you buy in the shop and bolt onto the car were never in the table:
their `Stage`/`Tightness` writes had no hook binding, nothing was sent, and
`ApplyBolt` found nothing to apply to. The old comment claiming *"`Screw` FSM'i
bu build'de yok"* was a misdiagnosis — `Screw` is real (592 of them); the
`bolt-seen` counter was only watching the `FsmInt Stage` write while the value
actually lives in `FsmFloat Tightness`.

Discovery now runs over `PlayMakerFSM.FsmList`, so parts and bolts come from one
object set. `Installable` is set **only** for slots under the car, which keeps
the part wire format and `InstallableCount` byte-identical.

## Outbound: value hooks, not event hooks

Turning a bolt spams `TIGHTEN`/`UNTIGHTEN` once per mouse-wheel tick, and
`Screw` tweens `Tightness` with `AddFsmFloat` **every frame** while it runs. The
outbound path therefore keys off the **value writes**, not the events:

1. `FsmInt.set_Value` (Postfix) → `OnStageWrite` → `LookupBoltStage` → `Dirty`
2. `FsmFloat.set_Value` (Postfix) → `OnTightnessWrite` → `LookupBoltTightness` → `Dirty`
3. `Collect` at 20 Hz applies the send gates and batches up to 32 bolts per packet

Postfix, not Prefix: in a Prefix `variable.Value` still returns the **old**
stage, which is why the old build logged no `[ACT] BOLT` at all.

The value write is the better signal. If `TIGHTEN` is handled and `IntClamp`
pins `Stage` at the limit, the event is observed but nothing changed — sending
on the event would ship a packet that says nothing.

### Three send gates, in order

| gate | value | why |
|---|---|---|
| `REMOTE_ECHO_WINDOW` | 0.75 s | the peer's own `Tightness` tween runs for a few frames *after* `SuppressScope` ends; those writes are not ours |
| `BOLT_QUIET_PERIOD` | 0.12 s | do not send while the wheel is still turning — one packet when it stops, not ~10/s of mid-tween values |
| `MIN_SEND_INTERVAL` | 0.1 s | hard floor per bolt, independent of the above |

`BOLT_MAX_QUIET` (1.0 s) is the escape hatch: if an FSM ever writes forever the
quiet gate is overridden rather than delaying a bolt silently, because an
unbounded wait reads as "bolt sync is broken".

### Why there is no `Msg.BoltEvent`

The message you would add already exists, and has the better shape.
`Msg.BoltBatch = 1` carries `(partId, index, tightness, stage, bolted,
boltPath)` — absolute values, reliable, idempotent, batched, with the bolt's
**path** as the identity so a differing save cannot shift the index onto the
wrong bolt. A `BoltEvent` carrying the event name would need the peer to
re-derive the stage from a scroll delta, which is a different (worse) protocol:
not idempotent, and it loses the value when the peer's FSM is mid-transition.

`BoltSync.OnScrewEvent` **is** hooked, but purely as telemetry — `ScrewEvents`,
`ScrewBursts`, `LastBurstEvents`, shown by `smp status`. That is the honest
replacement for the old `probe: bolt-seen 0` counter, which watched only the
`FsmInt` write and so reported `0` while 133 `Tightness` floats existed. It is
routed from the already-patched `Fsm.Event` hooks, so the Harmony patch count
stays at 13.

## Writing a value does not turn a bolt

`ApplyBolt` writes `Stage` and `Tightness`, which fixes the save state and what
`BoltCheck` and `Assembly` read. It does **not** turn the bolt. The mesh is moved
by `HutongGames.PlayMaker.Actions.SetRotation` inside the FSM's own `Screw 2` /
`Unscrew 2` states, which are only entered by `TIGHTEN` / `UNTIGHTEN`.

So the value is written *and* the peer's FSM is walked to the reported stage, one
bounded step per flush:

- `RECONCILE_BUDGET` (8 per flush) and `RECONCILE_MAX_TRIES` (12) bound the work,
  so a `Screw` FSM that refuses to advance cannot hold a queue slot forever.
- It stops the moment `Stage` matches, so a lost or reordered packet cannot
  overshoot. Stage range is 0..8, so it converges in at most 8 steps.
- `REMOTE_ECHO_WINDOW` (0.75 s) suppresses the peer's `Tightness` tween. That
  tween spans several frames and a `SuppressScope` cannot cover it; without the
  window, two players turning the same bolt iterate each other forever.

## Doors are events, not variables

There is no readable "is the door open" value in this build. From the assets:

```
CARPARTS/PartsCar/door left(Clone)      FSM Use (start: Close door)
    events : OPEN, CLOSE, "Mouse off", "Mouse over 1/2", LOOP
    states : Close door -> Open door -> Open door 2
    actions: SetRotation + SetHingeJointLimits
    bool   : Open
```

`Open` is the single exception and writing it does not advance the state machine:
the value lands, the door does not move. So doors travel as
`Msg.DoorEvent` = `(object path, fsm name, event name)` and the peer's own FSM
plays the event, which is also the only way to get the lock check, the hinge
limits and the sound.

**The door name is rarely on the leaf.** It is usually one level up
(`YARD/Building/BATHROOM/DoorBathroom/Pivot/Handle`), so discovery matches the
whole path, not the object name.

### Three tiers, because `OPEN`+`CLOSE` is not a test

142 `Use`/`UseOld` FSMs declare `OPEN` or `CLOSE`, and only 57 are real panels.
A TV switch, a fridge, a microwave, a parking brake, the sun visors, a breakable
window and 17 `PayMoney` NPCs declare the same pair. `PlayAnimation` is no help
either — it is on all of them.

| tier | test | count | false positives |
|---|---|---|---|
| 1 `hinged` | FSM uses `SetHingeJointLimits` | 21 | 0 |
| 2 `fuel-hatch` | a path segment is `fuelfiller` / `body_fuel_hatch` | 3 | 0 |
| 3 `door-name` | a path segment contains `door` | 33 | 0 |

Tier 1 is the load-bearing one: `SetHingeJointLimits` exists only on physical
hinged panels — every car door, boot lid and hood, and nothing else. It is a
public type in `Assembly-CSharp` (the build already references it), so the test
is `is`, with no reflection and no string comparison.

Matching is **per path segment**, never a bare substring: `lid` occurs inside
`MISC/holiday present(Clon2)/Parts/Money`.

`PanelCandidate` is a deliberately broad pre-filter so the expensive
`GetComponents<PlayMakerFSM>()` runs on 111 of the scene's 4,414 FSM-carrying
objects. A false positive there only costs one component read; `PanelTier` is the
authority and its false-positive count is zero.

## Design notes

**Identity is a path captured once.** An entry id is the object's path relative to
`SATSUMA(557kg, 248)`. The Assembly FSM re-parents a part on install (PlayMaker
`SetParent` in the FSM's End state), so re-deriving the path would break the mapping
mid-game. The id is computed at scan time and the object reference is kept alive, so the
table survives any number of install and remove cycles. Both players must load the same
save, otherwise the trees differ and the ids do not match.

**Two signals are captured because neither is complete.** The `Assembly` and `Removal`
FSMs give intent via `SendEvent`, but 3 of the 218 entries have no `Installed` variable
and the `Assemble` FSMs only ever run their `Init` state. So a write to an `Installed`
bool is also captured, and that is the ground truth: it is the save state, and an event
alone would miss the wiring parts entirely.

**Bolt indices come from sorted paths, not traversal order.** `GetComponentsInChildren`
order is only guaranteed per prefab, not across clients, so bolts are sorted by
`(owner id, bolt id)` before indices are handed out.

**Rate limiting is three layers deep.** Turning a bolt writes `Stage` and `Tightness`
dozens of times per second. The hooks only set a dirty flag; `Collect` runs at 20 Hz and
sends the *last* value at most every 100 ms per bolt; and the flush ships one batched
packet. Forty writes collapse into one packet (verified in `tests/CoreProbe.cs`).

**Deltas were rejected.** The remote `Screw` FSM is a state machine over `Stage`, so a
dropped or reordered delta desynchronises it permanently. A repeated absolute value is
idempotent, and part and bolt messages all go out reliable with no acknowledgement: the
next flush re-sends whatever is still wrong.

**The echo guard is re-entrant.** Firing `ASSEMBLE` makes the local FSM write `Installed`,
`Bolted` and `Tightness` in turn. A plain boolean would be cleared by the first nested
write, so `SuppressScope` counts depth and every `Apply*` disposes it in a `finally`.

**The name fallback refuses ambiguity.** If an id does not resolve exactly, the leaf object
name is tried, but only when exactly one *installable* entry carries it. 9 names are
shared by more than one entry under the Satsuma, so a name match is not by itself proof.

**Table mismatch is reported, not silent.** `SyncDone` carries an FNV-1a hash of every
`(entry id, bolt count)`. If the two sides disagree, the receiving player is told which
side has how many parts and bolts, because the real cause is a different save file.

**Variable names are resolved with a fallback.** `FsmVars` tries the known names, then a
case-insensitive substring match, then warns once. A renamed variable returns a default
rather than throwing, so a hardcoded name fails silently and forever otherwise.

## Layout

```
src/MSCSteamMP/
  ModEntry.cs                     MSCLoader entry point, Harmony PatchAll, load order
  SteamMPBehaviour.cs             persistent object: tick loop, packet routing, HUD
  SmpCommand.cs                   'smp' console command
  Net/
    Protocol.cs                   header, message ids, packet limits
    SteamTransport.cs             lobby, handshake, heartbeat, P2P read/write
    NetworkPacketManager.cs       Satsuma serialisation (pure) + transmission
    ConnectionPanel.cs            on-screen status box
    AutoLoad.cs                   brings a client into the world the host is in
    SceneDump.cs, SceneViewer.cs  read-only scene inspector ('smp scene', F1)
    FileLog.cs                    Mods\MSCSteamMP.log
  Player/
    PlayerAsset.cs               embedded asset bundle -> model prefab
    RemotePlayerFactory.cs       instantiate, renderer/collider setup
    RemotePlayerBehaviour.cs     interpolated pose, seat parent
    PlayerAnimManager.cs         clip selection, layer and blend setup
    RemotePlayerSync.cs          20 Hz PlayerPos stream, PlayerInfo once per session
    LocalPlayerState.cs          sit / crouch / dead flags, both directions
    ItemSync.cs                  held item ownership
  Satsuma/
    SatsumaRegistry.cs            part table, flush, initial sync, remote apply
    SatsumaPart.cs                SatsumaPart and SatsumaBolt
    AssemblyPatch.cs              Harmony: SendEvent, FsmBool.Value
    BoltSyncPatch.cs              Harmony: FsmInt.Value, FsmFloat.Value, batching
  Vehicle/                        vehicle sync (unchanged half of the mod)
tests/
  verify.cmd                      builds everything and runs all three probes
  PatchProbe.cs                   Harmony patches resolve and fire
  CoreProbe.cs                    filters, echo guard, batching, inbound apply
  WireProbe.cs                    byte-exact wire format
build.cmd                         compiles the mod against the game's own assemblies
deploy.cmd                        build and install into the game's Mods folder
```

## Build and install

```bat
deploy.cmd
REM or, if the game is not on D::
deploy.cmd "C:\Program Files (x86)\Steam\steamapps\common\My Summer Car"
```

`build.cmd` compiles with the C# compiler directly, against the assemblies in
`mysummercar_Data\Managed`. No MSBuild and no .NET 3.5 targeting pack is needed, and the
output is pinned to **mscorlib 2.0** because the game runs Unity 5.0.0f4 on a Mono 2.5
profile: Mono refuses to load an assembly that references a newer mscorlib. Compiling
against the game's own copies makes that structural instead of a rule to remember.

Two consequences of that pinning show up in the player code: `System.Core.dll` on this
machine is 3.0, so there is no `HashSet<T>` and no LINQ, and `Stream.CopyTo` (.NET 4.0)
is unavailable, so the embedded bundle is copied by hand.

`assets\mpplayer` is required at build time; `build.cmd` refuses to run without it.

## Verify without launching the game

```bat
tests\verify.cmd
```

Three console probes run against the real `PlayMaker.dll` and `0Harmony.dll`:

- **PatchProbe** — that Harmony 1.2 can actually patch `FsmBool.set_Value`,
  `FsmInt.set_Value`, `FsmFloat.set_Value` and `PlayMakerFSM.SendEvent`, that each fires
  once per write, and that reads fire nothing. This is the load-bearing assumption: if
  Harmony 1.2 cannot resolve a property setter, the whole value-capture design fails
  silently in game. The probes are compiled `/optimize-` for this: the desktop CLR JIT
  inlines the four-line setter straight into the caller, so an optimised build reports zero
  hits even though the patch applied. Unity's Mono JIT does not inline, so the mod does not
  have this problem in game.

  PatchProbe carries its own 11 patches, which is **not** the mod's count. The mod
  declares 14 targets, and `ModEntry.ExpectedPatches` counts them from the
  `[HarmonyPatch]` attributes at load time rather than holding a written-down number:
  a hand-written count had drifted to 10, which made the one diagnostic that exists to
  catch a silently missing hook report a healthy install as broken. `smp status` prints
  `patched/declared` and a shortfall is an error on load.
- **CoreProbe** — event filtering, the re-entrant echo guard, bolt batching and rate
  limiting against a manual clock, initial-sync paging, and that unknown ids are counted
  rather than thrown.
- **WireProbe** — the byte-exact wire format: header rejection of foreign packets, record
  round-trips, `Stage` clamping, stream alignment, batch paging, that a bolt container
  never appears in a part batch, and that the player packets keep their ids and payload
  sizes (`PlayerPos` = 30 / 32 bytes, `PlayerInfo` = 31, `PlayerState` = 42).

These do **not** replace a live two-player session.

## Usage

1. Build and install with `deploy.cmd` on both machines.
2. Steam must be running. Both players need the same game version, mod build and save.
3. Host: `smp host`, then invite via Shift+Tab or the in-game panel.
4. Friend: accept the invite, or `smp join <lobbyId>`.
5. On connect the host pushes its part and bolt table, 8 parts and 12 bolts per frame.
6. Both press CONTINUE on the same save. The peer's body appears as soon as the first
   position packet arrives, and `smp player` shows what it is doing.

## The remote player

MSC has no player model, so the peer is drawn with `MPPlayerModel.fbx` — the MSCMP
character, which carries a full skeleton and fifteen clips. It ships as a Unity asset
bundle built with **Unity 5.0.0f4**, the game's own engine version, and is embedded into
the DLL by `/resource`. Nothing is read from disk at runtime, which keeps the install
folder at one file.

The mesh itself is the archived MSCMP model; nothing about the netcode came from there.
What this mod adds on top is the part MSCMP needed an injector for: loading the model
from inside the mod DLL and drawing it against a peer running the same build.

**Why an asset bundle at all.** FBX cannot be imported at runtime, only a compiled
bundle can be read. `assets\mpplayer` is that bundle.

**The API name is a trap.** On this engine `AssetBundle.CreateFromMemory` returns an
`AssetBundleCreateRequest` — an *asynchronous request*, not a bundle. The synchronous
call is `CreateFromMemoryImmediate`. Verified by reflecting over the game's own
`UnityEngine.dll`, because getting it wrong compiles as a type error.

### The NPC model (`assets\mpnpc`)

`MPPlayerModel` is kept as a fallback, but the primary body is now the game's **own driver
NPC**, extracted from `mysummercar_Data` and rebuilt as a second bundle (`SteamMP.npc.bundle`).

**Source, measured not guessed**

| | |
|---|---|
| mesh | `level2 › TRAFFIC/VehiclesHighway/VICTRO/LOD/Passengers/Driver/bodymesh` — 1709 verts, 2680 tris, 3 submeshes (3438 / 3708 / 894) |
| skeleton | 20 skin bones, 23 nodes, root `Char/skeleton/pelvis` |
| textures | `char_shirt04` + `char_pants01` + `char_face02` (the driver's outfit) |
| clips | `Idle`, `Walk`, `DriveIdle`, `Drive`, `CarIn`, `CarOut`, `Wave` |

**All NPCs share one mesh.** `bodymesh` is used by every character in the game
(plus `bodymesh_kid`, `_uncle`, `_pig`, `_fighter`). Appearance is not in the mesh: each NPC
carries three materials — shirt, pants, face (`char_shirt01`, `cop_pants`, `char_face02`, …).
That is why the driver NPC needs nothing but three PNGs to look like a driver.

**Unity 5 mesh format, decoded by hand.** `Mesh.m_Vertices` does not exist in Unity 5; the
vertices live in `m_VertexData`. `m_Channels` gives `(stream, offset, dimension, format)` and
the per-stream stride is the sum of its dimensions times four:

```
stream 0 : 40 bytes  = pos(3) + normal(3) + tangent(4)
stream 1 :  8 bytes  = uv(2)
```

Verified: decoding 1709 vertices that way reproduces `Mesh.m_LocalAABB` exactly.

**Bind poses are row-major.** Sixteen floats per bone, translation in elements 12–14, i.e. the
last *column* of Unity's `Matrix4x4`. Proven, not assumed: reading `inv(bindpose)` under the
column-major guess puts every bone at (0,0,0); under the row-major reading the skeleton comes
out anatomically correct — ankles at z=0.048, knees z=0.506, pelvis z=0.867, head z=1.569,
left/right symmetric about y. The mesh AABB is z[0.007 … 1.827], so the figure fits its mesh.

**The mesh lies along Z.** The character is stored with Z as the body axis (feet z=0.007, head
z=1.827) and the arms spread along Y (T-pose). Unity's vertical axis is Y, so the editor
script measures the actual world box and writes a correcting rotation onto the prefab root:

```
dunya kutusu: yukseklik 1.712 m  up=(-0.342, 0.746, 0.571)  fwd=(1.000, 0.000, -0.016)
yon duzeltmesi: (-0.1045, -0.6250, -0.3403, 0.6947) (yaw -83.9, up -> (0,1,0))
```

1.71 m is a person. `Object.Instantiate(prefab, pos, rot)` overwrites the root rotation, so
`RemotePlayerFactory` composes the network rotation with `PlayerAssetNpc.PrefabLocalRotation`
and puts it back.

**Animation paths differ per clip.** In the game the legacy animation is split across four
`Animation` components — `skeleton` (legs), `spine_upper` (torso), `collar_left`,
`collar_right` (arms) — and four clips play simultaneously. `fat_standing` is authored
relative to `skeleton`; `fat_get_car_in` relative to `Char`. `make_char_data.py` detects each
clip's anchor from the rig paths and rewrites every curve onto a single root, then merges the
per-bone clips into one clip per state (Idle = `fat_standing` + both hand-idles). It also
rewrites `.../spine_upper/head` to `.../spine_upper/HeadPivot/head`, which is where the head
bone actually lives.

**Run is not a clip.** The NPC has no run cycle; MSC speeds the walk cycle up instead, so
`Run` resolves to `Walk` at 1.7x. `Drive`/`DriveIdle` are `fat_get_car_idle2`/`_idle1` — the
real seated poses, which is what the driver seat needs.

**Producing it** (one-off; the bundle is committed under `assets\`):

```
pip install UnityPy
python tools\extract_npc_dump.py                 # mesh OBJ, rig JSON, 350 clips, 48 poses
python tools\extract_textures.py                 # 45 character PNGs
python tools\make_char_data.py --npc "TRAFFIC/VehiclesHighway/VICTRO/LOD/Passengers/Driver/bodymesh"
build_bundle.cmd                                # Unity 5.0.0f4 -> assets\mpnpc
```

`build_bundle.cmd` needs a licensed Unity 5.0.0f4. `activate_license.cmd` walks through the
manual activation when the installed licence belongs to another machine. Two Unity 5.0.0f4
quirks are worked around in `unity\MPNPCBuilder\Assets\Editor\BuildNPCBundle.cs`:
`BuildAssetBundle` returns `bool` (not an `AssetBundle`), it refuses without
`BuildAssetBundleOptions.CollectDependencies`, it takes `Object[]` rather than paths, and its
final file move fails in this environment — the finished bundle is then in
`Temp/unitystream.unity3d` and gets copied to the project root.

**Both models ship.** `RemotePlayerFactory` prefers the NPC and falls back to `MPPlayerModel`
if `SteamMP.npc.bundle` is missing, so a build made before `build_bundle.cmd` still runs.

### Appearance picker

Because every NPC shares one mesh and rig, an *appearance* is nothing but three textures —
shirt, pants, face. The game has **70 distinct outfits**, so the main menu gets a picker:
`GÖRÜNÜM` opens it, the NPC turns on a stage on the right, and clicking a row applies it.

```
+--------------------------------------------------+
|  list (70 outfits, scrollable)  |  turning 3D    |
|   1  KiljuBuyer                 |  model         |
|   2  VICTRO                     |  + Idle clip   |
|   ...                           |  shirt04       |
|                                 |  pantolon01    |
|  [12/70]                        |  face02        |
+--------------------------------------------------+
| [◀ GERİ]      tıkla / ↑↓ seç · kaydır · ESC      |
+--------------------------------------------------+
```

- **Catalog is generated, not typed.** `tools\skin_roster.py` walks every level, reads each
  NPC's three materials, resolves the material's real `_MainTex` (the texture name is *not*
  always the material name — `cop_pants` → `cop_pants01`, `curtain4` → `fabric6`) and groups by
  outfit. `tools\gen_skin_catalog.py` turns that into `NpcSkinCatalog.cs`. A typed list would
  silently rot on a game update.
- **47 textures** go into the same bundle, read from the folder rather than a hardcoded list,
  so nothing can be missed quietly. Bundle: 3.7 MB, DLL: 6.5 MB.
- **The preview renders off-screen.** A camera plus two lights at y = −5000 draw into a
  256×384 `RenderTexture` which `GUI.DrawTexture` puts in the panel. The game's cameras never
  see it (outside the far clip plane) and the preview camera has `enabled = false` so it only
  renders when asked.
- **Fitting the model to frame.** `sharedMesh.bounds` is *mesh space*, where the body runs along
  **Z** (feet z = 0.007, head z = 1.827) and Y is arm span (±0.677). Scaling by
  `bounds.max.y - min.y` yields 1.35 m and a gigantic headless model. `SkinPreview.ScaleToFit`
  transforms the eight bounds corners through the renderer and root rotation first, then
  measures the world Y range.
- **Ambient light is global.** `RenderSettings.ambientLight` is saved, set for the preview
  render and restored in the same call, so the game's own lighting is untouched.
- **Materials are cloned.** `PlayerSkin.Apply` builds a fresh `Material` per submesh; writing
  to the bundle's shared asset would put every peer in the same outfit.
- **Unity 5.0 gaps hit here:** no `GameObject.light` / `.camera` shortcuts (use
  `AddComponent`), and no `RenderSettings.ambientMode`.
- **Selection is local** — your client decides what the other player looks like. Syncing it
  needs a protocol change.
- `smp skin`, `smp skin list`, `smp skin 12`, `smp skin Teimo` do the same from the console,
  which is how it gets tested without the menu.

**Which clips play.** Speed and crouch select the base pose: under 0.15 m/s is `Idle`,
under 2.2 m/s is `Walk`, above that `Run`; crouching picks `Crouch`/`CrouchLow` and their
walk variants. `Lean`, `Finger`, `Hitchhike`, `Hit`, `Push` and `Drink` sit on additive
layers above the base pose. `smp player` prints the current clip.

**Interpolation.** Packets arrive at 20 Hz and the screen runs at 60-300 Hz, so the body
approaches the last packet at `Vector3.Lerp`/`Quaternion.Slerp` 10/s. A difference over
8 m is a teleport rather than a glide, so a scene reload does not leave the body sliding
across the map.

**Sitting is a parent, not a pose.** When the peer is in a car, the body is parented to
the seat pivot (`Seat_Driver`, `SeatPivot`, ...), so it turns and bumps with the car
instead of standing beside it.

```
smp player         model, body, animation and seat state, in three lines
smp player kill    remove the body by hand
```

## Diagnostics

```
smp status     net state, counters, part table summary, remote player state
smp player     remote player detail: model, body, animation, seat
smp player kill remove the peer body
smp parts      every entry: installed, bolt count, which FSMs it has
smp bolts      every bolt: stage, tightness, pending
smp sync       host re-sends the full state
smp rescan     rebuild the table after a save change
smp verbose    log every inbound packet
```

The session log is written to `Mods\MSCSteamMP.log`.

## Not implemented

- Animation selection beyond speed, crouch and hit: there is no idle clip choice and no
  aiming or tool poses, so the tightening animation does not play on the remote side
- **Fuel filler caps** (the screw-on cap, not the hatch) do not sync. Measured:
  the 16 `OpenCap` objects have a `Screw` FSM with `TIGHTEN`/`UNTIGHTEN` but
  **no `Stage` and no `Tightness`** — their state is a single `ScrewAmount` float
  with no discrete stage. `BuildBolt` needs a stage, and inventing one would make
  `IsGroupBolted()` permanently false for the owning part, which would then fight
  `Assembly`'s bolt check on the peer. The hatch itself *is* synced (tier 2).
  This needs its own mechanism, not a bolt entry.
- **Damper adjustment knobs** (`SATSUMA(...)/TrailArm/Arm/ShockBottom/...
  DamperAdjustment/BoltPM`, 24 objects) have `TIGHTEN`/`UNTIGHTEN` but represent a
  continuous setting (`AdjustmentStep`, `Min`, `Max`, `Rotation`), not a 0-8
  stage. Correctly excluded for the same reason.
- Nothing yet triggers the `TIGHTEN` **animation** on the remote player; the bolt
  mesh and save state converge, the player's own wrench animation does not
- Seat ownership, so two players driving the same car will fight over it
- More than two players per lobby
- Stock car traffic and AI vehicles are not synchronised
- Nothing sets `LocalPlayerState.Dead`, so `FLAG_DEAD` never goes on the wire and the
  `Hit` clip only plays from a manual call. MSC's own death signal has not been traced yet.

## References

- MSCLoader: <https://mscloader.github.io/>
- PlayMaker 1.x (`HutongGames.PlayMaker`) as shipped with the game
- Harmony 1.2 (`0Harmony.dll`) as shipped with MSCLoader
- Steamworks.NET, bundled in `Assembly-CSharp-firstpass.dll`
- The archived MSCMP mod's PlayMaker knowledge, via the
  [WreckMPVehicleSync](../WreckMPVehicleSync/README.md) port in this workspace
>>>>>>> 8908af6 (Initial commit - Proje dosyaları eklendi)
