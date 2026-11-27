# Bad Day Simulator MVP on Lodur and Ymir (map)

Imagination pass `bds-mvp-map`, nibu-imagination, session nibu-self-2026-11-17, 2026-11-20.
Serves `lodur:ruling:bds-mvp-scope` and `lodur:ruling:bds-sandbox-arenas-will-economy`, under target
`lodur:target:r3`. Committed as `Lodur/docs/bds-mvp-map.md`; the bases, probes and body facts below are as of the pass (Modeling refreshed the state columns and the cut table on 2026-11-20, see "Landed since the pass"); the target r4 text below went to
`docs/lodur-target.md` when Self admits r4.

## Bases

| Repo | Base | Note |
|---|---|---|
| GameCult/Ymir | `0b0321a` (main) | Box3D v0.1.0 at `extern/box3d` 8441b4a |
| GameCult/Lodur | `1491bed` (read), main now `a048827` | rung 3 sensory load merged to main during this pass (a048827); Lodur anchors below are at 1491bed and move with it |
| GameCult/BadDaySimulator | `a218c55` (main) | |
| Godot client | none | a parallel Imagination maps `client-ready-sim`, `godot-client-v1`; not admitted at 5566 |

In-flight specs this map sequences around (no report unless noted): `rung3-sensory-load` (merged), `rung3-office-stress`, `rung4-goal-flip`, `rung4-office-flip`.

## Landed since the pass (Modeling, 2026-11-20; mains: Ymir `5104b17`, Lodur `f9e302e`, BDS `bd01101`)

Body facts 1 and 8-10 below describe the bases, not today. What the cuts changed; the owners' inputs, outputs, derived
state and forbidden writers are in `docs/lodur-target.md` ("Who writes what, per tick") and BDS's README ("Who owns
what"); rulings and specs are cited by id, not restated.

- **Ymir** (ABI 8): contact and hit facts with materials, exact per-step contact impulse, grab anchor, motor joint,
  point impulse, ray, joint handles with generations, tombstoned joint lanes, and joint origin lanes (the joint
  frame's origin in the world, on body A). Not landed: thresholds, overload facts, removal of bodies and joints
  (`ymir-break-and-remove`, still refused by the API).
- **Lodur**: the drive's will breaks and mends as a recurring event (`Breaks`, `WillMendsAt`); `Will` = min(resolve x
  attention, consciousness) is derived; every voluntary pose is written at `Will^WillCurve` (the will mix), an
  involuntary goal onset at full weight; goals carry `EntryResolve`, `Effort`, `InvoluntarySeconds`, and `SensoryGain`,
  `EntryArousal`, `EntrySeconds` (the Actor ramps gain and arousal over the entry, Perception gates the sense channels
  with the gain and refuses one outside [0, 1]). Startle (the Attention writer) and physiology (the Consciousness
  writer) are not built.
- **BDS**: the chamber and the office are scenarios on one stepping session each; the office arc is snap, rage, calm
  (the stress column is arousal), and each scenario's release goal is declared by id. The Godot client draws the
  body run as one skinned SDF puppet: `PuppetField` meshes, `PuppetModel` owns bind, signature and rebuild,
  `PuppetSkin` skins every frame and derives the surface, `Puppet` owns the Godot mesh and the build counter, the
  shader only draws the bevel, `RenderCheck` is the windowed oracle. Not landed: a sandbox session, commands,
  levels, runtime bodies.

## Body facts (probes)

All probes 2026-11-20, read-only on `F:\Projects`; the C probe is
`scratchpad/imag-mvp/probe/probe.c`, built with StrawberryPerl gcc 13.2
(`gcc -O1 -std=c17 -mavx2 -I extern/box3d/include -I extern/box3d/src probe.c extern/box3d/src/*.c`)
against Ymir's pinned Box3D, sphere r 0.1 m, density 1000, 1/120 s, 4 substeps, sleep off, one worker.

1. **Ymir SoA has none of what the MVP needs from physics.** `native/Ymir.Box3D/ymir_box3d_soa.c`: body kinds
   sphere/static box/capsule/box only (42-48, check at 365); joint kinds revolute/spherical/weld only (50-55,
   C# `Box3DSoaWorld.cs:13-18`); no remove, no kinematic body, no motor joint, no impulse, no raycast, no events;
   `add_body` builds every shape with `b3DefaultShapeDef()` (453) whose `enableContactEvents` is **false** in
   v0.1.0 (`extern/box3d/src/types.c:66-80`, the field is zero-initialised). Lanes are dense, append-only
   (`count++` at 485), capacity fixed at create (289-333), views are zero-copy pointers fixed for the world's life
   (`Box3DSoaWorld.cs:150-155`). Duplicate stable ids are refused by a linear scan (419-425). Shape `userData`
   already carries the stable id (454).
2. **Box3D v0.1.0 has every API the MVP needs.** Headers: `b3World_GetContactEvents` (box3d.h:69),
   `b3World_GetJointEvents` (72), `b3DestroyBody` (490), `b3Body_SetTargetTransform` (557),
   `b3Body_ApplyLinearImpulse` (598), `b3DestroyJoint` (1014), `b3Joint_GetConstraintForce/Torque` (1059/1062),
   `b3Joint_SetForceThreshold/TorqueThreshold` (1080/1086), `b3CreateMotorJoint` (1219), `b3_kinematicBody`
   (types.h:230), `b3JointDef.forceThreshold/torqueThreshold` (types.h:622/625, default `FLT_MAX`,
   joint.c:28-29), `b3MotorJointDef` with linear/angular spring hertz, damping and max force (types.h:696-730),
   manifold points with `normalImpulse` and `totalNormalImpulse` (types.h:2573/2577). This corrects
   `lodur:research:mvp-prior-art.contact-events` ("no normal-impulse accessor found in the docs"): the
   accessor is `b3Body_GetContactData` / `b3Contact_GetData` manifolds.
3. **Contact events, run.** With `enableContactEvents` and `enableHitEvents` on both shapes, a sphere dropped
   1.4 m gave 1 begin, 1 hit (approach 5.25 m/s), 0 ends over 120 steps. At rest, the manifold's
   `normalImpulse` summed to 0.0873 per step against m g dt = 0.349: it is the **last substep's** impulse
   (0.349 / 4). Force from a contact is `totalNormalImpulse / dt`, or `normalImpulse * substeps / dt`.
4. **End events after removal, run.** Destroying a resting body gives **no** end event in the destroy call's
   buffer and **one** after the next step, carrying the destroyed shape's id (invalid by then).
   `b3Body_Disable` behaves the same. So a consumer must resolve a dead shape id to the stable id it had. Ymir's
   planar session already does this: `ymir_box3d.c:694-712` (`ymir_session_resolve_event_shape`, a
   retired-shape table filled at removal, 1420-1437) and drains begin/hit/end with manifold details at 773-830.
   The SoA world should reuse that pattern, not invent one.
5. **Joint over threshold, run.** A spherical joint with `forceThreshold` 200 N under a ramped pull reported one
   `b3JointEvent` on the step the force crossed it; `b3Joint_GetConstraintForce` read 1241.9 N on that step; the
   joint is **not** destroyed by Box3D (event only); `b3DestroyJoint` from the event left the child falling free.
   Joint events are only for awake joints; Ymir disables sleep (soa.c:337, 447), so every joint reports.
6. **Grab, run.** A shapeless kinematic body moved by `b3Body_SetTargetTransform` each step, joined to a 4.19 kg
   sphere by a motor joint (linear spring 5 Hz, damping 0.7, max spring force 50 N, angular spring off), held
   the sphere at (0.72, 1.02) against a target of (1.0, 1.5): the max force (50 N against 41 N of weight) bounds
   the pull, as the Box2D v3 drag sample states (`lodur:research:mvp-prior-art.grab-drag`). Destroying the
   grabbed body destroyed the motor joint with it (`b3Joint_IsValid` 0).
7. **Impulse at a point, run.** `b3Body_ApplyLinearImpulse` of 1 N s, 0.1 m above the centre, gave v 0.28 m/s and
   spin -5.0 rad/s: an off-centre poke turns the part, which the force lanes (centre of mass only, soa.c:593-626)
   cannot do.
8. **Lodur today.** Perception senses named box volumes at points only (`Perception.cs:82-113`); regions are two
   sense points read by `Ragdoll.ReadSensePoints`; one reflex kind (`WithdrawalReflex.cs`); drive is pain,
   strain, resolve, `WillBroken` (`DriveState.cs`); behaviour blends two poses by leak and shake
   (`PoseBehaviour.cs:87-115`); the body takes one strength and one joint-space target per plan joint
   (`Ragdoll.Drive`). No IK, no clips, no physiology, no grip; the part tree is `BodyPlan` (parts, joints that are the tree,
   sense points), without HP, coverage or vital flags. Rung 3 adds emitters and arousal; rung 4 the goal flip and `PoseBehaviour`.
9. **BDS today.** Headless and body pain box only (`src/BadDay.Ymir/BodyPainBox.cs`); the world is built per run
   with capacity 15 (58); the puppet is welded to a stool because the PD ragdoll cannot balance for 15 s (59-62).
   No session type survives beyond a run, no commands, no level content.
10. **Godot today.** Nothing; no repo under `F:\Projects` matches.

## The systems, by owner

`exists` cites base code; `new` is MVP work and names the cut that builds it (cut order below).

### Ymir (physics facts and actuators; decides nothing about bodies' meaning)

| System | State | Cut |
|---|---|---|
| Contact facts: begin/end/hit per step with stable ids, lanes, point, normal, approach speed, material ids; per-body contact force lane | landed (ABI 5-7; `docs/soa-world.md`: facts per step, exact per-step contact impulse per body and per hit); planar session precedent `ymir_box3d.c:694-830` | `ymir-contact-facts` |
| Material id per body (u64, the game's), carried on hit facts | landed (the body's `material_id`, on the hit facts) | `ymir-contact-facts` |
| Raycast (closest hit: lane, stable id, point, normal, fraction) for picking and hitscan | landed (ABI 5) | `ymir-grab-anchor` |
| Grab anchor: shapeless kinematic body moved by target transform; motor joint (linear/angular spring, max force) | landed (ABI 5-6: grab release, joint handles with generations; probe 6) | `ymir-grab-anchor` |
| Point impulse command | landed (ABI 5; probe 7) | `ymir-grab-anchor` |
| Joint stress: per-joint force/torque thresholds (create and set), overload facts with measured force/torque | new; probe 5 | `ymir-break-and-remove` |
| Runtime removal of bodies and joints; tombstoned lanes; reuse after a step; removal facts (incl. joints that die with a body); end-touch attribution for dead shapes | new; probe 4 | `ymir-break-and-remove` |
| Placeable joint kinds for building (motor-driven revolute, prismatic, distance/rope, spring) | new | follow-up `ymir-joint-kinds` |
| More shapes (hulls, compounds) for authored props | new | follow-up `ymir-shapes` |

### Lodur (general character middleware; no gore)

| System | Layer | State | Cut |
|---|---|---|---|
| Part tree: the body plan as content (parts, parent, side, max HP, coverage, vital flags, muscles, Ymir lane binding) | 3 data + 5 binding | `BodyPlan` holds parts, joints as the tree and sense points; HP, coverage and vital flags are new | `lodur-physiology` |
| Touch perception per part: pressure (contact force), impact (hit speed, material), external-joint pull (grab, strap) | 1 (+5 reads Ymir) | new | `lodur-touch` |
| Reflexes: withdrawal (exists), startle on sensory and touch onsets, flinch-from-touch, brace on impact | 2 | withdrawal exists `WithdrawalReflex.cs` and reads `Will`; rest new | `lodur-startle-attention`, `lodur-reflex-set` |
| Physiology as drive state: per-part HP and injuries, body blood fraction, bleeding, pain sum, shock, consciousness, capacities scaling muscle strength, death | 3 | new | `lodur-physiology` |
| Will economy: resolve (exists) times attention (new, startle drains it) is the budget every voluntary layer draws on; reflexes act against it | 3/4 | landed except attention: `Will` = min(resolve x attention, consciousness) in `DriveState`, read by the reflex and the will mix; the Attention and Consciousness slots read 1 until startle and physiology write them | `lodur-startle-attention` (open), `lodur-will-mix` (landed `afb8df3`) |
| Voluntary motor layers: hold pose (exists as `PoseBehaviour`), clip player, IK goals (reach, look, aim), grip; mixed by will | 4 | hold exists, weighted by `Will^WillCurve` (`PoseBehaviour.Write`; an involuntary goal onset writes at weight 1); rest new | `lodur-will-mix`, `lodur-ik-goals`, `lodur-clips` |
| Body interface: strengths + joint targets (exists) + IK goals with weights | 5 | partial `Ragdoll.Drive` | `lodur-ik-goals` |
| Severing: physiology decides; binding removes the joint in Ymir; impairment states derived per part, mirror-symmetric | 3 decides, 5 executes | new | `lodur-sever-impair` |
| Grip: hand holds an object by a breakable weld whose threshold follows grip strength; drop on pain, overload, unconsciousness; the tool frame is an IK end effector | 4/5 | new | `lodur-grip` |

### Bad Day Simulator (content, rules, sandbox; owns the gore)

| System | State | Cut |
|---|---|---|
| Sandbox session: one deterministic world with puppets and objects; command queue (spawn, remove, grab begin/move/end, poke, cut, place joint, wire) | new; today per-run `BodyPainBox.Run` | follow-up `bds-sandbox-session` (after `client-ready-sim`) |
| Damage content: material ids to insult kinds and gains (blade Cut, bat Blunt, spike Pierce), tear from joint overload | new | follow-up `bds-damage-content` |
| Gore presentation data: blood colour, bleed particle rates by wound, decal sets, stump caps, gibs | new; waits on `bds-gore-look` | follow-up `bds-gore-content` |
| Levels and arenas as CultCache: objects, joints, stimulus volumes, emitters, triggers, signal wires; arenas are levels built with the player's commands | new | follow-up `bds-level-content` |
| Contraption logic: signals between objects (switch, timer, sensor volume, motor, emitter) ticked in the session | new | follow-up `bds-contraption-signals` |
| Guns and ballistics: hitscan by Ymir raycast, recoil impulse, damage content | new | follow-up `bds-guns` |

### Godot client

| System | State | Cut |
|---|---|---|
| Render bodies from Ymir lanes by stable id | landed: `BodyRenderer` draws static boxes and `Puppet` the dynamic bodies as one skinned SDF mesh (BDS README, "Who owns what") | `godot-client-v1` |
| Bodies added and removed at runtime from Ymir lifecycle facts | new | follow-up `godot-runtime-bodies` |
| Input to commands: ray pick, grab drag, poke, cut, place, wire | new | follow-up `godot-interaction` |
| Blood visuals from BDS gore events (particles at wounds, decals at hits, stump caps) | new | follow-up `godot-blood` |
| Building tools UI over the level commands | new | follow-up `godot-build-tools` |

## Authority map (MVP)

**Owner of each decision, one each.**

- **Whether a body or joint exists in the world:** Ymir, on command only. Ymir never removes anything on its
  own; a joint over threshold is a fact, not a removal (probe 5: Box3D itself does the same).
- **What touched what this step, how hard:** Ymir's contact facts and contact-force lane. Nothing else derives
  touch from geometry; the box-volume stimuli stay named game stimuli (a heat source is not a contact).
- **What damage an insult does to a part, and whether a part is severed, disabled or dead:** Lodur physiology,
  from insults (part, kind, magnitude) the binding builds out of Ymir facts and the game's material table.
  **The joint break threshold is no longer an owner; it is a stress sensor** whose facts become Tear insults.
- **What the body tries to do:** Lodur's will mix: voluntary layers weighted by available will, reflexes against
  it (ruling `bds-sandbox-arenas-will-economy`). `PoseBehaviour`'s blend (`PoseBehaviour.cs:87-115`) is
  no longer an owner; it becomes one voluntary layer (hold pose) inside the mix.
- **Muscle strength ceiling:** physiology capacities times consciousness; the will mix may only scale down from
  it. Content gains are parameters, not owners.
- **Where the player's hand is and what it holds:** BDS sandbox session (grab handle); Ymir executes the anchor.
- **What a material means (sharp, blunt, hot) and how gore looks:** BDS content. Lodur sees only insult kinds.
- **What is drawn:** Godot, from Ymir lanes and lifecycle facts, BDS presentation events. Godot owns no
  simulation state and never writes a lane.

**Inputs/outputs per tick (one order, the body run's order today, `BodyPainBox.cs:106-110`, extended):**
BDS applies queued commands (spawn/remove/grab target/impulse) → Ymir step → Ymir facts (contacts, hits,
overloads, removals) → binding turns facts into per-part touch samples and insults → Actor tick: perception,
reflex (gain from last tick's drive), drive and physiology, will mix → binding writes strengths, targets, IK
solve onto torque lanes, executes severing and grip changes as commands for the next step → BDS emits
presentation events (wound opened, part severed, death) → Godot draws.

**Derived state:** impairment state per part (from HP, severed, nerve flags); capacities; consciousness;
"dead"; blood visuals; Godot node set (from lifecycle facts); joint overload (a sensor reading).

**Forbidden writers:** Godot writing any lane or calling Ymir directly; BDS setting muscle strengths or targets
(body-one-interface); Ymir destroying a joint on threshold; any per-wound lookup choosing behaviour
(drive-is-state); a voluntary layer writing targets outside the will weighting; the binding deciding damage.

**Shared paths:** player grab, a scripted grab in an arena, and a level load all go through the session's
command queue; arenas are levels; a player cut and a falling blade go through the same contact-to-insult path;
a torn limb and a cut limb go through the same physiology severing decision; reload of a level replays the
same commands as building it.

**Deletion lines:** `PoseBehaviour.Blend` as the motor owner (in `lodur-will-mix`, landed: `Blend` now only mixes the hold layer's flinch pose, the strength is the will mix); hand-only `Region`
enum and `ReadRegions` as perception's body model (landed: deleted with `Limb` and `Muscle` in `lodur:cut_spec:cut-body-plan-data.r2`; `lodur-touch` reads parts and sense points by plan label); the per-run world in
`BodyPainBox.Run` as BDS's only world (in `bds-sandbox-session`, scenario runs become sessions,
follow-up `scenario-session-unify`).

## Model page: identity, lifecycle, authority

| Kind | Named by | Lifecycle | Decides | Persisted |
|---|---|---|---|---|
| Ymir body lane | caller's `stable_id` (u64, unique among live bodies of a world) and its lane while alive | add → live → removed (tombstoned) → lane reusable after the next completed step | Ymir on command | no (the level persists what spawned it) |
| Ymir joint lane | lane while alive; caller may tag | add → live → removed by command or with its body | Ymir on command | no |
| Body part (def) | (body plan content id, part label) | authored content, immutable at runtime | content (game), schema Lodur | CultCache content |
| Body part (state) | (actor, part label) | spawned with the actor → intact → impaired/disabled → severed or dead; never resurrected in MVP (`bds-reattach`) | Lodur physiology | no in MVP (`bds-gore-look` option persist) |
| Wound (injury) | (actor, part, ordinal) | opened by an insult → bleeding → clotting (content rate) → closed; no tending in MVP | Lodur physiology | no |
| Blood (body) | (actor) scalar fraction | 1.0 at spawn, falls by bleeding, recovers only while nothing bleeds (RimWorld, `rw-blood-bleeding`) | Lodur physiology | no |
| Blood visual (pool, decal, spray) | presentation instance | emitted from wound/hit events, fades or stays per `bds-gore-look` | BDS presentation data, Godot draws | no (question) |
| Severed part | the stable ids of its bodies (they survive; only the joint goes) | severed → loose physics prop owned by the session, grabbable, removable by the player | physiology severs; session owns the prop after | no |
| Grab | (session, pointer) handle; anchor's stable id | begin on ray pick → target moved per tick → end on release, or ends when its body is removed (Ymir removal fact) | BDS session | no |
| Impairment state | (actor, limb slot, side) | derived each tick from part states; closed set `Intact, Impaired, Disabled, Severed` per limb slot, mirror-symmetric | derived (physiology) | no |
| Animation clip | content id | authored, immutable | content (BDS), schema Lodur | CultCache content |
| IK goal | (actor, chain) for chain in the closed set `ArmL, ArmR, LegL, LegR, Look, Aim` | written every tick by a voluntary layer, weighted by will, consumed by the binding's solver | Lodur will mix | no |
| Held object (grip) | (actor, hand side) → object stable id | grip begins on behaviour or level content → weld joint with threshold = grip strength → released by behaviour, reflex, unconsciousness or overload | Lodur grip state; Ymir weld executes | level may author "starts holding X" (content) |
| Level / arena | content id | authored with player tools, saved, loaded by replaying commands | BDS | CultCache |
| Contact / hit / overload / removal fact | (step, ordinal) | one step | Ymir | no |
| Attention | (actor) scalar | 1.0, and nothing writes it yet but the test seam `Actor.SetAttention`; startle will drop it, recovering in under a second | Lodur drive | no |

## Questions for the operator and Deru (one batch; admitted as `question`s)

1. `bds-gore-look`: what gore looks like and whether it stays. Options: stylised (bright blood, decals and
   spurts, capped stumps, no viscera), realistic (wound textures, exposed bone, gibs), stylised with a content
   slider. Recommend stylised default with presentation data shaped to take a realism slider later; blood
   visuals fade per session and are never saved in levels. Grounds: People Playground reads clearly with
   stylised blood (`mvp-prior-art.pp-physiology`); the sim (wounds, blood fraction) is the same for every
   option, so only BDS content and Godot change.
2. `bds-death`: what kills a puppet and what death looks like. Options: RimWorld rules (consciousness 0,
   blood loss 100%, lethal part destroyed: head, neck, torso, heart), then limp and permanent until reset;
   People Playground rules (no hard line; integrity and oxygen fade, revivable by an item); no death in the
   MVP (unconscious only). Recommend RimWorld rules with a session reset, revival items later
   (`rw-consciousness`, `rw-body-tree`, `pp-physiology`).
3. `bds-reattach`: can parts be put back. Options: no; prop-only (the player may weld a severed arm back with
   the joint tool, it hangs limp and stays Severed to physiology); full heal. Recommend prop-only: it costs
   nothing (it is the joint tool) and keeps physiology one-way.
4. `bds-dismember-trigger`: what takes a part off. Options: blades and extreme force both (Cut insults past a
   part's HP, and Tear from joint overload while grabbed or hit); blades only; an explicit sever tool. Recommend
   both through one severing rule (DF: severed limbs common; Box3D thresholds as sensors, `joint-break-apis`).
5. `bds-upright`: how the puppet stays up in the MVP. Options: seated or strapped (today's stool weld); an
   assist pin on pelvis and torso whose strength is consciousness times will, so a fainting puppet collapses
   (PuppetMaster pin weights, euphoria doc 10.1); hung or suspended; learned balance (v2, out of MVP per
   `euphoria-body-layer-order`). Recommend the assist pin: it is a voluntary layer in the will mix, reads as
   standing, and its failure is the collapse People Playground shows.
6. `bds-mvp-clips`: which animations exist. Options: poses only (idle, guard, cower, protect-head, brace, reach,
   aim); poses plus a few looping clips (idle sway, struggle, writhe); generated clips from Vili. Recommend poses
   plus three loops, authored as pose keys (rung 4's `PoseBehaviour` generalised); Vili clips when its retarget to
   this ragdoll exists.
7. `bds-will-visible`: what failing will looks like. Options: tremor and sag (strain shake exists,
   `PoseBehaviour.cs:89`), slower and shorter reaches, aim wander; plus an on-screen will bar; plus vocal or
   facial cues (presentation). Recommend tremor, sag, slower reach and aim wander from the will mix, plus a
   debug bar; voice later.

## Target r4 (landed: `5d0fec8` and r5 `58c2201`; `docs/lodur-target.md` is now the text)

Keep r3's invariants and add:

- `one-severance-owner`: A part is damaged, disabled or severed only by Lodur's physiology; Ymir reports
  contacts and joint stress and removes a joint only on command; no physics threshold removes anything by itself.
- `will-is-the-budget`: Every voluntary motor layer (hold, clip, IK reach, look, aim, grip) is weighted by
  available will (resolve times attention, capped by consciousness); reflexes act against it with their own
  gains; nothing writes targets outside that mix.
- `sandbox-is-content`: An arena is a level built from the commands the player has; no engine path exists only
  for an arena; levels are CultCache.

Replace r3's first not-in-scope line with: "Adapting to body topology outside the authored part tree (new
limbs, re-rooted trees, morphology-agnostic learned control). Losing a subtree of the authored tree is in
scope as a closed set of impairment states per limb slot, mirror-symmetric." Add to not in scope: "Weapons,
ballistics, gore and wound presentation (Bad Day Simulator content)."

This resolves the tension the MVP ruling names: severing changes topology only inside the authored tree, so
the behaviour set compensates through a closed set of states (intact, impaired, disabled, severed per limb
slot), as the r3 doc's open item proposed and `euphoria-stimulus-and-impairment` asks (hopping after losing a
leg is a behaviour keyed on the state, not a re-learned controller).

## Rationale

- **Facts, not callbacks.** Ymir fills fixed-capacity fact buffers per step and the caller reads spans, like
  lanes; no managed callback crosses the ABI, overflow is counted, never silent (bounded-tick).
- **Tombstones, not compaction.** Lodur and the client cache lane indices (`Ragdoll._bodyLane`,
  `Ragdoll.cs:16`). Swap-remove, as the planar session does (`ymir_box3d.c:1435-1436`), would move a
  live body's lane under them. A removed lane keeps its last state and a `Removed` kind; reuse waits for a
  completed step so end facts still name the old id.
- **Grab is felt.** A motor joint is not a contact, so a grab produces no contact fact. The binding treats every
  joint on a ragdoll body that the ragdoll does not own (grab, strap, grip, a player's weld) as a touch at its
  anchor with the joint's constraint force: one rule for grabs, restraints and held tools.
- **Poke is a body.** The poke and cut tools are small kinematic bodies on the grab anchor, so their touch
  arrives through contacts and their damage through the material table, like a falling blade. The impulse
  command stays for explosions, recoil and bullets.
- **Physiology is drive state.** RimWorld's model (part tree, HP, pain sum, blood fraction, consciousness,
  capacities; `rw-*` notes) is per-tick state with no per-wound behaviour lookup, which drive-is-state asks.
  Numbers start from RimWorld and ATLS bands (`df-and-atls`) as content defaults.
- **Grip and aim follow PuppetMaster's prop and IK split.** A held prop becomes part of the puppet through its
  own joint, pick-up and drop are one assignment, and props drop when the puppet loses balance
  (`mvp-prior-art-2.puppetmaster-prop-attachment`); aiming IK runs before the physics read so the muscles chase
  the aimed pose and physics, pain and will degrade it (`mvp-prior-art-2.ik-after-physics-aiming`). Lodur's
  grip is that model with the drop rule moved into drive state (pain in the hand, unconsciousness, overload
  of the weld's threshold). Aim error is not a parameter: it is the gap between the aim IK goal and where the
  will-weighted, tremoring, startled arm lands. Arma's suppression and fatigue sway
  (`mvp-prior-art-2.suppression-aim-degradation`, `aim-physiology-numbers`) are the games that fake that gap
  with a sway multiplier; here it emerges, which is what the ruling asks for.
- **Building is props plus constraints plus signals.** Garry's Mod composes from props and a fixed set of
  constraint tools and saves entities then constraints (`mvp-prior-art-2.gmod-constraint-duplicator`);
  People Playground, Besiege and Algodoo add activation signals, sensors, timers and logic
  (`mvp-prior-art-2.algodoo-ppg-besiege-signals`). BDS levels are that triple as CultCache: objects (with
  behaviours as content), joints (Ymir kinds, `ymir-joint-kinds`), wires; load creates objects before
  joints, as the duplicator's paste does. No source reached says how those games' shipped levels were built;
  the ruling's "arenas with the player's tools" is ours.
- **Startle moves up.** The new ruling makes distraction a will drain, so `lodur:follow_up:startle-reflex`
  becomes cut `lodur-startle-attention`. It did not land right after rung 4: `lodur-will-mix` landed first (it needs only the `Will` slots, which read 1), so startle now depends on it.

## Cut order

Agent pace: S is one Hands session plus Soul, about 1-2 h; M about 3-4 h; L about 6 h. Ymir runs in parallel
with the Lodur rungs (different repo); Lodur cuts serialise on `Actor.cs`; BDS waits on the client pass.

| # | Cut | Repo | One line | Depends on | Size |
|---|---|---|---|---|---|
| 1 | `ymir-contact-facts` | Ymir | contact/hit facts, contact-force lane, material ids | ymir-shape-lanes (parallel client pass, ABI 3) | M | landed |
| 1' | `ymir-grab-anchor` | Ymir | kinematic anchor, motor joint, impulse, raycast | ymir-shape-lanes (sibling of 1) | S-M | landed |
| 2 | `ymir-break-and-remove` | Ymir | thresholds, overload facts, remove body/joint, tombstones, removal facts | 1 | M | open |
| - | `rung3-sensory-load` Soul, `rung4-goal-flip`, `rung3-office-stress`, `rung4-office-flip` | Lodur/BDS | landed (rung 3 `a048827`, rung 4 the Brain `c26c15c`; the office arc and the chamber release in BDS) | as admitted | - |
| - | `client-ready-sim`, `godot-client-v1` | BDS/Godot | landed (`BodyPainBoxSession`; the Godot client at `client/`) | its own | - |
| 3 | `lodur-will-mix` | Lodur | voluntary layers weighted by will; PoseBehaviour demoted | rung4-goal-flip | M, landed (`afb8df3`, with the recurring break `74883f8`; goal entry effects `c93bdb7`) |
| 4 | `lodur-physiology` | Lodur | part tree content, HP, wounds, blood, pain, shock, consciousness, capacities, death | 3 (landed) | L (split core/binding if over budget) |
| 5 | `lodur-touch` | Lodur | per-part touch from contact facts, external-joint pull; insults from material table | 1, 2, 4 | M |
| 6 | `lodur-startle-attention` | Lodur | startle reflex on onsets; attention scales will | 3 (it writes the Attention slot the will mix already reads) | M, open |
| 7 | `lodur-ik-goals` | Lodur | IK goals in body interface; two-bone arm/leg, look, aim | 3 | M |
| 8 | `lodur-reflex-set` | Lodur | flinch-from-touch, brace | 3, 5 | M |
| 9 | `lodur-sever-impair` | Lodur | severing via binding, impairment states | 2, 4, 5; Q `bds-dismember-trigger`, `bds-reattach` | M |
| 10 | `lodur-clips` | Lodur | pose-key clips as a voluntary layer | 3; Q `bds-mvp-clips` | S |
| 11 | `lodur-assist-pin` | Lodur | upright assist as a voluntary layer | 3; Q `bds-upright` | S |
| 12 | `lodur-grip` | Lodur | grip weld, drop rules, tool frame for aim | 2, 7 | M |
| 13 | `bds-sandbox-session` | BDS | session + command queue, grab/poke/cut | client-ready-sim, 1' | M |
| 14 | `bds-damage-content` + `bds-gore-content` | BDS | materials to insults; gore data | 5, 9; Q `bds-gore-look`, `bds-death` | M |
| 15 | `godot-runtime-bodies`, `godot-interaction`, `godot-blood` | Godot | lifecycle, input, blood | godot-client-v1, 2, 13, 14 | M each |
| 16 | `ymir-joint-kinds`, `bds-level-content`, `bds-contraption-signals`, `godot-build-tools` | mixed | building and arenas | 13 | M-L each |
| 17 | `bds-guns` | BDS | gun content aimed by IK | 7, 12, 1' | M |

Rough total: about 26 cuts, roughly 70-90 agent-hours; with Ymir and BDS/Godot running beside the Lodur chain,
the critical path is the Lodur chain 3 → 4 → 5 → 9 (and 3 → 7 → 12; startle, 6, is off it), about 30 agent-hours after rung 4 lands.
The playable MVP (grab, poke, cut, bleed, sever, reflexes, will) is cuts 1-9, 13-15.

Admitted 2026-11-20 (pass `bds-mvp-map`): specs `lodur:cut_spec:cut-ymir-contact-facts.r2`,
`cut-ymir-grab-anchor.r2` (siblings, after the parallel pass's `ymir-shape-lanes`), `cut-ymir-break-and-remove.r2` (after contact facts); every
later cut above is a `lodur:follow_up:<label>` of the same name (22), owner nibu; questions
`lodur:question:bds-*` (7); ideas `lodur:idea:bds-mvp-map.*` (22, seven rejected and resolved Recorded).

## Wound blood slice (pass `bds-slice-map`, 2026-11-27; mains Lodur `2359142`, Ymir `1d27114`, BDS `f26ec57`)

Probe: `<scratchpad>/nibu-imagination/probe/Program.cs` (Release, net10 roll-forward, paired interleaved medians of 9 against a no-fluid control).

- **B, bleed.** One Knife cut (Skin, Fat, Muscle at 60) bleeds 78.0 mL/s at peak and kills by BloodLoss at 38.1 s; 3 cuts 234 mL/s and 11.2 s; 6 cuts 468 mL/s and 5.5 s. Every bleed-out spills 2.50 L real, 25.0 L visible at gain 10, 1600 drops of 15.625 mL.
- **A, cost on the seated world.** Drops at three body points, settled when supported and under 0.05 m/s for 30 ticks: +0.076 ms a tick at 50 drops/s (live 42), +0.250 at 150/s (live 126), +0.562 at 300/s (live 256), at caps 512 to 2048 alike; wets 37-39, overflow 0. Cap-bound (only the cap settles): 512 live +1.49 ms, 1024 +3.9, 2048 +7.2. Settling carries the gain; the cap stays 512.
- **A, lane reuse.** A lane freed by Settle is free only after the next Step: settling only when emission finds the cap full refuses 795 of about 2,600 drops at cap 512. Settle after the Step, down to LiveCap - MaxDropsPerTick.
- **I, identity.** A SeatedPuppet world with particle capacities and no fluid steps byte-identically over 1200 driven ticks (hash `B3ACA960FE3B41E7` both); Ragdoll.Build places 2 airway sensors.

Order: `cut-bds-wound-blood.r1`, then `cut-bds-client-office.r1`, then follow-up `bds-drown-own-blood-slice` (its Lodur part, `lodur-airway-internal-bleed`, can run beside the client cut). Idea `bds-slice-map.gain-at-circulation-boundary` holds the aspirate-cough amplification hazard for that slice.

## Airway internal bleed (pass `internal-bleed-map`, 2026-11-27; mains Lodur `cb20b37`, Ymir `c635c17`, BDS `407319b`)

Probe: `<scratchpad>/imag-airway/probe` (a scratch copy of Lodur.Core at `cb20b37` with spec `cut-lodur-airway-internal-bleed.r1`'s rule; `Program.cs`, Release, net10).

- **P1, conservation.** Over every run (knife on Skin, Fat, Muscle plus a Larynx core of share 1; gains 1 and 10; vascularity 0.2 and 1), blood lost = skin bleed + airway bleed exactly (2500.2 = 2166.9 + 333.4 mL), and Coughed + Drained + AirwayLiquid = gain x airway bleed exactly (3183.6 + 150.0 = 3333.6 mL at gain 10), the overflow spill included.
- **P2, timing.** Larynx-only Cut 60 (vascularity 1): airway full (occlusion 0.99) at 0.3 s at gain 10, 31.1 s at gain 1; dies of BloodLoss at 53.7 s either way. Larynx-only Cut 20: full at 129 s (gain 10) and 147 s (gain 1), unconscious at 127.7 s both, dead 275-290 s by Consciousness: blood-loss stages, not hypoxia, take him first. With the knife's skin, fat and muscle also cut he is unconscious at 11-18 s and dead at 20-32 s, before the ruled 75 s asphyxia: a cut throat cannot drown him with today's knife (question `bds-throat-cut-bleed-band`).
- **P3, cost.** Physiology.Step on the humanoid with two knife wounds: 2.563 us base, 2.687 us with the rule and Larynx and Trachea added (paired interleaved medians of 7, +0.17 us).
- **Source reads.** Breathing in takes nothing from the world: `Ragdoll.ReadAirway` and `SeatedBody.ReadAirway` only read sensor lanes (idea `internal-bleed-map.drawn-liquid-stays-in-world`). Ymir creates and removes sensors at runtime (`Box3DSoaWorld.CreateSensors`, `Remove`) and destroys particles (`DestroyParticles`), but has no partial take from a wet patch. BDS's knife names Skin, Fat, Muscle and no airway tissue; the Humanoid has no larynx or trachea at `cb20b37`.

Gain owner: the caller (BDS `BloodParameters.Gain`), passed to `Physiology` at construction like `slowScale`; Lodur content holds none. The airway is in world units, so bled blood enters at the gain and leaves at 1x.

Order: `cut-lodur-airway-internal-bleed.r1` (beside `cut-bds-client-office.r1`), then follow-up `lodur-wound-reach` (Lodur), then `bds-throat-cut` (BDS, after client-office and question `bds-throat-cut-bleed-band`), then `bds-airway-expulsion`, then `bds-fella-air-hunger` (question `bds-air-hunger-struggle-look`). `lodur-airway-wound-openings` is off the slice's path.
