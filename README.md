# Lodur

A physics character middleware for characters that **feel and react**: they
pull back from something hot, jump when poked, and, if they want their
objective badly enough, crawl toward it after losing a leg.

**Status: layers 1-3 and a layer-4 stand-in run on a PD-muscle ragdoll on Ymir.** The
design is [`docs/lodur-target.md`](docs/lodur-target.md); read it first. `Lodur.Core`
holds perception, the withdrawal reflex, the drive state and a hold-against-reflex
stand-in, run per character by `Actor`; `Lodur.Body.Ymir` is the PD-muscle active ragdoll
on Ymir's Box3D SoA world ([`docs/first-slice.md`](docs/first-slice.md), measured and
unmeasured). Behaviour selection beyond the stand-in, impairment and the learned tracker
are not coded.

Entry points: `src/Lodur.Core/Actor.cs` (one character's nervous state, one tick per
physics step), `src/Lodur.Core/PdMuscle.cs` (engine-free PD law, strength per muscle),
`src/Lodur.Core/BodyPlan.cs` (a body as data: parts, joints that are the tree, sense points, airway openings),
`src/Lodur.Body.Ymir/Ragdoll.cs` (builds a `BodyPlan` into a Ymir world; `Drive`, `ReadSensePoints`, and `ReadAirway`, which reads the liquid sensors it places at the airway openings (live and pooled volume) and each opening's facing against the caller's down),
`tests/Lodur.Core.Tests` (the layers and parity with the pain box; builds without Ymir),
`tests/Lodur.Tests` (stand, fall, rest, same-seed-same-hash, and the connected pain box:
`ConnectedPainBoxTests`). The map this was cut from is
[`docs/connect-rungs-map.md`](docs/connect-rungs-map.md). Needs a sibling Ymir checkout at
`main` (the SoA joints merge, 2026-11-10, or later); `dotnet test tests/Lodur.Tests` builds
its native library with cmake.

## The problem

Active-ragdoll middleware (NaturalMotion's Euphoria, RootMotion's PuppetMaster)
and learned physics controllers (DReCon, ProtoMotions) solve the body: physics,
pose tracking and, in the learned case, balance. None of them models the nervous
system. They have no senses that are not forces, no reflexes layered under
deliberate behaviour, and no character whose pain competes with its will over
time. Lodur is that layer.

## Shape (planned)

- **Core** (C#, no engine references): perception, reflexes, a drive state of
  pain, shock, adrenaline and resolve that evolves every tick (the will breaks
  at zero resolve and mends as it recovers, and can break again), and behaviour
  selection that turns the drive into targets plus muscle strength, weighted by
  the will available to each voluntary pose.
- **Body** (per engine): follows those targets on the engine's own articulated
  physics. v1 is a PD-tracking active ragdoll on Ymir; v2 adds a small learned
  tracker for upright balance behind the same interface.

## First consumer

[Bad Day Simulator](https://eureka.gamecult.org/forge/GameCult/BadDaySimulator),
a 3D physics sandbox.

## Where it lives

Canonical: `https://eureka.gamecult.org/forge/GameCult/Lodur`. GitHub
`GameCult/Lodur` is a push mirror.

Prior-art research:
[`gamecult-ops/docs/research/euphoria-at-home-prior-art.md`](https://github.com/GameCult/gamecult-ops/blob/main/docs/research/euphoria-at-home-prior-art.md).
