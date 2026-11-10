# First slice: a PD-muscle ragdoll on Ymir

Bad Day Simulator ladder rung 1: a body that stands, falls and rests deterministically, read through
Ymir's structure-of-arrays world. Nothing above layer 5 exists yet.

## What is built

- `Lodur.Core` (`netstandard2.1`, no references): `Quat`, `MuscleGains`, `PdMuscle.Torque`. A PD law on one
  joint: the child's rotation relative to the parent and their relative angular velocity in, a torque out.
  Strength is a multiplier on every gain; zero is limp and returns exactly zero. Swing (joint frame x, y) and
  twist (z) have separate gains.
- `Lodur.Body.Ymir` (`net8.0`, references Ymir.Box3D): `HumanoidRagdoll` builds 13 bodies and 12 joints in a
  caller-owned `Box3DSoaWorld`: capsule limbs, box pelvis, torso and feet, a sphere head, ball joints at
  spine, neck, shoulders and hips, hinges at elbows, knees and ankles. `Drive(strengths, targets)` writes
  each muscle's torque onto the world's torque lanes (equal and opposite on child and parent). The strength
  array and the target array belong to the caller; nothing is copied or retained, and a tick allocates
  nothing (tested).
- Tests (`tests/Lodur.Tests`): the PD law's behaviour; stand, fall, rest; per-muscle strength; torque
  conservation; fractional strength scaling; no allocation; same seed, same state hash.

## Measured (Linux x64, Debug native build, one machine)

- At 120 Hz, with full strength and a random one-step shove from the seed (up to 30 N m per axis on pelvis and
  torso), the ragdoll holds head height 1.73 m (initial 1.74) through 1.5 s for seeds 1, 2, 3 (tested) and
  through about 2 s in the exploratory runs. It then topples: there is no balance controller, so a
  pose-holding PD ragdoll is an inverted pendulum with soft ankles.
- With zero strength it falls (head under 0.6 m within 1 s) and its trunk comes to rest within 6 s for seeds
  1, 2, 3 (trunk under 0.05 m/s and 0.2 rad/s). Extremities can keep rocking: a head resting on an arm
  chattered at up to about 3.5 rad/s for 10 s in one seed. The test bounds it (under 0.6 m/s, 6 rad/s); it
  does not claim every body is still.
- Same seed gives the same FNV-1a hash of every body and joint lane at 14 checkpoints, in one process. A
  different seed, or one strength element changed, gives a different hash.

## Not measured, or limits found

- Cross-platform bit-identity: unclaimed. Windows build and Unity consumption: unrun. One machine, Debug
  native build only; no Release run, no run in a second process.
- Explicit PD through torque lanes limits stiffness and damping: damping times the step must stay under the
  inertia it acts on. Hips and spine went unstable at damping 15 and 20 and are set to 3 and 4; the gains
  are tuned by running, not derived. Stiff ankles (what balance needs) need joint springs or motors inside
  the solver, which Ymir's SoA world does not expose yet.
- A bare sphere rolls on forever, so the head takes a rolling resistance (0.1).
- Hands, a rig licence question, impairment, contact facts (pain input), body removal: not built.
