# Fella's goal-following AI (map)

Campaign `lodur`, pass `goal-ai-map`, 2026-11-25. Serves `lodur:ruling:bds-fella-needs-goal-ai`: Fella follows
simple goals, moving towards or away from things and interacting with objects. Read with
`lodur:ruling:bds-hallucination-shown` (hallucinations alter his reality and drive behaviour by type),
`lodur:ruling:bds-graft-control-by-wiring`, `lodur:ruling:npc-brain-authors-goals`,
`lodur:ruling:lodur-locomotion-rosen-style` and `lodur:ruling:bds-mvp-clips`. Where this file and a ruling
disagree, the ruling wins.

## Bases

Lodur `fbc3501`, BDS `3d6e735`, Ymir `d82da6d` (origin mains, fetched 2026-11-25).

## Body facts (probes and reads at base)

1. `Brain` (`src/Lodur.Core/Brain.cs`) is goals selected by eligibility (conditions over `DriveState`) then
   priority, committed by the Actor after the deliberate latency. A goal has exactly one behaviour, a
   `PoseBehaviour`, and **no target**: nothing in a goal names a thing in the world.
2. `Perception` (`src/Lodur.Core/Perception.cs`) reduces the world to intensities: noxious boxes per region, and
   point `Emitter`s (channel, position, radius, intensity) reduced to one stimulus per channel at the sense
   region, then one `Load`. An emitter has **no identity**: the puppet cannot tell two sounds apart, or know
   where anything is. There is no percept of an object.
3. No IK exists in the code (`grep -rniE "\bIK\b|reach|two-bone" src` finds nothing); `lodur-ik-goals` is a
   follow-up. The body interface is per-joint strength plus target rotation (`Ragdoll.Drive`).
4. The puppet cannot stand: every BDS body run builds through `SeatedPuppet.Build`, which welds the pelvis to a
   stool (`BDS src/BadDay.Ymir/SeatedPuppet.cs:24-30`). `lodur-assist-pin` (the will-scaled upright assist the
   Rosen ruling names) is a follow-up, unbuilt.
5. BDS authors two Brains, `OfficeBrain` and `ChamberBrain`; neither has a target. Emitters are built in BDS
   `Setting.cs` and `StressSession.cs` with no id.
6. `dotnet test tests/Lodur.Core.Tests` at base: 188 passed, 0 failed (2026-11-25).

## Prior art (research pass `goal-ai-prior-art`)

- The Sims: needs-based utility; objects advertise what they offer, so the data lives on the object and new
  objects need no character code. Fits "interact with objects in various ways" and `sandbox-is-content`.
- F.E.A.R. (Orkin, GDC 2006): three states (Goto, Animate, UseSmartObject) plus a STRIPS-like A* planner over
  actions. The three states are the motor vocabulary we need; the planner is not (puppets do not plan
  multi-step tactics, and planners are hard to author and to read).
- RimWorld: a priority think tree picks a job; a job driver runs a fixed list of toils (go to, reserve, wait,
  interact). The tree is what `Brain` already is (priority plus conditions); toils are the shape of an
  interaction.
- Utility AI (Dave Mark's infinite-axis system): normalised considerations through response curves, multiplied.
  The way `Brain` grows (`lodur:idea:brain-map.brain-extends-to-utility-and-will-mix`), now over (goal, target).
- Gang Beasts: virtual balancing forces not tied to ground contact, and a developer note that the control
  mixes the Overgrowth talk with IK and PID on a fully physical ragdoll. Precedent for the will-scaled assist
  carrying the body while gait poses and foot IK supply the look.
- Euphoria (GTA IV): behaviours run on a simulated body; reachable sources give no behaviour API. Nothing to
  copy below the behaviour level beyond what Lodur already has.

## Target shape

Signal per tick stays one way: perception → reflex → drive → Brain → motor layers → body.

**Percepts (layer 1).** An emitter gains an `Id` (the game's stable id; 0 is anonymous and only loads the
nerve) and a `Kind` (the game's tag; Lodur never interprets it). Perception keeps a bounded percept memory:
one slot per thing sensed (id, kind, last position, salience, seconds since sensed), forgotten after a memory
time. Real and phantom things are **one stream**: a hallucination is a thing in the puppet's reality, sensed
like any other, and nothing the Brain reads tells them apart. Phantom ids carry the top bit, reserved to
Lodur; `Sense` refuses a caller emitter that sets it, so the game cannot forge one and Lodur's hallucination
owner (physiology and substances, `lodur-phantom-perception`) is the only writer.

**Goals with targets (layer 4).** A goal may name a target: a kind filter over percepts. Selection scores each
(goal, percept) pair, Priority times response-curve considerations (distance, salience, drive quantities),
with a commitment bonus on the current pair. Conditions stay eligibility gates. A goal with no target scores
its Priority alone, so OfficeBrain and ChamberBrain select exactly as now.

**Motor layers (layer 4 to 5).** A goal's behaviour becomes a set of voluntary layers, each weighted by will:
pose (exists), look (head IK at the target), reach (arm IK to the target), ward off (arms between head and
target), lean or recoil (pose blend by the target's side), and, once the body can stand, locomotion (a desired
ground velocity toward or away from the target). The hallucination behaviours of the ruling are Brain data
over phantom kinds: flee is avoid, the attractive phantom is approach, fight is approach plus strike, ward
off is a layer, stare at hands is look plus reach at a phantom attached to his own hand, fetal is a pose goal
eligible when threat salience is high and no avoid target is reachable.

**Locomotion is its own chain**, after the orienting goals: the assist pin (pelvis and torso, will times
consciousness), standing (BDS drops the stool weld), a Rosen walk gait (pose keys timed by speed, foot IK),
then steering (seek, flee, arrive) that writes the desired velocity the assist carries. Crawl and hop follow
as impairment gaits.

**Interactions** are advertised by objects (BDS content): a verb, an interaction point and a short step list
in the RimWorld toil shape (go to, face, reach, grip, pose, wait, release). Lodur runs the steps; BDS authors
them.

**Grafts.** Every motor layer writes per joint, so a wired brain's authority is a per-joint mask; two brains on
one joint contest by will. Nothing in the cuts below may write a body-wide output that a mask could not split.

## Authority map

- Owner of what the puppet perceives: `Perception` (the game supplies emitters; Lodur decides what is sensed
  and remembered). Owner of what is real to him: the same; phantom-ness is not an input the Brain reads.
- Owner of which goal and which target: `Brain` data, committed by the `Actor`.
- Owner of motion: the will mix over voluntary layers; reflexes act against it.
- Forbidden writers: the game writing a phantom (top-bit id), a Brain reading phantom-ness, BDS setting a goal
  or target from outside, any layer writing a joint or force (`body-one-interface`), a kinematic root move.
- Derived: salience, the target slot, the thought-bubble type (from the phantoms in memory), desired velocity.
- Shared paths: a phantom and a real thing go through the same `Sense`; a scenario Brain and a sandbox Brain
  are the same data; a player-placed lure and an arena's lure are the same object content.

## Cut order

| # | Cut | Repo | Builds | Waits on | Size |
|---|---|---|---|---|---|
| 1 | `lodur-percepts` | Lodur | emitter id and kind, percept memory, phantom id bit | none | S, specced |
| 2 | `brain-goal-targets` | Lodur | target filters, (goal, percept) utility, commitment | 1 | M |
| 3 | `lodur-ik-goals` | Lodur | Look, ArmL, ArmR (legs later) in the body interface | none (parallel to 1-2) | M |
| 4 | `lodur-orient-goals` | Lodur + BDS | look, reach, ward off, recoil, fetal and stare-at-hands as layers; seated Fella demo | 2, 3 | M |
| 5 | `lodur-phantom-percepts` + `bds-thought-bubble` | Lodur, BDS | phantom things by hallucination type; the bubble reads their kinds | 1, 4 | M, S |
| 6 | `lodur-assist-pin` | Lodur | upright assist | none | S |
| 7 | `lodur-stand` | Lodur + BDS | standing puppet scene, stool weld dropped | 6 | S |
| 8 | `lodur-walk-gait` | Lodur | Rosen walk, leg IK | 3, 7 | M |
| 9 | `lodur-steering` | Lodur | approach, avoid, arrive into desired velocity | 2, 8 | M |
| 10 | `lodur-grip`, then `lodur-interactions` | Lodur + BDS | advertised verbs as step lists | 9, Q `bds-sandbox-lures` | M each |
| 11 | `lodur-intelligence` | Lodur | what intelligence scales | 2, Q `bds-intelligence-in-goal-ai` | S |

Cut 1 is `lodur:cut_spec:cut-lodur-percepts.r1`; the rest are follow-ups.

## What intelligence changes

Open as `lodur:question:bds-intelligence-in-goal-ai`. Against this AI, intelligence can scale three
things: quickness (deliberate latency and dwell), judgment (how sharply selection prefers the best pair; a dim
puppet picks among good options at random, as the Sims do), and memory (how long an unsensed thing stays
known). Recommended all three from one scalar.
