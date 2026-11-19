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
   one region and combined into one load, `1 - prod(1 - afferent)`. Output per region: the stimulus, and
   the afferent signal after threshold, sensitisation and conduction delay; and the load. A poke that barely moves the body
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
   scaled by the objective's pull, recovering only when pain and arousal are both low;
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
caller-owned outputs, and reports its events (flinch, will broke, withdrew) as flags.

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
- Hand-tuned balance controllers of the SIMBICON family as the upright path.

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
