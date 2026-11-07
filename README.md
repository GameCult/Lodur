# Lodur

A physics character middleware for characters that **feel and react**: they
pull back from something hot, jump when poked, and, if they want their
objective badly enough, crawl toward it after losing a leg.

**Status: design plus a first slice of layer 5.** The design is
[`docs/lodur-target.md`](docs/lodur-target.md); read it first. What runs today is a
PD-muscle active ragdoll on Ymir's Box3D SoA world: [`docs/first-slice.md`](docs/first-slice.md)
(measured and unmeasured). Layers 1-4 (perception, reflex, drive, behaviour) are not
coded.

Entry points: `src/Lodur.Core/PdMuscle.cs` (engine-free PD law, strength per muscle),
`src/Lodur.Body.Ymir/HumanoidRagdoll.cs` (the ragdoll and `Drive`), `tests/Lodur.Tests`
(stand, fall, rest, same-seed-same-hash). Needs a sibling Ymir checkout on branch
`nibu/soa-joints`; `dotnet test tests/Lodur.Tests` builds its native library with cmake.

## The problem

Active-ragdoll middleware (NaturalMotion's Euphoria, RootMotion's PuppetMaster)
and learned physics controllers (DReCon, ProtoMotions) solve the body: physics,
pose tracking and, in the learned case, balance. None of them models the nervous
system. They have no senses that are not forces, no reflexes layered under
deliberate behaviour, and no character whose pain competes with its will over
time. Lodur is that layer.

## Shape (planned)

- **Core** (C#, no engine references): perception, reflexes, a drive state of
  pain, shock, adrenaline and resolve that evolves every tick, and behaviour
  selection that turns the drive into targets plus muscle strength.
- **Body** (per engine): follows those targets on the engine's own articulated
  physics. v1 is a PD-tracking active ragdoll in Unity; v2 adds a small learned
  tracker for upright balance behind the same interface.

## First consumer

[Bad Day Simulator](https://eureka.gamecult.org/forge/GameCult/BadDaySimulator),
a 3D physics sandbox.

## Where it lives

Canonical: `https://eureka.gamecult.org/forge/GameCult/Lodur`. GitHub
`GameCult/Lodur` is a push mirror.

Prior-art research:
[`gamecult-ops/docs/research/euphoria-at-home-prior-art.md`](https://github.com/GameCult/gamecult-ops/blob/main/docs/research/euphoria-at-home-prior-art.md).
