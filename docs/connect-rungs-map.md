# Lodur: connect the rungs (map)

Campaign `lodur` (Nibu's mind). Imagination pass `connect-rungs-map`, session `nibu-self-2026-11-17`,
2026-11-19. Operator order: "A, connect the rungs" (`lodur:ruling:lodur-body-on-ymir`). This file is the prose
map: body facts, the model page, rationale, the proposed target r3 text (committed as docs/lodur-target.md), and the
Ymir cut draft. Committed here from the Imagination scratchpad so it is durable; the bases below are the pins it was probed at.

## Bases

| Repo | Base | Note |
|---|---|---|
| GameCult/Lodur | `18c4ffd0d598c34fc00f96c7f5d23aafdc003706` | main = origin/main |
| GameCult/BadDaySimulator | `9f597647a608a6c6e44473cc9f1399983de14616` | main = origin/main |
| GameCult/Ymir | `7ba31c30fc3329b84cc638646310259c663c1051` | origin/main. **`F:\Projects\Ymir` local `main` is 11 behind** (its last fetch was 2026-10-29; it has no SoA world at all). I ran `git fetch origin` only (refs, no checkout). Lodur's sibling default `..\Ymir` therefore builds against a tree without `Box3DSoaWorld` until that checkout is fast-forwarded; that is Self's call, not done. |

## Body facts (probes)

All probes in detached worktrees `F:\aeth-wt\connect\{Ymir,Lodur,BadDaySimulator}` at the bases above
(Ymir submodule `extern/box3d` at pin `8441b4a`), Windows 11 workstation, 2026-11-19. Worktrees removed after.

1. **Windows build of Ymir native works with MinGW, with one defect.** No MSVC/VS on the workstation
   (`vswhere` finds nothing). `CMAKE_GENERATOR="Ninja Multi-Config" CC=gcc dotnet test tests/Lodur.Tests`
   (gcc and ninja from StrawberryPerl, CMake 3.x in Program Files) configures "Box3D on MinGW", builds 53
   objects, and links `bin\Debug\libymir_box3d.dll`. The build then fails MSB3030: `Ymir.Box3D.csproj`
   expects `bin\$(Configuration)\ymir_box3d.dll` (MSVC naming; MinGW adds the `lib` prefix). After copying the
   DLL to the unprefixed name: **Lodur.Tests 21/21 pass on Windows** (6 s), including same-seed-same-hash
   within one process. The CultLib pin guard in Ymir's `Directory.Build.targets` did not fire for this graph.
   Without `CMAKE_GENERATOR` set: not tried.
2. **What Ymir's SoA world exposes for a hand in a box** (`src/Ymir.Box3D/Box3DSoaWorld.cs`, origin/main;
   `docs/soa-world.md` "Not done"): body lanes (stable id, kind, parent, limb, position, rotation quaternion,
   linear and angular velocity), write lanes (force, torque), joint lanes. **No contact facts, no sensor shapes,
   no overlap or ray queries, no body removal.** Box3D compiles `sensor.c` but Ymir's SoA ABI does not
   surface sensors. So the hand-in-box test is geometry over the body lanes: the hand point is the
   palm body's centre (the ragdoll has palm, thumb and fingers bodies per arm).
   The box is a game stimulus volume (layer 1 "named game stimuli"), not a physics body.
3. **The SoA world is internal.** `Box3DSoaWorld`, `Box3DSoaView` and the kind enums are `internal`, visible to
   `Ymir.Core`, `Ymir.Box3D.Soa.Tests`, `Lodur.Body.Ymir` and `Lodur.Tests` by `InternalsVisibleTo`
   (`src/Ymir.Box3D/Properties/AssemblyInfo.cs:3-6`, commit 0227636 "until Ymir.Core has a public surface").
   `Ragdoll` (Lodur.Body.Ymir) builds a `BodyPlan` into a caller-owned `Box3DSoaWorld`. **Bad Day Simulator cannot name the
   world today**, so it cannot own its one physics world without a Ymir change.
4. **A pelvis weld to a static body holds the ragdoll upright for 20 s.** `AddJoint(Weld, staticBox, pelvis)`
   is accepted (native refuses only out-of-range or equal indices). Full-strength ragdoll, pelvis welded to a
   5 cm static box behind it: head 1.739 m and pelvis 1.110 m at every 2 s checkpoint to 20 s. Unwelded it
   topples at about 2 s (first-slice.md), and the pain box breaks at about 15 s.
5. **Arm reach into a box, at strength 1.** Right shoulder target `FromAxisAngle(1,0,0,-1.0)` (joint frame),
   elbow `FromAxisAngle(0,0,1,-0.6)`: hand tip settles at about (-0.21, 1.19, 0.46). Over 2-4 s of hold its
   range is x [-0.233, -0.191], y [1.175, 1.220], z [0.448, 0.469] (natural wobble, no tremor). Shoulder
   `-1.2`, elbow 0.3 gives (-0.21, 1.20, 0.48). Positive shoulder angles swing the arm back (+1.0 puts the hand
   at z -0.25). Strength 0 leaves it hanging at y 0.96.
6. **Strength above 1 is not safe on the arm.** Strength 3 with the same targets sagged the hand to y 1.03 and
   let it drift in x to -0.35 (explicit PD past its stable damping). Keep strength in [0, 1].
7. **Withdrawal is fast.** Switching the arm target to shoulder +0.3, elbow 2.0 (strength 1) takes the hand out
   of a 0.12 m half-extent box centred at (-0.21, 1.19, 0.46) in 5-6 ticks at 120 Hz (about 50 ms).
   Shoulder -0.3 / elbow 1.5 takes 18 ticks.
8. **Visible strain needs low frequency.** A 9 Hz shoulder-target tremor of 0.08 rad leaves the hold range
   unchanged to the millimetre (the PD at swing gains 20/1 filters it). 3 Hz at 0.15 rad widens y to
   [1.162, 1.230]; 4 Hz at 0.3 rad to [1.161, 1.233] and z to [0.437, 0.477]. So strain shows as a shake of a
   few Hz, not physiological tremor frequency.
9. **Pain-box baseline at the BDS base** (Windows, git-bash, `dotnet run --project src/BadDay.Cli -- run
   --seed N > file`; Summary uses `WriteLine`, so CRLF on Windows and the hashes are platform-bound):
   13/13 tests pass; seed 1 "will broke at 14730 ms, hand out at 14830 ms, 16 flinches". sha256 run-1
   `973603f9…dd4cc`, run-2 `cf1a6060…a4af95`, run-3 `bafc5967…20e1`. PainSweep table (scratch csproj compiling
   `tools/PainSweep/Program.cs` against BadDay.Core; stdout) sha256 `13424cd1…7bf`; cells e.g. 0.80 x 0.95 =
   14.9 s, 0.20 x 0.50 = 95.4(8/9), every suppression-0 cell "never". Hands recaptures at base with one method.
10. **Where the model's arithmetic sits** (`src/BadDay.Core/PainBoxSimulation.cs` at base): traits 22-23; box
    stimulus 59-61; nociceptor threshold, sensitisation, conduction ring 64-69; pain 72-73; reflex suppression,
    leak, flinch, strain 77-86; resolve drain, recovery, break 89-101; sample 104; withdrawal 106-112. Order
    inside a tick matters: suppression reads last tick's resolve (gain set ahead of time); the Flinch row is
    logged after pain but before strain and resolve update (lines 83, 85, 94).

## The model page (step 0b)

No persistent content is introduced by these cuts: every parameter stays a code default or a CLI argument,
so `content-is-cultcache` is not engaged yet (follow-up `lodur:follow_up:drive-profiles-as-content`).

| Kind | What names it | Lifecycle | Who decides |
|---|---|---|---|
| Nerve, reflex and drive parameters (`NerveParameters`, `WithdrawalParameters`, `DriveParameters`) | Nothing persistent: C# records with defaults in Lodur.Core; a per-actor instance is a value | Built once when the consumer creates an actor (per-seed traits folded in as `Gain`, `DrainTrait`), immutable for the actor's life | Lodur owns the schema and defaults; the consumer (BDS scenario) owns per-actor values and the trait draw |
| Actor (nervous state: afferent rings, drive state, goal) | The consumer's handle; no id inside Lodur | Created with the scenario, ticked once per physics step, never persisted, dies with the scenario | Lodur.Core alone writes it; nothing else writes drive state |
| Puppet definition (the 19-body humanoid: shapes, joints, gains, regions) | `BodyPlan` data in Lodur.Core (`BodyPlans.Humanoid`), built by `Ragdoll` in Lodur.Body.Ymir; world stable ids `firstStableId..+18` passed by the caller | Built into the caller's world once; no removal (Ymir has none) | `BodyPlan` owns geometry, joints and gains, `Ragdoll` builds them; BDS owns placement and stable-id allocation |
| Stimulus source (the pain box) | BDS object, scenario-local; not a world body | Placed by the scenario; intensity written every tick from its schedule (ramp, noise, disable time) | BDS writes intensity and on/off; Lodur perception only reads it and decides containment and transduction |
| Restraint (stool weld) | BDS scene object: one static box + one weld joint in the world | Created with the scenario | BDS (a game object acting on the body through the world, like a grab; not a layer above Lodur) |
| Scenario run | (seed, parameters) | One run, headless or on the body | BDS; the timeline is output, not content |

## Rationale

- **Wrong owner, now.** The pain box is perception, reflex and drive flattened into the consumer. Lodur.Core
  holds only the PD law. The cut moves layers 1-3 and a layer-4 stand-in into Lodur.Core and leaves BDS with
  the things a game owns: the box schedule, the seed and trait draw, the restraint, the world, the recorder.
- **Move first, retune never in the same cut.** The layers keep the pain box's double arithmetic and the order
  of operations, so seed 1 still breaks at 14730 ms and the sweep table is byte-identical. The only planned
  difference: every timeline row becomes end-of-tick state, which changes the strain and resolve columns of
  Flinch rows (they were logged mid-tick). Any retune (latencies, `[M]` figures) is a later, separately
  falsifiable cut.
- **The game owns the world** (`one-physics-world`, `lodur-body-on-ymir`): BDS creates the Ymir world and Lodur
  adds its bodies to it. That needs Ymir's SoA world to be nameable outside Ymir, hence the Ymir cut. A Lodur
  facade that creates and hides the world would make Lodur the world's owner, which is the split this invariant
  forbids.
- **Headless stays engine-free.** BadDay.Core references only Lodur.Core, so the sweep and the headless tests
  build with `dotnet` alone; the body scenario lives in a new BadDay.Ymir project. One more csproj buys a
  measure that does not need cmake.
- **Posture.** The PD ragdoll cannot balance for 15 s; the puppet sits strapped at the pelvis (body fact 4).
  The learned tracker (v2) is what lets it stand later.
- **Visible strain** is a 3-4 Hz shake along the withdraw direction, scaled by strain (body fact 8), plus the
  flinch blend. Sweat is a render concern; there is no renderer.

## Cut order

1. `ymir-soa-public` (GameCult/Ymir): **not admitted**, because Ymir is not in the lodur repo set (target r2:
   Lodur, BadDaySimulator). Recommended: target r3 adds GameCult/Ymir; then admit the draft below. Tracked as
   `lodur:follow_up:ymir-soa-public-surface`. Ymir is Nibu's stewardship (`nibu:stewardship:gamecult_-ymir.n1`).
2. `nervous-layers` (GameCult/Lodur), `lodur:cut_spec:cut-nervous-layers.r1`. Its Core commits need nothing
   from Ymir; its body-binding commit needs cut 1 on Ymir main.
3. `pain-box-on-lodur` (GameCult/BadDaySimulator), `lodur:cut_spec:cut-pain-box-on-lodur.r1`, depends on 2;
   blocked on `lodur:question:bds-consumes-lodur`.

### Draft cut spec: ymir-soa-public (admit after target r3 names GameCult/Ymir)

- repo GameCult/Ymir, branch `nibu/soa-public`, base `7ba31c30fc3329b84cc638646310259c663c1051`.
- deletes: `src/Ymir.Box3D/Properties/AssemblyInfo.cs:5-6` (2 lines, the Lodur friend grants).
- file_changes: `src/Ymir.Box3D/Box3DSoaWorld.cs:5` `Box3DSoaKind`, `:13` `Box3DSoaJointKind`, `:151`
  `Box3DSoaView`, `:223` `Box3DSoaWorld` become `public`; the native desc/view structs and
  `Box3DSoaNative` stay internal; the `Box3DSoaView` constructor stays internal. `native/Ymir.Box3D/CMakeLists.txt`
  `:22` `set_target_properties(ymir_box3d ...)` gains `PREFIX ""` so MinGW emits `ymir_box3d.dll` (body fact 1).
  `docs/soa-world.md` "Managed (... internal)" and "Not done ... public typed surface" lines: say the SoA world
  is public in Ymir.Box3D. Ymir.Core keeps its own surface.
- verification: `dotnet test tests/Ymir.Box3D.Soa.Tests` green; Lodur at 18c4ffd with `YMIR_ROOT` at the head
  builds and its 21 tests pass with the friend grants gone; on Windows,
  `CMAKE_GENERATOR="Ninja Multi-Config" CC=gcc` builds with no DLL rename. Negative: `InternalsVisibleTo\("Lodur`
  in `src/` finds nothing.
- estimate: about +10 / -4 lines, no targets.

## Proposed target r3 text for `Lodur/docs/lodur-target.md`

Hands commits this in `nervous-layers`; Self admits the typed `lodur:target:r3` (invariants as below, repos
GameCult/Lodur, GameCult/BadDaySimulator, GameCult/Ymir; doc at that commit) with the superseding resolution
of r2.

```markdown
# Lodur: target

Campaign `lodur`. The operator's rulings live in Nibu's mind under roots `lodur` and `motion-pipeline` (the
workshop ran under `motion-pipeline`; some of those show as moved in Eureka's mind, and Nibu's copies are the
ones in force). This document holds the rationale and the design truths the campaign produces. Where it and a
ruling disagree, the ruling wins and this file is stale.

## What it is for

A character that reacts to forces and stimuli, and compensates for injury, the way a person would: it pulls
back from something hot, jumps when poked, and crawls toward its objective on one leg if it wants that
objective badly enough. (`motion-pipeline:ruling:euphoria-stimulus-and-impairment`.)

The field has the physics and the balance. NaturalMotion's Euphoria shipped active, physically simulated
characters in GTA IV through Red Dead Redemption 2, and learned trackers (DReCon, ProtoMotions, MaskedMimic)
follow arbitrary motion while keeping their feet. What the field does not have, per the prior-art pass, is the
nervous system on top: senses that are not forces, reflexes layered under deliberate behaviour with their own
latencies, and a character whose will competes with its pain. That layer is Lodur. Evidence:
`gamecult-ops/docs/research/euphoria-at-home-prior-art.md` (sections 1-9 at `8320c18`, section 10 at
`15d9a4e`).

Named after the god in Voluspa who gives the first humans blood, warmth and good colour
(`motion-pipeline:ruling:euphoria-middleware-named-lodur`).

## Layers

Signal flows one way per tick, in this order; each layer has one owner. Layers 1-4 are `Lodur.Core`
(`netstandard2.1`, no references); layer 5 is a body binding.

1. **Perception** (`Perception`, `NerveParameters`, `BoxStimulus`). Contacts, impulses, temperature,
   proximity and named game stimuli become typed signals on body regions. Input: one point per body region
   (from the body binding, read-only) and the game's stimulus sources. Today one channel (noxious) and one
   source kind: a box volume with an intensity the game writes each tick. Output per region: the stimulus, and
   the afferent signal after threshold, sensitisation and conduction delay. A poke that barely moves the body
   is still a stimulus.
2. **Reflex** (`WithdrawalReflex`, `WithdrawalParameters`). Fast, local responses to afferent signals
   (withdrawal now; startle and bracing later), each with a latency, overlaid on whatever the body is doing.
   Each reflex's gain is set ahead of time by the drive, the way descending pathways modulate spinal reflexes
   (the Gom Jabbar case): a reflex reads the drive state of the previous tick, never waits for behaviour.
   Output: the reflex command that leaks past suppression, the strain load of holding the rest down, and
   flinch events. Physiology sets the scale: withdrawal roughly 60-150 ms, voluntary reaction about 200 ms
   (figures `[M]` in the research doc; verify before they become constants).
3. **Drive** (`DriveState`, `DriveParameters`). Per-character state that evolves every tick: pain from
   afferent input, strain from suppressed reflexes, resolve drained by both and scaled by the objective's pull;
   later shock on a clock that blood loss speeds and adrenaline that spikes on threat. Resolve reaching zero
   breaks the will, which releases every suppression. Static per-character values are this state with its
   dynamics frozen. (`motion-pipeline:ruling:euphoria-drive-is-dynamic-state`.)
4. **Behaviour.** The drive selects and weights behaviours (hold against a reflex, withdraw, writhe, crawl to
   the objective, pass out, get up) that emit targets and per-muscle strength into caller-owned arrays, and
   nothing else. Today one stand-in: hold a pose against the withdrawal reflex, blended toward a withdraw pose
   by the reflex leak, shaking with strain, and withdrawing once the will breaks.
5. **Body.** Follows targets with per-muscle strength in the game's physics world. v1 is GameCult's own
   PD-tracking active ragdoll on **Ymir**, GameCult's boundary around a pinned Box3D, read and driven through
   Ymir's structure-of-arrays world in place (`lodur:ruling:lodur-body-on-ymir`): `Lodur.Body.Ymir`. It owns
   the puppet's shapes, joints and gains and reports body-region points upward to perception. v2 is a small
   learned tracker for upright states (DReCon scale: 340 us per character per frame measured, 60 us of it the
   policy) behind the same interface. (`motion-pipeline:ruling:euphoria-body-layer-order`.)

`Actor` runs layers 1-4 for one character in that order once per physics step, from caller-owned inputs into
caller-owned outputs, and reports its events (flinch, will broke, goal changed) as flags.

## Invariants

- `core-engine-free`: the core assemblies reference no engine and no physics. A Unity or Ymir type in the
  core is a defect, not a convenience. The core's tests build without Ymir.
- `body-one-interface`: the body consumes only targets and per-muscle strength. Drive, behaviour and reflex
  never set a joint, force or physics property directly; swapping the PD ragdoll for a learned tracker changes
  no layer above.
- `one-physics-world`: the game creates and owns one physics world, Ymir's; Lodur adds its bodies to that
  world and never creates one of its own. Game objects (a restraint, a box, a thrown chair) act on a puppet
  through that world.
- `reflex-gain-from-drive`: a reflex fires on its own shorter latency and overlays the running behaviour; it
  never waits for behaviour selection. Will acts on reflexes ahead of time, not after: drive state (resolve,
  the pull of the objective) sets each reflex's gain, so a resolved character can hold its hand in the box.
  Suppression is never free: the stimulus still feeds pain, and holding a reflex down shows as visible strain
  until resolve breaks. Deliberate behaviour takes over a fired reflex only after its own, longer latency.
  (`motion-pipeline:ruling:lodur-reflexes-suppressible-by-will`.)
- `drive-is-state`: what a hurt character does is decided by drive state evolving over time, never by a
  per-wound lookup.
- `bounded-tick`: per-character work per tick is bounded and allocates nothing in steady state, and the same
  inputs give the same outputs. A middleware that hitches is unusable in a game.
- `content-is-cultcache`: persistent content (puppet and object definitions, drive parameters, saved levels
  in Bad Day Simulator) is CultCache, per the GameCult substrate doctrine. JSON is schema publication only.
  Parameters that are only code defaults are not yet content.

## First consumer

Bad Day Simulator (`GameCult/BadDaySimulator`): a 3D sandbox in the spirit of how People Playground plays,
not its camera or UI. The player gets a level and places puppets of various sorts and objects with set
behaviours and interactions. (`motion-pipeline:ruling:euphoria-sandbox-shape`,
`motion-pipeline:ruling:euphoria-sandbox-named-bad-day-simulator`.) Its one physics world is Ymir's. It is a
separate repo so that Lodur's boundary has a real consumer from the first cut. Its first item is the pain box,
the Synaptic Induction Chamber (`lodur:ruling:bds-pain-box-item`,
`lodur:ruling:bds-pain-box-named-synaptic-induction-chamber`).

## Not in scope

- Adapting to arbitrary changes of body topology. The operator called it too ambitious; impairment is handled
  through the drive and the behaviour set.
- A Rust port of the core.
- The StreamPixels overlay as a consumer before StreamPixels M1 ships.
- Depending on RootMotion PuppetMaster (paid, not redistributable, and no balance control: research
  section 10.1).
- Hand-tuned balance controllers of the SIMBICON family as the upright path.

## Open

- **Impairment states.** Self's proposal, not ruled: a closed set of named body states (intact, missing a leg,
  missing an arm, both legs), with mirror symmetry; crawling as the first locomotion after any leg loss.
- **How Bad Day Simulator consumes Lodur** (`lodur:question:bds-consumes-lodur`).
- **The pain measure** (`lodur:question:pain-sweep-measure`).
- **Ymir gaps Lodur needs next**: contact facts for pain from impacts, body removal, joint springs for stiff
  ankles (`nibu-body:follow_up:ymir-soa-lodur-needs`).
- **Renderer and rig** for Bad Day Simulator: nothing draws the puppet yet.
- **Training host for v2.** Raven trains only when the household's GPU allows
  (`motion-pipeline:ruling:raven-gpu-defers-to-household`).
- **Trademark search** on "Bad Day Simulator" before anything public is announced.
```

Changes against r2: header names Nibu's mind; layer 5 binds to Ymir (Unity ArticulationBody wording gone, it
was replaced by `lodur-body-on-ymir`); layers carry their first type names and the tick order; `one-physics-world`
restated with the game as the world's owner; `core-engine-free` adds "core tests build without Ymir";
`bounded-tick` adds same-inputs-same-outputs (it was implicit in every test); `content-is-cultcache` says code
defaults are not content; the Rust-port line drops "until a first consumer outside Unity exists" (Unity is no
longer the first host); Open drops the Unity version and UPM items (moot under Ymir) and the rig licence item
becomes renderer and rig.
