# Flesh on the bones: map

Pass `flesh-bones-map` (Imagination, 2026-11-25). Pinned to Lodur fbc3501, Ymir d82da6d, BadDaySimulator 3d6e735.
Rests on rulings bds-particle-tissues, bds-flesh-at-rest, bds-body-skeleton-particle-flesh-rough, bds-skeleton-19-for-mvp,
bds-cut-location, bds-gut-length, bds-gut-rope-tension, bend-limit-stiffness, ymir-sheet-sieve-at-budget-substeps,
particles-are-constraint-graphs, and on the ideas of pass bds-body-flesh-map (2026-11-23), which this map measures and corrects.

## Target shape

Fella is 19 Ymir rigid bones (the plan's parts). Each part's flesh is one skin sheet and, on the six muscled parts, one muscle
sheet: Ymir particles, distances and bends, each particle attached to its own bone at its rest point. Sheets of neighbouring
parts are stitched across each joint by distances. At rest a part's flesh is dormant: in no Ymir loop at all, drawn from the
bone pose. Lodur's binding wakes one part when that part is disturbed and pins it again when it settles. A wound removes the
sheet edges it crosses; a sever at a joint removes that joint's stitches with the joint. Fat and bone are thicknesses in the
part's tissue stack, not particles. Internal tubes and organs stay meshes until released (follow-ups lodur-organ-release-rope,
lodur-exposed-tube-chains).

### Tissue layers per part (plan data)

| Part (label, side) | Skin sheet | Muscle sheet | Stack only |
| --- | --- | --- | --- |
| Pelvis, Torso | yes | yes | fat, bone; organs contained |
| Head | yes | no (scalp over skull) | fat, bone; brain contained |
| UpperArm, LowerArm, Thigh, Shin (L, R) | yes | yes | fat, bone; vessels, tendons contained |
| Foot, Palm, Thumb, Fingers (L, R) | yes | no | bone, tendons contained |

Authority: the BodyPlan owns each part's tissue stack (layer kind, thickness, and for a sheet layer: spacing, edge and
attachment compliance, tensile strength). Lodur.Core derives the sheet geometry (rest points in the part frame, edges, bends,
joint stitches) from the part's shape and stack, deterministically, at build. Derived geometry is never stored: a stored copy
would be a cache that could disagree with the stack. The plan lives in BodyPlans.Humanoid until lodur-body-plan-cultcache.

## Authority map

- Owner of flesh meaning (which part, which layer, awake or dormant, wound, sever): Lodur's binding (Lodur.Body.Ymir), from
  plan data and physiology insults.
- Owner of flesh physics (positions, constraint solve, tension and attachment-force sensors, components): Ymir.
- Inputs to the binding's wake rule: contact impulse lanes on the part's bone, insults on the part, wounds, severs,
  evisceration; for re-pinning, attachment force times compliance (the deviation; no new lane).
- Outputs: Ymir commands (create in batches, set pinned, remove distances, destroy particles); Tear insults from DistanceTension.
- Derived: sheet geometry (from the stack), render embedding weights (BDS client, at load), components and tethers (Ymir),
  the active lists of the pin cut (Ymir, at command time), the per-particle held-body list and limb skip (Ymir, at command time).
- Forbidden writers: Ymir never wakes, pins, tears or splits on its own; BDS never commands flesh except through the session;
  no per-tick writes of dormant particles.
- Demotions: "pinned = inverse mass 0 in every constraint" (follow-up ymir-particle-pinning as admitted) is no longer the
  mechanism; dormant particles leave the loops. held_by's scan over every attachment is no longer an owner; the held list is
  derived at command time.

## Body facts (probes)

Probe sources: `<scratchpad>/imag-flesh/fella.c`, `sieve.c`, and a copy of Ymir d82da6d `ymir_particles.c` with opt-in probe
patches (pinned skip flags, a body group filter in place of held_by, flesh out of the solid contact grid, phase timers; the
unpatched paths are byte-for-byte the shipped code). gcc 13.2 -O2 -ffp-contract=off, one core, 120 Hz x 4 substeps. Bones are
kinematic proxies driven by forward kinematics of BodyPlans.Humanoid (sways at 3 rad/s); Box3D's own step is not included.

P1 Counts per Fella, sampler at skin 10 cm / muscle 14 cm with a ring floor of 4: 397 skin + 264 muscle = 661 particles,
3,123 distances (383 joint stitches), 434 bends, 661 attachments. At 8 / 10 cm with a ring floor of 6: 619 + 414 = 1,033,
4,742 distances (527 stitches), 736 bends. Torso skin 110 and pelvis 54 at 8 cm; hands 76 together.

P2 Per-tick cost, median of 5 interleaved runs of 600 ticks (`matrix2.sh`, 10/14 cm config):

| Scene | Live particles | ms/tick |
| --- | --- | --- |
| A 1 Fella pinned as mass 0 in the solve (shipped code) | 0 | 4.918 |
| B 1 Fella pinned by skip flags | 0 | 0.255 |
| H 4 Fellas pinned by skip flags | 0 | 1.023 |
| D 1 live forearm, rest dormant (absent) | 40 | 0.204 |
| E 1 Fella live, flesh out of particle contact, limb skip | 661 | 1.036 |
| F E plus a released 8 m gut (300 particles, self-colliding) | 961 | 2.359 |
| G E with flesh in the solid contact walk | 661 | 5.441 |
| I E with held_by's attachment scan instead of the limb skip | 661 | 5.280 |
| J 4 Fellas' bones and capacity, 1 Fella live, rest absent | 661 | 2.271 |
| K J plus the gut | 961 | 4.262 |

Phase split (fella_prof): a live Fella in the contact walk spends 4.4 of 5.4 ms in particle_contacts walking pairs of its
own group that never collide. With nothing live, 0.6 ms of 4 Fellas' 1.2 ms is grid_sort clearing a bucket table sized 2x
particle capacity (32,768 buckets). body_contacts is live particles x every body: 0.49 ms for 232 particles x 77 bodies.

P3 Command cost (one call each, 4 Fellas + gut, 8/10 cm): remove 24 skin edges 0.59-0.65 ms, remove 32 elbow stitches
0.61-0.66 ms (one outlier 10.3 ms); 1 Fella + gut at 10/14 cm: 0.12-0.47 and 0.11-0.17 ms. Every remove, attach and
create_distances runs rederive_topology over the whole world. Waking a part (restoring inverse mass) 2-10 us.

P4 Spawn with one attach per call (the only attach Ymir has): 1 Fella 147 ms, 4 Fellas 2.3 s (quadratic in world size).

P5 Joint rings: with held_by, neighbouring bones of the same Fella push flesh at the joints: max live deviation from the
attachment point 0.064 m against 0.023 m with the limb skip (8/10 cm, all live).

P6 Sieve (`sieve.c`): a skin sheet at 8 cm (36 particles, r 0.036, 96 edges) attached along a static capsule (r 0.07,
half 0.18, horizontal) under a 7x7x7 fluid pour or a 4x4x4 solid block, 3 s at 4 substeps:

| Load, rate | Worst edge stretch | Deep tick x pairs | Load inside the bone | Skin mean axis distance |
| --- | --- | --- | --- | --- |
| fluid 60 Hz | 0.0204 | 35 | 0 | 0.0700 |
| fluid 120 Hz | 0.0221 | 2 | 0 | 0.0700 |
| solid 60 Hz | 0.0275 | 2 | 0 | 0.0700 |
| solid 120 Hz | 0.0321 | 0 | 0 | 0.0700 |
| mutant: attachments off (compliance 1e3), fluid 120 Hz | 0.0566 | 0 | 0 | 0.4016 |

The body-backed skin does not sieve: it holds its place within 0.1 mm and stretches 2-3%. 96 of 343 fluid particles reach the
bone surface between skin particles, which is where the drawn skin is; none enters the bone. The corner-pinned tarp's sieve
is a limit of loose cloth, not of BDS skin.

## Cut order

1. cut-ymir-skin-sieve-test (Ymir, test and doc). Beside it: 2. cut-ymir-particle-limb-contact (Ymir: held list derived at
   command time, LIMB attachment flag skips the attached body's limb tree).
3. ymir-particle-pinning, revised: dormant particles out of every loop through command-time active lists; pinned particles
   bordering live ones are kinematic per substep; one-way attachment flag; wake writes position and velocity from the target.
4. ymir-attach-batch (spawn) and, after question ymir-flesh-particle-contact, ymir-flesh-no-particle-contact.
5. lodur-tissue-stack (plan data and the deterministic sheet builder; can start any time).
6. lodur-body-sheets-graph (binding: build dormant, wake per part, re-pin, wounds and severs as commands), after lodur-physiology.
7. bds-flesh-render-embedding; then lodur-organ-release-rope and lodur-exposed-tube-chains.

Budget reading: at rest flesh must cost nothing per tick (P2 B, H); one hurt part costs about 0.2 ms; a whole live Fella about
1 ms alone and 2.3 ms in a 4-Fella world; an eviscerated gut adds 1.3 to 2 ms. The rope scene already takes about 2 ms, so a
whole-body event on top of it overruns: question bds-flesh-live-budget.
