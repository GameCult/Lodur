# Lodur: target

Campaign `lodur`. The operator's rulings live in the eureka-state mind under
root `motion-pipeline` (the workshop ran there); this document holds the
rationale and the design truths the campaign will produce. Where it and a
ruling disagree, the ruling wins and this file is stale.

## What it is for

A character that reacts to forces and stimuli, and compensates for injury,
the way a person would: it pulls back from something hot, jumps when poked,
and crawls toward its objective on one leg if it wants that objective badly
enough. (`motion-pipeline:ruling:euphoria-stimulus-and-impairment`.)

The field has the physics and the balance. NaturalMotion's Euphoria shipped
active, physically simulated characters in GTA IV through Red Dead Redemption
2, and learned trackers (DReCon, ProtoMotions, MaskedMimic) follow arbitrary
motion while keeping their feet. What the field does not have, per the prior-art
pass, is the nervous system on top: senses that are not forces, reflexes layered
under deliberate behaviour with their own latencies, and a character whose will
competes with its pain. Euphoria's reactions are whole-body behaviours a game
starts; nothing in the record senses heat or a light touch, and nothing models
the twenty seconds in which a wounded character crawls, slows, and passes out.
That layer is Lodur. Evidence: `gamecult-ops/docs/research/euphoria-at-home-prior-art.md`
(sections 1-9 at `8320c18`, section 10 at `15d9a4e`).

Named after the god in Voluspa who gives the first humans blood, warmth and
good colour (`motion-pipeline:ruling:euphoria-middleware-named-lodur`).

## Layers

Signal flows one way per tick; each layer has one owner.

1. **Perception.** Contacts, impulses, temperature, proximity and named game
   stimuli become typed stimuli on body regions. A poke that barely moves the
   body is still a stimulus.
2. **Reflex.** Fast, local responses to stimuli (withdrawal, startle,
   bracing), each with a latency, overlaid on whatever the body is doing.
   Their gain is set from above by the drive, the way descending pathways
   modulate spinal reflexes: the Gom Jabbar case. Physiology sets the scale: withdrawal roughly 60-150 ms, voluntary
   reaction about 200 ms (figures `[M]` in the research doc; verify before
   they become constants).
3. **Drive.** Per-character state that evolves every tick: pain accumulates
   and decays, shock runs on a clock that blood loss speeds, adrenaline spikes
   on threat and fades; resolve is a trait, and the objective pulls. Static
   per-character values are this state with its dynamics frozen.
   (`motion-pipeline:ruling:euphoria-drive-is-dynamic-state`.)
4. **Behaviour.** The drive selects and weights behaviours (writhe, crawl to
   the objective, pass out, get up, the reflex overlay) that emit targets.
5. **Body.** Follows targets (poses, IK goals) with per-muscle strength on the
   host engine's articulated physics. v1 is GameCult's own PD-tracking active
   ragdoll and ships the ground behaviours; v2 is a small learned tracker
   (DReCon scale: 340 us per character per frame measured, 60 us of it the
   policy) for upright states, behind the same interface.
   (`motion-pipeline:ruling:euphoria-body-layer-order`.)

Layers 1-4 are the core: C# with no engine references, testable without an
engine. Layer 5 binds to the host: Unity `ArticulationBody` first; v2 inference
through the engine's runner (Unity Inference Engine, ONNX Runtime).
(`motion-pipeline:ruling:euphoria-first-consumer-sandbox-csharp-core`.)

## Invariants

- `core-engine-free`: the core assemblies reference no engine. A Unity type in
  the core is a defect, not a convenience.
- `body-one-interface`: the body consumes only targets and per-muscle strength.
  Drive, behaviour and reflex never set a joint, force or physics property
  directly; swapping the PD ragdoll for a learned tracker changes no layer above.
- `one-physics-world`: the body simulates in the host's physics world. No
  second world that the game's objects cannot touch.
- `reflex-gain-from-drive`: a reflex fires on its own shorter latency and
  overlays the running behaviour; it never waits for behaviour selection. Will
  acts on reflexes ahead of time, not after: drive state (resolve, the pull of
  the objective) sets each reflex's gain, so a resolved character can hold its
  hand in the box. Suppression is never free: the stimulus still feeds pain,
  and holding a reflex down shows as visible strain until resolve breaks.
  Deliberate behaviour takes over a fired reflex only after its own, longer
  latency. (`motion-pipeline:ruling:lodur-reflexes-suppressible-by-will`.)
- `drive-is-state`: what a hurt character does is decided by drive state
  evolving over time, never by a per-wound lookup.
- `bounded-tick`: per-character work per tick is bounded and allocates nothing
  in steady state. A middleware that hitches is unusable in a game.
- `content-is-cultcache`: persistent content (puppet and object definitions,
  drive parameters, saved levels in Bad Day Simulator) is CultCache, per the
  GameCult substrate doctrine. JSON is schema publication only.

## First consumer

Bad Day Simulator (`GameCult/BadDaySimulator`): a 3D sandbox in the spirit of
how People Playground plays, not its camera or UI. The player gets a level and
places puppets of various sorts and objects with set behaviours and
interactions. (`motion-pipeline:ruling:euphoria-sandbox-shape`,
`motion-pipeline:ruling:euphoria-sandbox-named-bad-day-simulator`.) It is a
separate repo so that Lodur's boundary has a real consumer from the first cut.

## Not in scope

- Adapting to arbitrary changes of body topology. The operator called it too
  ambitious; impairment is handled through the drive and the behaviour set.
- A Rust port of the core, until a first consumer outside Unity exists.
- The StreamPixels overlay as a consumer before StreamPixels M1 ships.
- Depending on RootMotion PuppetMaster (paid, not redistributable, and no
  balance control: research section 10.1).
- Hand-tuned balance controllers of the SIMBICON family as the upright path.

## Open before the first cut is mapped

The identity, lifecycle and authority table (one row per persistent kind) is
written before any cut; these feed it.

- **Impairment states.** Self's proposal, not ruled: impairment as a closed set
  of named body states (intact, missing a leg, missing an arm, both legs), with
  mirror symmetry; crawling as the first locomotion after any leg loss; one-leg
  hopping a stretch goal.
- **Rig.** Which humanoid the sandbox ships with, and its licence for a public
  repo. The StreamPixels Little Heroes Mega Pack is a paid asset and cannot be
  committed to a public repo.
- **Unity version** for Bad Day Simulator, and how Lodur's core is consumed
  (UPM package from the Lodur repo, or a built assembly).
- **Training host for v2.** Raven trains only when the household's GPU allows
  (`motion-pipeline:ruling:raven-gpu-defers-to-household`).
- **Trademark search** on "Bad Day Simulator" before anything public is
  announced.
