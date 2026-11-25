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

1. **Perception** (`Perception`, `NerveParameters`, `BoxStimulus`, `Emitter`). Contacts, impulses, temperature,
   proximity and named game stimuli become typed signals on body regions. Input: one point per body region
   (from the body binding, read-only) and the game's stimulus sources. Two source kinds today: noxious box
   volumes with an intensity the game writes each tick, and point emitters on the audio, visual and interrupt
   channels (position, radius and intensity written each tick, falling off linearly to the radius), sensed at
   one region and combined into one load, `1 - prod(1 - afferent)`. An emitter may carry an `Id` (the game's
   stable id; 0 is anonymous and only loads the nerve) and a `Kind` (the game's tag, never interpreted by Lodur).
   Perception keeps a bounded percept memory (`PerceptParameters`, `Percept`): one slot per identified thing
   strictly inside an emitter's radius, with its kind, the position of the emitter that reached it hardest,
   salience (that reach times the sensory gain) and seconds since sensed, forgotten after a memory time. The top
   id bit (`PhantomIdBit`) is reserved to Lodur's phantoms and `Sense` refuses it from the game. Percepts also remember the thing's `Facts`
   (a `ulong`, bit i is fact i) as last sensed; the game owns every bit but 63 (`InReachFact`), which is Lodur's and which `Sense` refuses, like `PhantomIdBit`. Output per region: the stimulus, and
   the afferent signal after threshold, sensitisation and conduction delay; and the load, and the percepts. A poke that barely moves the body
   is still a stimulus.
2. **Reflex** (`WithdrawalReflex`, `WithdrawalParameters`). Fast, local responses to afferent signals
   (withdrawal now; startle and bracing later), each with a latency, overlaid on whatever the body is doing.
   Each reflex's gain is set ahead of time by the drive, the way descending pathways modulate spinal reflexes
   (the Gom Jabbar case): a reflex reads the drive state of the previous tick, never waits for behaviour.
   Output: the reflex command that leaks past suppression, the strain load of holding the rest down, and
   flinch events. Physiology sets the scale: withdrawal roughly 60-150 ms, voluntary reaction about 200 ms
   (figures `[M]` in the research doc; verify before they become constants).
3. **Drive** (`DriveState`, `DriveParameters`). Per-character state that evolves every tick: pain from
   afferent input, strain from suppressed reflexes, arousal from sensory load; resolve drained by all three and
   scaled by the active goal's pull, recovering only when pain and arousal are both low;
   later shock on a clock that blood loss speeds and adrenaline that spikes on threat. Resolve reaching zero
   breaks the will, which releases every suppression; the break is an event that counts (`Breaks`) and recurs:
   the will mends when resolve climbs back to `WillMendsAt`. `Will` is what is available to hold a reflex down
   and to power a voluntary layer: resolve times `Attention`, capped by `Consciousness` (both slots that read 1
   until startle and physiology fill them). Static per-character values are this state with its
   dynamics frozen. (`motion-pipeline:ruling:euphoria-drive-is-dynamic-state`.)
4. **The Brain.** The consumer's authored goals (a behaviour, a priority, a pull in [0, 1] that defaults to 0, no pull (the former objective pull
   defaulted to 0.5), an optional minimum dwell, conditions over the drive
   state), selected after a deliberate latency: the eligible goal of highest priority is wanted, and the
   Actor commits it. Lodur has no flip rule of its own; every rule is Brain data
   (`lodur:ruling:npc-brain-authors-goals`). One behaviour, `PoseBehaviour`: a pose held or a loop of pose keys,
   blended toward a flinch pose by the reflex leak and shaking with strain, emitting targets and per-muscle
   strength into caller-owned arrays and nothing else. Commitment is soft: a goal with a minimum dwell, counted
   in ticks from the tick it took over, is left for a goal that ties or ranks below it only once the dwell has
   passed, while a strictly higher-priority goal preempts during the dwell (after the deliberate latency); the
   return trip from an entered goal is gated by that goal's own dwell, which is what bounds chatter at a threshold.
   A goal also carries an entry effect on the drive (`EntryResolve`: the tick it takes over, resolve is set, which
   is how rage resets the will), an `Effort` (resolve drained per second to sustain it) and `InvoluntarySeconds`
   (an onset that runs without will and without paying the effort; +infinity is involuntary throughout)
   (`lodur:ruling:rage-involuntary-onset-then-voluntary`). The will mix: a voluntary goal writes each muscle at
   `HoldStrength x min(1, Will^WillCurve + leak x FlinchGain)` (the Actor passes `Will^WillCurve` as the weight), an involuntary one at `HoldStrength`, so a failing
   will slackens the layer while a reflex that gets past it still moves the muscle
   (`lodur:ruling:bds-sandbox-arenas-will-economy`).
   A goal can also carry a sensory gain and an arousal target (`SensoryGain`, `EntryArousal`), reached linearly over `EntrySeconds`
   from their values at takeover (0, the default, is at the takeover tick; the Start goal's apply at construction). The gain multiplies the sense
   channels' afferents as they leave the conduction delay, so sensitisation still follows the world; `Load` is built from the gated afferents and
   `ChannelStimulus` stays the world's. Sense reads the gain at the start of each tick, so a change of gain reaches `Load` one tick after the Actor sets it;
   the ramp's last tick assigns the targets exactly. The nociceptive region afferents, the withdrawal reflex and its flinch are never gated. The Actor owns the gain
   in force (`DriveState.SensoryGain`) and, through the ramp, arousal; after it the drive owns arousal again. Calm uses it to silence the office's
   stressors and drain the stress bar, tailing off over a few seconds rather than cutting
   (`lodur:ruling:stress-bar-shows-arousal-calm-silences`, `lodur:ruling:flinch-tail-after-stimulus`).
   A built Brain owns copies of its data and reads back as read-only views. Considerations extend it.
5. **Body.** Follows targets with per-muscle strength in the game's physics world. v1 is GameCult's own
   PD-tracking active ragdoll on **Ymir**, GameCult's boundary around a pinned Box3D, read and driven through
   Ymir's structure-of-arrays world in place (`lodur:ruling:lodur-body-on-ymir`): `Lodur.Body.Ymir`. It owns
   the puppet's shapes, joints and gains and reports body-region points upward to perception. v2 is a small
   learned tracker for upright states (DReCon scale: 340 us per character per frame measured, 60 us of it the
   policy) behind the same interface. (`motion-pipeline:ruling:euphoria-body-layer-order`.)

`Actor` runs layers 1-4 for one character in that order once per physics step, from caller-owned inputs into
caller-owned outputs, and reports its events (flinch, will broke, goal changed, died) as flags.

### Who writes what, per tick

One order inside `Actor.Tick`: the Physiology steps and writes Consciousness (an actor with a physiology; a dead one
goes limp and ends the tick) → Perception senses (with the sensory gain in force) → each reflex steps on its
afferent (gain from the previous tick's drive) → the drive steps (pain, strain, arousal, resolve, the break and
the mend; the goal's effort is paid here) → the Brain decides (a decision is pending for the deliberate latency)
and a due decision commits (entry resolve is set) → the entry ramp assigns gain and arousal → the goal's
behaviour writes strengths and targets at the will's weight, and the strengths are scaled by the motor ceiling. The tick that
commits a goal already writes under it.

| Owner | Inputs | Outputs | Derived state | Forbidden writers |
|---|---|---|---|---|
| `Perception` | region points, the game's stimulus sources and emitters, the sensory gain passed in | per-region stimulus and afferent, per-channel stimulus and afferent, `Load`, the percept slots | sensitisation, the conduction rings, percept salience and slot occupancy; the channel afferents are gated, the stimuli are not | the drive or a goal writing an afferent; the game setting `PhantomIdBit` (`Sense` refuses it); the Brain or a session writing a percept; a gain outside [0, 1] (`Sense` refuses it, NaN and infinity included); gating a region (nociceptive) afferent |
| Reflex (`WithdrawalReflex`) | its region's afferent and the drive's `Will` (the pull acts through resolve's drain, not here) | leak, strain load, flinch | none beyond its latency ring | the Brain or the behaviour setting a reflex gain |
| Drive (`DriveState`) | afferent sum, strain load, `Load`, the goal's pull and effort | pain, strain, arousal, resolve, `Breaks`, `Will` (= min(resolve x attention, consciousness)), the break and mend events | `Will` is derived and never stored; the break recurs (it mends at `WillMendsAt`) | the Brain, a session or the body writing resolve, arousal or `Will`; the one sanctioned jumps are `Enter` (a goal's entry resolve) and the Actor's entry ramp (arousal) |
| Attention slot | nothing yet: reads 1 (startle is the writer to come) | a factor of `Will` | none | anything but the startle reflex (`lodur-startle-attention`); the Actor's test seam `SetAttention` is internal |
| Consciousness slot | `Physiology.Consciousness`, written by the Actor each tick; 1 for an actor with no physiology | the cap of `Will` | derived from the physiology each tick | anything but the Actor reading its Physiology; no test seam: tests lower it through a Physiology |
| `Physiology` | its `BodyPlan` and `PhysiologyPlan` (tissue stacks, joined to the `BodyPlan` by `PhysiologyPlan.Resolve`, the one (label, side) join; a stack also carries thickness and sheet material that Physiology does not read; afflictions with data effects, blood) at construction; `Afflict` (the one entry for harm); `Step(dt)` from the Actor; the slow scale at construction | capacities (`Consciousness`, `BloodPumping`), `Bleed` per part, `BloodVolume` and `BloodLoss`, `MotorCeiling` per joint, `Integrity` per tissue, `Dead`, `Cause`, `Refused` | integrity (from Injures afflictions: part HP is never stored), capacities, bleed and the ceiling, recomputed each step; death is judged once at the end of a step (vital tissue, blood loss, consciousness: `lodur:ruling:bds-death`) and is permanent | anything else writing tissue, blood or the slot; a game writing integrity, volume or severity (harm enters through `Afflict`); an effect writing state; an affliction killing by its own severity; tissue, organ or affliction labels outside content (`PhysiologyPlans`) |
| Brain (`Brain`, `GoalSpec`) | the drive state it reads through conditions | the wanted goal; each goal's pull, priority, dwell, entry effects (`EntryResolve`, `EntryArousal`, `SensoryGain`, `EntrySeconds`), `Effort`, `InvoluntarySeconds` and behaviour | none: it is data, copied at build and read back read-only | a flip rule anywhere else (`lodur:ruling:npc-brain-authors-goals`) |
| `Actor` | all of the above, one tick | the committed goal; `DriveState.SensoryGain`; arousal during a goal's `EntrySeconds`; the events | the pending decision and its tick, the dwell, the ramp's start values, the onset in whole ticks | a consumer setting the goal or the gain from outside |
| Behaviour (`PoseBehaviour`) | the weight (the will's `Will^WillCurve` once the goal's involuntary onset is over, else 1), the leak, strain, time | strengths and targets into caller arrays: `HoldStrength x min(1, weight + leak x FlinchGain)` | none | writing a joint or a force (`body-one-interface`); any strength not under the will mix |
| `Planner` | percept facts and positions, self facts, the verb list (`Verb`: kind filters, needs, gives), a depth, the reach | `Plan`'s length (0 holds, n steps, -1 none within depth or within the 4096-grounding `NodeBudget`, a counter) and the steps as (verb index, This id, Tool id) in the caller's buffer | reach (bit 63) from distance, per call; the plan, per call | reading phantom-ness or a kind's or fact's meaning; storing a plan; the game supplying bit 63; allocating after construction |

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
  the pull of the active goal) sets each reflex's gain, so a resolved character can hold its hand in the box.
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
- `one-severance-owner`: a part is damaged, disabled or severed only by Lodur's physiology. Ymir reports
  contacts and joint stress and removes a joint only on command; no physics threshold removes anything by
  itself.
- `will-is-the-budget`: every voluntary motor layer (hold, clip, IK reach, look, aim, grip) is weighted by
  available will (resolve times attention, capped by consciousness); reflexes act against it with their own
  gains; nothing writes targets outside that mix. A distraction such as a startle lowers attention, and so the
  will every layer can spend (`lodur:ruling:bds-sandbox-arenas-will-economy`).
- `sandbox-is-content`: in Bad Day Simulator an arena is a level built from the commands the player has; no
  engine path exists only for an arena; levels are CultCache.

## First consumer

Bad Day Simulator (`GameCult/BadDaySimulator`): a 3D sandbox in the spirit of how People Playground plays,
not its camera or UI. The player gets a level and places puppets of various sorts and objects with set
behaviours and interactions. (`motion-pipeline:ruling:euphoria-sandbox-shape`,
`motion-pipeline:ruling:euphoria-sandbox-named-bad-day-simulator`.) Its one physics world is Ymir's. It is a
separate repo so that Lodur's boundary has a real consumer from the first cut. Its first item is the pain box,
the Synaptic Induction Chamber (`lodur:ruling:bds-pain-box-item`,
`lodur:ruling:bds-pain-box-named-synaptic-induction-chamber`).

## Not in scope

- Adapting to body topology outside the authored part tree (new limbs, re-rooted trees, morphology-agnostic
  learned control). Losing a subtree of the authored tree is in scope as a closed set of impairment states per
  limb slot (intact, impaired, disabled, severed), mirror-symmetric; the behaviour set compensates for each
  (`lodur:ruling:bds-mvp-scope`).
- Weapons, ballistics, gore and wound presentation: Bad Day Simulator content, not Lodur.
- A Rust port of the core.
- The StreamPixels overlay as a consumer before StreamPixels M1 ships.
- Depending on RootMotion PuppetMaster (paid, not redistributable, and no balance control: research
  section 10.1).
- A hand-tuned balance controller of the SIMBICON family as the upright path. What holds the body up is the PD
  muscles plus a will-scaled assist (and later a learned tracker). Stepping strategies that only re-plan targets
  (capture-point foot placement after a shove, stumble, brace, get up) are in scope as behaviours.

## Open

- **The MVP map** (`docs/bds-mvp-map.md`): systems by owner, authority map, model page and cut order for
  grab, poke, physiology, dismemberment, will-weighted animation and IK, and sandbox levels.
- **How Bad Day Simulator consumes Lodur** (`lodur:question:bds-consumes-lodur`).
- **The pain measure** (`lodur:question:pain-sweep-measure`).
- **Ymir gaps Lodur needs next**: contact facts, joint stress and removal, a grab anchor and impulses are
  specced (`lodur:cut_spec:cut-ymir-contact-facts`, `cut-ymir-grab-anchor`, `cut-ymir-break-and-remove`);
  joint springs for stiff ankles remain (`nibu-body:follow_up:ymir-soa-lodur-needs`).
- **Renderer**: a Godot client (`lodur:ruling:bds-client-godot`), specced as `lodur:cut_spec:cut-godot-client-v1`.
- **Training host for v2.** Raven trains only when the household's GPU allows
  (`motion-pipeline:ruling:raven-gpu-defers-to-household`).
- **Trademark search** on "Bad Day Simulator" before anything public is announced.
