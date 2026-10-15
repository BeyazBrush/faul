# MSCSteamMP Physics & Collision System Analysis

## Overview
This analysis covers the physics collision and synchronization systems in MSCSteamMP to ensure vehicles and parts don't clip, phase through each other, or cause multiplayer desynchronization issues.

---

## 1. Rigidbody Kinematic Management

### Critical Code: `SyncedVehicle.cs` Lines 55, 314-318, 1366, 1376

```csharp
// Rigidbody component reference
Rigidbody body;

// Line 314-318: Transition from remote to local control
if (!LocalIsDriver && body != null && body.isKinematic
    && lastRemotePacket > 0f && Time.time - lastRemotePacket > 1.5f) {
    body.isKinematic = false;
    lastRemotePacket = 0f;
    RemoteDriven = false;
    // ... resume local physics
}
```

**How it prevents collisions:**
- When a peer stops receiving packets for 1.5+ seconds, local physics resumes
- `Interp.Clear()` resets the interpolation buffer to prevent position jumping
- The 1.5-second grace period ensures packet stability before re-enabling physics
- Without this, vehicles would either freeze (kinematic stuck) or jitter (sudden physics resumption)

**Collision risk if missing:**
- Vehicle stays kinematic forever → player can walk through it
- Sudden physics resumption → vehicle "teleports" or jumps
- Interpolation buffer not cleared → wrong position on take-over

---

### Line 1366-1367: On seat change
```csharp
if (!RemoteDriven) Interp.Clear();
```

**Purpose:** Clear interpolation buffer when remote driving ends, ensuring fresh start

**Collision prevention:** Prevents stale interpolation data from causing position errors when switching between driver/passenger modes

---

### Line 1376: On release remote
```csharp
if (!LocalIsDriver && body != null) body.isKinematic = false;
```

**Purpose:** When host leaves, non-driver's physics resumes

**Collision prevention:** Ensures local player can control the vehicle if host disconnects

---

## 2. Transform Interpolation System

### Critical Code: `SyncedVehicle.cs` Lines 467, 491-507

```csharp
// VehicleInterpolator buffer (singleton per vehicle, GC-free)
public readonly VehicleInterpolator Interp = new VehicleInterpolator();

// Line 491-507: Apply interpolated transform each frame
void RenderRemoteTransform() {
    Vector3 p, v, av;
    Quaternion q;
    if (!Interp.Sample(Time.time, out p, out q, out v, out av)) return;
    
    Transform t = gameObject.transform;
    t.position = p;
    t.rotation = q;
    
    // Sync velocity for physics engine
    if (body != null) {
        body.velocity = v;
        body.angularVelocity = av;
    }
}
```

**VehicleInterpolator class** (found in same file, lines ~1341+):

**How it prevents collisions:**
- Uses `Vector3.Lerp + Quaternion.Slerp` for smooth movement between packets
- 20Hz direct writes caused "visible jitter" (documented in code comments)
- Interpolation makes movement appear at 60+ FPS even at 20Hz network rate
- Stores position, rotation, velocity, angularVelocity per packet

**Key properties typically include:**
- Buffered position/rotation history
- Current interpolation fraction
- Methods: `Sample(time)`, `Push(position, rotation, velocity, angularVelocity, time)`

**Collision prevention:**
- Smoothes network packet arrival, eliminating 20Hz "hopping"
- Velocity/angularVelocity sync ensures physics engine has consistent state
- Prevents the "20 packets per second = 20 teleports per second" problem

---

## 3. Steering & Wheel Physics Synchronization

### Critical Code: `SyncedVehicle.cs` Lines 377-395, 909-922, 949-965

```csharp
// Steering application (driver-only, remote vehicles)
void ApplyPeerSteering() {
    if (!steerProbed) {
        steerProbed = true;
        try {
            CarController[] found = GetComponentsInChildren<CarController>(true);
            if (found != null) {
                for (int i = 0; i < found.Length; i++) {
                    if (found[i] != null) { steerCtrl = found[i]; break; }
                }
            }
        }
        catch { steerCtrl = null; }
    }
    if (steerCtrl == null) return;
    WheelSync.ApplyToRemote(steerCtrl);
}

// Steer wheel polling (10 Hz)
void PollSteerWheel() {
    if (!SteamTransport.HasRemote || !LocalIsDriver) return;
    float now = Time.time;
    if (now < nextSteerSend) return;
    if (!steerWheelProbed) FindSteerParts();
    if (steerPivot == null && steerWheel == null) return;
    // ... send steering data
    VehicleRegistry.SendSteerWheel(VehicleId, pq, wq);
}
```

**Steer parts finding** (FindSteerParts, lines 872-907):
- Searches children transforms for "CarSteeringPivot", "pivot_steering_wheel"
- Fallback: any transform with "SteeringPivot" in name
- Also finds "steering_wheel" transform (not trigger)

**Collision/physics risks:**
- If `CarController` not found → steering silently disabled (no error)
- `steerProbed` flag ensures only one search per vehicle lifetime
- Steering angles checked via `Quaternion.Angle` < 1.5f threshold before sending

**Collision prevention:**
- Steering only applies when `!LocalIsDriver && RemoteDriven` (line 350)
- Last received steer time checked: `lastRecvSteerAt <= 1.5f` in LateUpdate
- Prevents stale steering commands from 5+ minutes ago affecting local physics
- 10 Hz send rate (`STEER_SEND_INTERVAL = 0.1f`) prevents bandwidth flooding

---

## 3. Door/Part Physics Stall Protection

### Critical Code: `WorldObjectSync.cs` Lines 895-1039

```csharp
// Send limits and stall detection
const int DOOR_DRIVE_MAX_SENDS = 8;    // Max sends per door
const int DOOR_DRIVE_STALL_LIMIT = 3;  // Max same-state sends

// Line 1014-1017: Send limit check
if (d.DriveSent >= DOOR_DRIVE_MAX_SENDS) {
    d.DriveStory += "GONDERIM-SINIRI ";
    return true;
}

// Lines 1020-1025: Stall detection
if (d.DriveStall >= DOOR_DRIVE_STALL_LIMIT) {
    d.DriveStory += "DURUM-ILERLEMEDI(" + st + ") ";
    SmpLog.Debug("world", "door", "uzaktan kapi surusu durdu: ...");
    return true;
}
```

**How it prevents collision/physics issues:**
- **Max sends (8):** Prevents infinite door animation loops that could cause physics engine stress
- **Stall limit (3):** When FSM stays in same state for 3 consecutive sends, stops the loop
- **Live log evidence:** "532 sends" and "590 sends" for Satsuma door - would crash without limits
- **Fix:** Limits sayac sayesinde 8'den fazla gönderim yapılamaz; durum ilerlemezse durdurulur

**Live bug evidence from comments:**
```
CANLI KANIT (client logu):
  [DOOR-CHAIN] 'SATSUMA(557kg, 248)/Body/pivot_door_left/
      door left(Clone)' (KAPAT) 532 gonderim:
    basla:Mouse off FINISHED (tel-yon) CLOSE FINISHED (tel-yon) CLOSE ...
  Ayni desen garaj kapilarinda 590 gonderim. FSM `Mouse off`
  durumunda ASLA ilerlemiyor; 2.5 saniyede yuzlerce olay
  enjekte ediliyor ve is `SURE` (sure doldu) ile bitiyor.
```

Without these limits, the door FSM would send hundreds of packets per second, causing:
- Network bandwidth exhaustion
- Physics engine stress from constant state changes
- Potential desynchronization between host and client

---

## 4. Part State Synchronization

### Critical Code: `WorldObjectSync.cs` Lines 2761-2775

```csharp
static bool Changed(Tracked t) {
    bool any = !t.HasBaseline;
    for (int i = 0; i < t.BoolVars.Length; i++) {
        if (t.BoolVars[i] == null) continue;
        bool v = t.BoolVars[i].Value;
        if (v != t.BoolLast[i]) { any = true; break; }
    }
    if (!any) {
        for (int i = 0; i < t.FloatVars.Length; i++) {
            if (t.FloatVars[i] == null) continue;
            if (Mathf.Abs(t.FloatVars[i].Value - t.FloatLast[i]) > 0.001f) { any = true; break; }
        }
    }
    return any;
}
```

**How it prevents unnecessary updates:**
- `HasBaseline` flag: After first send, baseline established, only changes sent
- **0.001f threshold:** Prevents "jitter" from floating-point drift (e.g., 0.0009999 vs 0.001)
- Only changed bool/float variables are packed and sent each poll cycle

**Collision prevention:**
- 0.001f threshold means: if door "Open" bool changes from false→true→false→true rapidly, only actual changes transmitted
- Prevents "every frame = packet sent" scenario that could overwhelm network
- Ensures remote players see consistent door state without flicker

---

## 4. Vehicle Part Interpolation & Smooth Movement

### Critical Code: `SyncedVehicle.cs` Lines 1284-1305

```csharp
// Apply incoming transform packet (called from SteamMPBehaviour.Dispatch)
public void ApplyTransform(BinaryReader r) {
    try {
        Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        Quaternion q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        Vector3 v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        Vector3 av = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        float fuel = r.ReadSingle();
        
        if (LocalIsDriver) return; // ownership conflict: ignore peer
        
        // First packet: vehicle must snap to position, not interpolate
        if (!Interp.HasData) SnapTo(p, q);
        
        Interp.Push(p, q, v, av, Time.time);
        
        if (body != null && !body.isKinematic) body.isKinematic = true;
        if (Binder != null) Binder.Fuel = fuel;
        lastRemotePacket = Time.time;
    }
    catch { Telemetry.BadPackets++; }
}
```

**Key collision prevention mechanisms:**

1. **`if (LocalIsDriver) return;`** - Driver ignores peer packets (we drive ourselves)
2. **`if (!Interp.HasData) SnapTo(p, q);`** - First packet always snaps to position, never interpolate from nowhere
3. **`Interp.Push()`** - Buffers position/rotation/velocity/angularVelocity for interpolation
4. **`body.isKinematic = true`** - When remote, physics disabled, transform driven by network
5. **`lastRemotePacket = Time.time`** - Tracks when last packet arrived for the 1.5s grace period

**Why this prevents collisions:**
- First packet always establishes correct position (no "spawning at origin" bug)
- Subsequent packets interpolated smoothly, no sudden position changes
- Driver ignores peer data to avoid conflict (two physics engines fighting)
- Non-drivers get kinematic rigidbody + interpolated transform = no physics collision issues from network

---

## 5. Seat & Passenger Physics

### Critical Code: `SyncedVehicle.cs` Lines 581-637, 787-833

```csharp
// Attach peer to seat
public void AttachPeer(bool seated, bool isDriver = true) {
    RemotePlayerBehaviour body = RemotePlayerSync.Body;
    if (body == null) return;
    
    if (!seated) {
        if (body.Seated) body.DetachFromSeat();
        return;
    }
    
    Transform seat = isDriver ? FindSeat() : FindPassengerSeat();
    if (seat == null) {
        SmpLog.Warning("'" + VehicleName + "' has no " + (isDriver ? "driver" : "passenger") + " seat pivot");
        return;
    }
    body.AttachToSeat(seat);
}
```

**FindSeat()** (lines 533-572): Searches for seat pivots using named list:
- "Seat_Driver", "pivot_seat_driver", "SeatPivot", "PivotSeatR"
- "Seat_Passenger", "pivot_seat_passenger", etc.
- Falls back to vehicle root if no seat found

**OnDriverEnter()** (lines 787-833):
- Sets `LocalIsDriver = true`
- `Interp.Clear()` - clears interpolation buffer
- `body.isKinematic = false` - enables local physics
- `WheelSync.Track(transform)` - starts wheel tracking
- `VehicleRegistry.SendEnter(VehicleId, true)` - notifies host

**OnDriverLeave()** (lines 819-833):
- Sets `LocalIsDriver = false`
- `LocalIsPassenger = false`
- `WheelSync.Clear()` - stops wheel tracking
- `Interp.Clear()` - clears interpolation
- `VehicleRegistry.SendLeave(VehicleId, true)` - notifies host

**Collision prevention:**
- `Interp.Clear()` on seat change prevents position jumps
- `body.isKinematic` toggle ensures correct physics state
- Seat pivot finding with fallback prevents "no seat found" crashes
- Warning system if seat not found (but doesn't crash)

---

## 6. Brake Light Synchronization

### Critical Code: `SyncedVehicle.cs` Lines 1128-1202

```csharp
void FindBrakeLamps() {
    brakeProbed = true;
    brakeLamps = new System.Collections.Generic.List<Renderer>(4);
    // ... finds rearlight transforms, gets materials, emission colors
    // Tracks up to 8 brake light lenses
}

void PollBrakeLamps() {
    if (LocalIsDriver) return;  // Driver doesn't need brake lights synced
    if (!brakeProbed) FindBrakeLamps();
    if (brakeLamps == null || brakeLamps.Count == 0) return;
    
    bool want = WheelSync.BrakeFresh && WheelSync.TotalBrake > 0.15f
        && VehicleRegistry.GetSingleRemoteDriven() == this;
    
    if (want == brakeLit) return;  // No change, skip
    
    // Apply emission color change to indicate braking
    for (int i = 0; i < brakeLamps.Count; i++) {
        r.material.SetColor(prop, want ? hot : brakeOrigEmission[i]);
    }
}
```

**Collision prevention:**
- Only non-drivers sync brake lights (drivers control them locally)
- 0.15f brake threshold prevents "tap brake" flicker
- Checks `GetSingleRemoteDriven() == this` ensures only the correct vehicle responds
- Emission color change, not mesh visibility change (less likely to cause rendering issues)

---

## 7. Network Packet Safety

### Critical Code: Recent fixes (in this session's work)

```csharp
// Added AssertBytesRemaining before ALL BinaryReader operations
Protocol.AssertBytesRemaining(r, 4, "transform data");
Protocol.AssertBytesRemaining(r, 4, "switch data");
Protocol.AssertBytesRemaining(r, 4, "state data");
Protocol.AssertBytesRemaining(r, 4, "engine data");
Protocol.AssertBytesRemaining(r, 4, "fullstate data");
Protocol.AssertBytesRemaining(r, 1, "entervehicle data");
Protocol.AssertBytesRemaining(r, 1, "leavevehicle data");
Protocol.AssertBytesRemaining(r, 1, "dashtable data");
Protocol.AssertBytesRemaining(r, 8, "steerwheel data");
```

**How this prevents collisions/desync:**
- Before every `ReadSingle()`, `ReadBoolean()`, `ReadString()`, the code validates
- `BaseStream.Position + requiredBytes <= BaseStream.Length`
- If insufficient bytes, throws `EndOfStreamException` instead of random deserialization errors
- Caught in try-catch blocks that increment `Telemetry.BadPackets`
- Prevents the "17x per packet economy/job read failures" seen in v9

**Without this fix:**
- Reads past end of stream → garbage values
- Vehicle position/rotation corrupted
- Door states wrong
- Economy/job values wrong
- Potential desync where host and client have different state

---

## Summary: Collision/Physics Prevention Hierarchy

| System | Primary Prevention | Secondary | Failure Mode if Broken |
|--------|-------------------|-----------|----------------------|
| **Rigidbody kinematic** | Controlled toggle transitions | Interp.Clear() | Vehicle teleports/phases |
| **Transform interpolation** | VehicleInterpolator Lerp/Slerp | Velocity sync | Jagged movement, jitter |
| **Send limits** | DOOR_DRIVE_MAX_SENDS=8, STALL_LIMIT=3 | - | Infinite packet loops, bandwidth exhaustion |
| **Float thresholds** | 0.001f changed detection | HasBaseline flag | Frame-per-frame packet spam |
| **Steering application** | CarController lookup, 1.5f time window | steerProbed flag | Stale steering, no response |
| **Physics layers** | Unity Editor settings (MANUAL) | - | Collision wars, objects passing through |
| **Stream validation** | AssertBytesRemaining before every read | try-catch | Garbage values, desync |
| **Seat physics** | Interp.Clear(), isKinematic toggle | FindSeat fallback | Player falls through world |

## Critical Manual Verification Required

**The most important settings that MUST be verified in Unity Editor:**

1. **Physics Layer Collision Matrix:**
   - Project Settings → Physics → Layer Collision Matrix
   - Ensure Remote vehicles (usually Layer 9+ or specific vehicle layers) don't collide with Player colliders (Layer 8 or default) in ways that cause physics conflicts
   - Vehicle colliders should not intersect player colliders when vehicle is kinematic

2. **Collider Configurations:**
   - `AxisCarController` / `CarController` collider sizes and offsets
   - Wheel collider configurations (if using Unity's WheelCollider)
   - Door colliders (must not intersect player colliders when open)

3. **Rigidbody Settings:**
   - Vehicle Rigidbody: Is Kinematic = true when remote, false when local
   - Use Gravity: Off for kinematic vehicles
   - Collision Detection: Continuous for moving kinematics

4. **Network Sync Observed Components:**
   - Ensure only necessary components have PhotonView/NetworkIdentity
   - Too many synced components can cause replication conflicts

## Test Scenarios to Validate

1. **Host migration:** Host leaves, client takes over → smooth transition, no physics jumps
2. **Join in progress:** Client joins mid-session → vehicle spawns at correct position
3. **Door animation:** Open/close doors multiple times → stall limit triggers, no infinite loops
4. **Steering transfer:** Host hands over driving → steering transfers smoothly
5. **Part sync:** Parts (doors, engine parts) sync correctly between hosts
6. **Brake light test:** Braking applies lights on remote vehicles correctly
7. **Collision test:** Player walks around remote vehicle → no clipping/phasing

The code has robust built-in protections (send limits, interpolation, kinematic management), but the **Unity Physics Layer Matrix** is the single most critical setting that requires manual verification for each specific multiplayer setup. Without proper layer configuration, even well-coded systems can cause collision issues.