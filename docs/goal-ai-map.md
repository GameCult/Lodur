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

Pass `goal-targets-ik-map`, Lodur `4118b47` (percepts landed), Ymir `d82da6d`, 2026-11-25. Probes in the
scratch project `imag-goals/probe` (not committed); humanoid, 120 Hz, 4 substeps.

7. **Joint target convention (probe).** Ymir's joint frame lane at build is the shortest arc from +z to the
   plan's axis: Shoulder `(-0.707, 0, 0, 0.707)` (z to up), Elbow `(0, 0.707, 0, 0.707)` (z to across). A target
   q makes the child's rotation relative to the parent, from the creation pose, `F q F^-1`: at gravity 0 the
   measured upper-arm direction in the torso frame for shoulder `x -1.0` was `(0.14, -0.55, 0.83)` against
   predicted `(0, -0.54, 0.84)`, the forearm for elbow `z -1.2` `(0.00, -0.36, 0.93)` against `(0, -0.36, 0.93)`.
   Spherical Lower/Upper bound twist (about frame z, the bone axis) and Cone bounds swing (Ymir
   `AddJoint` summary). Shoulder twist tracks weakly: a combined target `z +0.6` then `x -0.8` reached joint
   rotation `(-0.385, -0.065, 0.157, 0.907)` against `(-0.372, -0.115, 0.272, 0.880)` (twist kp 1, swing kp 20).
   Swinging the right arm toward +x hits the torso: the arm collides with its own trunk.
8. **Zero-twist two-bone solve (probe).** Choosing the elbow swivel so the shoulder target has no twist is
   closed form: `R = Rot(v, phi) * Arc(p, v)`, `phi/2 = atan2(-Q.y, w v.y + (v x Q).y)` with `(Q, w) = Arc(p, v)`
   and y the creation-pose bone axis; the result is unique. Arm lengths: L1 0.310 m, L2 0.265 m (anchors).
   That swivel can push the swing past the shoulder cone where another swivel reaches: the seated ward-off goals at
   head height (`(0.30, 1.74, 0.26)` left, `(0.00, 1.74, 0.26)` right) clamp 20.5 and 22.6 cm short at zero twist and
   are reached at twist 0.27 and 0.52 inside the [-1, 1] limits. At gravity 0 the twist muscle holds those to 0.54
   and 0.44 and the wrists land 7.0 and 2.8 cm off (probe, cut `lodur-orient-goals` s1 fix).
   Across the body, a goal the zero-twist swivel reaches can still be missed: the right arm swinging toward
   `(-0.04, 1.60, 0.24)` drives its forearm into the side of the trunk and wedges there, 45 cm short at gravity 0
   (two forearm contacts, swing 0.20 against 1.50); a forward via point for 0.6 s first brings it to 9.8 cm.
9. **Reach on the ragdoll (probe).** Solving each step from the measured torso frame, wrist error after 4 s,
   right arm, targets at shoulder plus an offset. Gravity 0, free-floating: `(0,-0.25,0.35)` 3.0 cm,
   `(0.12,-0.1,0.4)` 11.1 cm (swing 1.22 near the 1.5 cone, across the body), `(-0.15,-0.3,0.25)` 2.3 cm,
   `(0,0.05,0.45)` 1.0 cm. Gravity on, pelvis welded to a static box: 13.6, 17.9, 10.2 and 23.9 cm: PD sag
   under gravity, not IK error. A naive task-space integrator on the target (2 and 4 per second, 0.3 m cap)
   made it worse (2 to 38 cm, swing past the cone); see follow-up `lodur-reach-tracking`.
10. **Select's one caller** is `Actor.Tick` (`Actor.cs:173`); BDS never calls it (grep at BDS origin/main).
    Every authored priority is >= 0 (BDS OfficeBrain 0, 1, 2; ChamberBrain 0, 1; PainBoxBrain 0).
11. **Percept slots are reused** by another id after eviction (`Perception.Admit`), so a target is tracked by
    Id. Sense runs before Select within a tick (`Actor.cs:145`, `:173`).
12. **In-flight overlap.** `lodur-physiology-core.r1` changes `Actor.cs` 5-16, 46-90, 100-105, 131-140 and
    207-213, `DriveState.cs`, `WillTests.cs`, `CalmSensesTests.cs`; `lodur-airway-openings` changes `BodyPlan.cs`,
    `BodyPlans.cs`, `Ragdoll.cs`. `brain-goal-targets` touches `Actor.cs` only at 36-40, 126-129 and 169-194;
    `lodur-ik-goals` touches none of these files.

Pass `goal-planner-map`, Lodur `997b1fe`, 2026-11-25, after `lodur:ruling:bds-intelligence-is-planning-depth`.
Probe: a scratch C# replica (not committed), net10 Release on the workstation, 41 rounds of 200 calls in shuffled
order per round, paired medians against a utility-only Select in the same round.

13. **Planner cost (probe).** World: 16 percept slots (the `Perception` default capacity), 48 grounded verbs (go to
    each slot, grab and drop four portable things, unlock with either key on either door, open, cut the strap,
    switch off, eat, two filler verbs on each of eight other things), facts as a 128-bit set; Fella starts strapped
    down with a knife in reach. Six planned wants per Select (open either door, 8 steps; pain off, 4; fed, 6; hold
    the knife, 1; a filler, 5). Iterative-deepening regression to the depth limit, plus the utility pass over six
    goals by 16 slots. Paired median extra cost per Select over utility alone (3.75 us):

    | Depth | Regressive (F.E.A.R.) | Forward | Wants planned | Regressive nodes |
    |---|---|---|---|---|
    | 1 | 0.47 us | 0.59 us | 1/6 | 18 |
    | 2 | 1.33 us | 1.56 us | 1/6 | 41 |
    | 3 | 3.30 us | 4.06 us | 1/6 | 97 |
    | 4 | 8.52 us | 16.07 us | 2/6 | 244 |

    Nodes, untimed, beyond 4: 516, 1118, 2744, 5860 at depths 5 to 8 (all six wants planned at 8); forward search
    visits 15035 at depth 5. Regression grows about 2.2x a level, forward about 10x. At depth 4 the planner costs
    about 1 ms of CPU per second per puppet at 120 Hz, so it can run every Select: no planning cadence, no plan cache.
14. **Where depth bites.** In the probe world a depth-3 Fella plans nothing but grabbing the knife; depth 4 finds
    "grab knife, cut strap, go to switch, switch off"; the door needs 8. Depth is visible in play exactly as the
    ruling wants: the same room, the same wants, a different number of things he can work out.
15. **What exists to plan over.** Percepts carry id, kind, position, salience and age (`Perception.cs:111-131`);
    nothing carries a fact about the thing (open, locked, on). Interactions do not exist (follow-up
    `lodur-interactions`); a verb today has neither preconditions nor effects.

Pass `goal-planner-cut`, Lodur `419d454` (`nibu/brain-goal-targets` head, in Soul review), 2026-11-25.

16. **Baseline.** `dotnet test tests/Lodur.Core.Tests -c Release` at `419d454`: 278 tests. The first run after a
    fresh build failed one test in a reflection-invoked path; four reruns passed 278/278. The failing test was not
    named in the output kept; a Hands pass that sees it names it.
17. **Facts have no home at base.** `Emitter` carries channel, position, radius, intensity, `Id`, `Kind`;
    `Percept` carries id, kind, position, salience, seconds since sensed (`Perception.cs` `Emitter`, `Percept`).
    A thing's kind may change between ticks (`PerceptTests` "takes the new kind"), so facts follow the same rule:
    the slot takes what it was last sensed with, and `Sense` refuses two emitters of one id that disagree.
18. **Overlap.** Cut 2b is split. Part 1 (`lodur:cut_spec:cut-lodur-goal-planner.r1`) touches only
    `Perception.cs`, a new `Planner.cs`, tests and `docs/lodur-target.md` (its row lands after line 115, not
    adjacent to `lodur-tissue-sheet-material`'s edit at 112). Part 2 (follow-up `lodur-planner-select`) edits
    `Brain.Select` and `Actor`, where `lodur-orient-goals` (ctor `BodyPlan?`, Tick `parts`, the commit block) and
    the oxygen cut (`Physiology.Step` input, the ceiling loop) also edit; it runs after whichever of them is in
    flight and adds only trailing optional parameters.

Pass `planner-select-cut`, Lodur `77d00d3` (main: goal targets, planner part 1, airway, orient goals), 2026-11-26.
Probes in the scratch project `nibu-imagination-select/probe` (not committed): `Lodur.Core` at `77d00d3` with
`NodeBudget` made settable and a `MoveRule` switch in `Regress` only; net10 Release on the workstation.

19. **Baseline.** `dotnet test tests/Lodur.Core.Tests -c Release` at `77d00d3`: 377 passed.
20. **Reach and the oracle.** `Planner.Plan` sets bit 63 once per call from distance to the observer
    (`Planner.cs:177-181`) and no verb takes it away (finding `reach-is-monotone`). The forward oracle in
    `PlannerEquivalenceTests` places things only at x 0.5 or 5 and its `Apply` has no reach rule, so it cannot see the
    rule either way: with the Regress half of the move rule on, the 17 planner tests (equivalence included) passed.
21. **Crowded worlds (probe).** Rooms of 16, 48 and 64 things (a strap at 0.3 m; knives, keys, doors 70% locked,
    switches, tools and four junk kinds within 12 m), verbs of the test catalogue plus `Pry` (strap with a tool) and
    junk `Fiddle`/`Stack` decoys, wants on every door, switch, knife and the strap; 100 seeds. Plan calls exhausting the
    4096 budget while an unbounded search finds a plan within depth: none at depth <= 5; at depth 6, 23 of 1200 calls
    (48 things) and 96 of 1800 (64); at 7-8, 62/1200 and 127/1800. The move rule removed 2-3 plans per 600-1800 calls at
    depth 4 (plans holding two far things in reach) and cut nodes slightly. **Decisions** (300 seeds, the best pair by
    `raw x (1 - 0.08 len)`, direct `raw x 0.5 x (1 - 0.08 (d + 1))`): per-plan budget, exhaustive or best-first, chose
    as unbounded search in every room; one 4096 budget per decision spent best-first differed in 0 rooms at depth 4,
    11-20 of 300 at depths 5-8 for 48-64 things, and 16 of 300 at depth 8 for 16 things.
22. **Cost per Select (probe).** Paired interleaved medians, 31 rounds, us per Select (all want pairs):

    | Things | Depth | Every pair | Best-first, budget per plan | Best-first, budget per decision |
    |---|---|---|---|---|
    | 16 | 4 | 25.8 | 10.9 | 11.1 |
    | 16 | 8 | 937.6 | 578.7 | 233.0 |
    | 48 | 4 | 239.0 | 59.3 | 65.5 |
    | 48 | 6 | 1989.9 | 525.1 | 213.4 |
    | 48 | 8 | 3169.1 | 1114.2 | 237.7 |
    | 64 | 4 | 526.8 | 105.0 | 101.1 |
    | 64 | 8 | 3539.3 | 905.2 | 172.6 |

    A grounding costs about 0.2 us here, not the 35 ns fact 13's world implied, and pairs multiply it: the 8.5 us of
    fact 13 was six wants, not a want per thing. At 120 Hz, 1.1 ms per Select is 13% of a core per Fella.

Pass `reach-tracking-map` (its records cite these facts as 19-23; here they are 23-27), Lodur `77d00d3`, Ymir `e51cf76`, 2026-11-26. Probes in a scratch test class (not committed)
in `F:\aeth-wt\imag-reach-lodur`, net8 Release; humanoid, 120 Hz, 4 substeps; errors after 2 s (seated) or 4 s (reach).

23. **Gravity sag is PD sag, and feedforward removes it (probe).** At base the seated head-height wards read 16.0-17.8
    cm under gravity against 2.8-7.0 cm without; the welded reach targets 10.2-24.0 cm against 0.25-0.89 cm. Adding,
    after `Ragdoll.Drive`, each joint's subtree gravity load times the muscle's strength (`-(sum m_b (x_b - a_j)) x g`,
    masses from the plan's shape and density, which Box3D also uses) brings the reach targets to 0.88, 0.11, 0.55 cm and
    the seated wards to 3.95-5.51 cm. At half strength it still sags (12.0-22.9 cm), as a weak will must: the torque
    and the stiffness scale together. Cost, an O(parts) bottom-up form, 41 paired interleaved rounds: +0.79 us per
    `Drive` (0.89 to 1.66 us) against 89.4 us per `world.Step`.
24. **Feedforward on every muscle moves 13 of 60 `Lodur.Tests` (probe, also projected onto each muscle's gain
    axes).** ConnectedPainBoxTests (5): the held arm no longer sags into the box, the will never breaks, the replay
    golden changes; HandTests (7): thumbs keep turning at rest (0.2-1.2 rad/s), the wrist overshoots its cone to
    1.069, the finger rest reads 0.29 against 0.35; RagdollTests hinge-axis torque (1). Content and tuning were set
    against sagging muscles. Question `gravity-feedforward-scope`.
25. **The far-arm wedge is cleared by a via point ahead of the wrist's own side (probe).** Seated low sound, right
    wrist from (-0.21, 0.975, 0) to its ward goal (-0.04, 1.60, 0.24) in the torso's creation frame: the straight
    wrist segment touches the trunk (distance 0.0 to the torso and pelvis boxes); for the head-height sound on the
    right it clears by 6.6 cm, on the left it touches (0.0). Minimum-jerk path, two 0.3 s segments, via point
    `(wrist x, mid y, trunk front + c)`, under gravity with feedforward: low far arm 9.9, 7.6, 6.0 cm at c = 0.15,
    0.2, 0.3 (zero gravity, c 0.2: 7.8); the left head-height sound's right arm 2.2-3.2 cm. A via point at the
    midpoint's x needs c 0.3 and fails at 0.15-0.2 (38-40 cm); at the goal's or shoulder's height 8.2-16.6 cm. A
    straight path with no via: 47.5 cm.
26. **A hand path costs where nothing blocks (probe).** The straight minimum-jerk path on the unblocked right-side
    ward ends 12.8 cm off (gravity, feedforward) against 5.5 cm for the direct solve; on the left-side ward without a
    via, 9.7 against 3.95 (zero gravity 28.2 against 2.8). The final IK targets are the same; the shoulder twist is
    not: 0.84 rad off its target after the path against 0.06 direct, no contacts at the end. The twist muscle (kp 1,
    fact 7) cannot pull the twist back once the path leaves it elsewhere. Question `reach-path-unblocked`.
27. **The hand-off jump.** `OrientRig.Compose` runs only while the goal in force has layers (`Actor.Tick`,
    `PoseBehaviour.Write`), and an arm not driven keeps its slewed output, so engaging slews from stale targets and
    disengaging writes the base pose at once (Soul s4 on `lodur-orient-goals`: 0.65 rad in a tick).
28. **Feedforward on a supported body makes it weightless (probe, pass `gravity-cuts-map`).** The hand-rest bodies
    that keep turning (fact 24) belong to a fallen body: `RagdollScenario` lies on the ground after 5 s (head at
    0.11 m). Feedforward cancels the weight that gave the floor contact its friction, so palm, thumb and fingers
    drift at 0.07-0.80, 0.21-1.20 and 0.12-0.94 rad/s (off: 0.000-0.012). Skipping feedforward on the hand joints
    leaves them drifting (thumb 0.13-2.88): the load comes from the arms and trunk. Reading the anchor from Ymir's
    `JointOrigin` lane instead of the plan changes nothing measurable. A gate that gives a body touching anything
    (`ContactCount > 0`) no load settles every hand body (0.000-0.007 rad/s) and passes all five hand-rest tests.
    A continuous gate (load minus `ContactImpulse / dt`) is worse than none (thumb 1.5-1.9 rad/s): the one-step
    lag rings. The count gate also counts self-contact: thumb against fingers, a forearm on its own thigh.
29. **The pain-box hold re-solved, and its survival curve (probe).** At the base pose (shoulder -1.0, elbow -0.6)
    feedforward lifts the palm from (-0.207, 1.121, 0.446) to (-0.174, 1.395, 0.572), out of the box, and no seed
    of 20 breaks in 60 s. A grid fit of the two hold angles to put the settled palm (seed 1, 3-4 s, box at
    intensity 0) at the box centre gives shoulder -0.66, elbow -0.28 (0.49 cm) without the gate and -0.70, -0.36
    (0.34 cm) with it. Seeds 1-20, 60 s limit, break / first flinch in s: at base 14.12-19.23 / 6.86-9.34, median
    break 15.9 s, every seed breaks by 19.3 s (no censoring); re-solved pose with gate within +0.03 s of base on
    every seed's break and first flinch within 0.11 s (seed 13; the rest equal). Hand in the box after 2 s: 99-100% at
    base, 94-98% gated.
30. **What moves with the gate and the re-solved pose (probe, gravity hard-coded so the two zero-gravity tests are
    probe artefacts).** `Lodur.Tests`: 5 of 60 (13 ungated): the seed-1 replay golden; the leak test, whose hand
    now leaves the box at tick 1756, 18 ticks before the break at 1774 (box half 0.125 was sized against sag); the
    wrist cone, 1.0616 against the 1.06 bound both sides; the hinge-torque test (the wrist's feedforward reaches the
    forearm). BDS `3d6e735` against it: 69 of 71 (70 of 71 ungated): the body hand-in-box test (one excursion
    before the break) and, gated only, the flinch control (tracks 5.5 cm apart before the first flinch, 6.0 after).
    Office body tests, the CLI goldens and `tools/PainSweep` (engine-free, no body) do not move. Probe:
    `F:\aeth-wt\imag-gravity-probe\` (patch, harness, logs).
31. **The built gate in a crowded room (probe, `PlannerSelectTests`).** The first build planned pairs best raw first, a whole
    depth each. Room of 48 things (a strap at 0.3 m, a knife at
    0.5 m, then knives, keys, doors 70% locked, switches and four junk kinds within 12 m; the test catalogue plus `Pry`,
    `Fiddle`, `Stack`; wants on every door, switch, knife and the strap; doors outrank the strap), 100 seeds, ThinkBudget
    4096 against `int.MaxValue`: decisions that differ were 0 at depth 4, 37 at 5, 59 at 6 and 62 at 8 (at 64 things, 8
    at depth 4). This is far above fact 21's 4-7%: the first pair in raw order is a locked door no plan within depth 6
    reaches, it spends the whole budget searching, and the strap he could cut is starved to a direct try. The count is
    a property of the world's priorities (a crowd of high-raw unplannable wants), not of the gate; fact 21's room did
    not have it. Paired interleaved medians (20 seeds, 20 measured ticks each after 12 settling ticks, net10 Release,
    workstation): `Brain.Select` alone, budget 4096, 406 us at depth 4 and 766 us at depth 6 (unbounded: 259 us and
    13.3 ms); `Actor.Tick` over a utility-only Brain, extra 441 us at depth 4 and 924 us at depth 6, the difference
    from Select being the committed pair's plan, a second Plan call with the full budget every tick. A grounding costs
    about 0.1-0.2 us. Deepening across pairs, with the plan made by the Select that chooses the pair and carried with
    it, as built: decisions that differ are a rate, about 0.5% at 48 things and 3% at 64, at every depth (400 seeds, 5000-5399: 1 and 11;
    seeds 0-399: 1 and 13; seeds 9000-9399: 3 and 23; the first 100 seeds gave 0 and 2, a property of those seeds). Paired interleaved medians, 48 things, budget 4096, net10 Release: `Brain.Select` 206, 205 and 206 us at depths 4, 6 and 8
    (unbounded 205, 208, 206; a Select spends 1592 groundings at the median, 4096 at most), `Actor.Tick` extra over a
    utility-only Brain 68 us at depth 4 and 79 us at depth 6: a tick spends at most one `ThinkBudget`. At depth 4 the move rule's plan cost
    is about 2x on movers and 1.0x elsewhere (Soul s1 on `lodur-planner-moves`, paired medians, 6 wants); the budget
    bounds it, so no work on it here.
32. **Self-contact under the count gate, and the subtree-external gate (probe, pass `gravity2-map`).** At `63444f0` the
    count gate's self-contact cost shows in tests: the gravity reach reads 3.01, 3.16, 3.97 cm (ungated 0.89, 0.13,
    0.54) because the right thumb and fingers touch. Probed gate: each body's load is deposited at the lowest common
    ancestor of it and every body it touches (a body touching nothing deposits at itself; a touch outside the ragdoll
    deposits nowhere, as the count gate), then summed bottom-up as at `63444f0`, so a joint carries a body unless a
    touch outside the joint's subtree supports it. Touching pairs were kept in the probe from Ymir's Begin/End facts
    and equal `ContactCount` on every body every step (2,614,540 body-steps, 0 mismatches). Readings: gravity reach
    0.77, 0.16, 0.72 cm; half strength 19.00, 13.85, 22.79 cm; seated ward under gravity 7.03 cm worst (count 6.00,
    ungated 7.59, zero gravity 8.11: the ward is not gravity-limited); hand-rest tests pass (ungated: 5 fail). In
    the chamber hold at 3 s, 5 of the 7 touching pairs are self-contacts. Cost per `Drive` from a per-step pair list
    (lanes mapped by offset, LCA by depth walk), 20000 calls x 41 paired interleaved rounds, net10 Release: +0.227,
    +0.230, +0.255 us over the count gate's 1.58-1.93 us. Probe: `F:\aeth-wt\imag-gravity2-probe-lodur.patch`
    (scratch modes `LODUR_GATE=count|subtree|none`), logs `F:\aeth-wt\imag-gravity2-*.txt`.
33. **The pain-box hold under the subtree-external gate (probe).** The count-gate pose -0.70, -0.36 settles the palm
    4.74 cm from Box (count 0.24). Grid 0.04 then 0.01: -0.68, -0.25 at 0.15 cm (count-gate refit -0.73, -0.30 at
    0.15 cm: the fit is a ridge). Seeds 1-20, 60 s: median break 15.90 s (14.16-19.26), holding at 10/15/20 s
    1.00/0.70/0.00, censored 0, median first flinch 7.55 s (`63444f0`: 15.88 s, 1.00/0.70/0.00, 7.55). Box half by
    the shipped-run rule (seed 1, ticks out from 2 s to the break): 0.145 3, 0.150 2, 0.155 0. `Lodur.Tests` then
    fail 2: the seed-1 replay golden and the strain shake (shipped 2.68 cm in second 8, bound 3 cm).
34. **BDS `c55770f` against the gravity feedforward (probe).** Against `63444f0`: 66 of 81 (the report's 15). With
    Lodur's pose -0.70, -0.36 and box half 0.145 only: 79 of 81, left the flinch control (seed 1 gap before the
    first flinch 5.50 cm, after 6.15) and office settle (seed 100 hand 0.0292 m/s). The same content under the
    subtree-external gate: 80 of 81 (only the idle drift, 5.7 cm: the pose was fit for the count gate). With the
    subtree refit (-0.68, -0.25, half 0.155): 80 of 81, the flinch control on seed 3 (before 2.06 cm, after 3.98;
    rule before < after / 2). The pre-flinch gap is the leak, not a contact: in the Lodur fixture full and
    flinch-gain-0 runs part at tick 39-40 (the box ramp), first flinch at 843-1043, and forcing the feedforward's
    strength factor to 1 leaves the gap (seeds 1-3 before 1.70-2.69 cm). BDS's world is Lodur's fixture (same
    ground, stool, weld, lane order). Probe: `F:\aeth-wt\imag-gravity2-probe-bds.patch`.

## Prior art (research pass `goal-ai-prior-art`)

- The Sims: needs-based utility; objects advertise what they offer, so the data lives on the object and new
  objects need no character code. Fits "interact with objects in various ways" and `sandbox-is-content`.
- F.E.A.R. (Orkin, GDC 2006): three states (Goto, Animate, UseSmartObject) plus a STRIPS-like A* planner over
  actions. The three states are the motor vocabulary we need. The planner was rejected in the first pass and is
  taken now (pass `goal-planner-map`): its depth is plan length, which is what the intelligence ruling names.
  Read from the paper's text: the world state is a fixed array of variables, effects and preconditions are
  entries in it, search regresses from the goal, and **target and weapon selection run outside the planner**,
  which reasons about the one target in focus. That is the split this map keeps: utility picks (goal, target),
  the planner finds how.
- HTN (Killzone 2 bots, SHOP-like; Horizon's Decima planner, which backtracks over preconditions as Prolog does):
  depth there is the decomposition depth of authored methods. Capping it does not make a duller character, it
  truncates a method into a broken one; a dim NPC is one with fewer methods, which is authoring, not a stat.
  No published cost figures (Guerrilla's pages give none).
- The Sims: objects advertise and selection is one step; no lookahead (Forbus's lecture notes mention only
  routing A*). Wright said the early autonomy was too good and Sims were made dumber for play: the precedent for
  intelligence as a tunable limit rather than a fixed competence.
- MCTS (Total War: Rome II campaign AI, Champandard 2014): depth is rollout length and iterations, it needs a
  forward simulator of the world, and it is stochastic per decision; built for turn-based campaigns.
- Dave Mark (gamedev.net) on GOAP: long plans in a dynamic world are invalidated each think and replanned
  whole. Fella's plans are short (the depth cap) and replanning is the measured 0.5-8.5 us, so the cost he
  names is paid and affordable.
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

**The planner (layer 4, between selection and the motor layers).** A goal may name a want: a fact about its
target (open, off, held) or about himself (free, fed). Objects advertise verbs (BDS content, keyed by kind);
each verb carries preconditions and effects as facts over the acting slot, a second slot, or himself, plus the
toil step list that performs it. An emitter carries the thing's facts as the game sees them, and the percept
remembers the facts as last sensed, so a remembered door is still locked to him after it was opened out of
sight. Lodur never interprets a fact key, as it never interprets a kind.

Search is regressive and depth-first with iterative deepening, as F.E.A.R.'s, over verbs grounded on the
percept slots, to a depth limit that is the intelligence stat; it returns the shallowest plan, ties to the
lowest verb order, and allocates nothing. Selection stays utility (`brain-goal-targets`), and the planner is a gate
on its pairs, built in `Brain.Select` and the Actor's `Deliberation`: a pair whose want already holds is not offered;
a pair planned within `PlanDepth` steps scores raw times the `PlanSteps` responses at its length; a pair with no
plan (none within the depth, or the think budget gone) is tried direct at `DirectDiscount`, reading `PlanSteps`
at depth + 1 (ruling `bds-unplannable-tries-direct`, which answers `bds-unplannable-goal`: he goes at the target
and fails where the player can see it; a direct try that reads `PlanLength` -1 for `FrustrationSeconds` bars that pair's
direct try, reports `TickEvents.Frustrated` and decides again past the dwell, and the bar lifts on forgetting the target or
`RetrySeconds`; a changed fact does not lift it, since his own try can change one, and a change that makes the goal
achievable shows as a plannable pair offered through the bar, ruling `frustration-lifts-without-fact-changes`). One `ThinkBudget` serves a whole Select, spent by deepening
across the pairs (Korf's iterative deepening, anytime): round k asks every live pair for a plan of exactly k steps,
by a bound on what the pair can still score, so short plans are found first and one pair's dead end cannot starve the
rest; a want about himself is planned once per goal. The plan is the decision's: made by the Select that chose or
re-chose the pair and carried with it to the Actor's commit (`PlanLength`, `PlanStep(i)`), checked against the
percepts on a tick that decides nothing (its want holds, or a thing it names left memory), and made again for the
pair in force when the dwell refuses a Select, from what is left of that Select's budget. Select never reads it;
the pair's commitment keeps the choice stable. A plan step is a verb on a percept, and a phantom
percept advertises as its kind does: a clever Fella plans through a hallucinated door and fails at it as a dim one
fails at a real one. Intelligence 0 is the Fella of `brain-goal-targets`: no plans, every want pair direct. With no
pair offered the goal in force stays and lets go of its target.

**Mind stats.** Four sim stats per `lodur:ruling:bds-intelligence-is-planning-depth`, each derived each step as
a physiology capacity is (base from the character content, then the offsets and factors of live afflictions,
so a potion is an affliction whose effects target a stat): intelligence (planner depth, rounded down),
quickness (scales the deliberate latency and the dwell), judgment (temperature of a seeded softmax over pair
scores; perfect judgment is the argmax of `brain-goal-targets`), memory (percept memory seconds). Player-facing
stats, if fewer, map onto these four and own nothing.

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
- Owner of how a goal is reached: the planner, reading percept facts and advertised verbs to the depth the
  intelligence stat gives it. The plan belongs to the decision: written by the Select that chooses or re-chooses the
  pair (or by its refusal), carried with it, checked on carried ticks; Select never reads it; nothing outside the Actor writes it.
- Owner of the four mind stats' current values: physiology, from character base and live afflictions. Brain,
  planner and Perception read them; a potion, a session or BDS never writes a depth, a latency or a memory time.
- Owner of motion: the will mix over voluntary layers; reflexes act against it.
- Forbidden writers: the game writing a phantom (top-bit id), a Brain reading phantom-ness, BDS setting a goal
  or target from outside, any layer writing a joint or force (`body-one-interface`), a kinematic root move,
  the planner reading phantom-ness or a kind's meaning, BDS handing Lodur a plan or a depth.
- Derived: salience, the target slot, the thought-bubble type (from the phantoms in memory), desired velocity,
  the plan, the mind stats.
- Shared paths: a phantom and a real thing go through the same `Sense`; a scenario Brain and a sandbox Brain
  are the same data; a player-placed lure and an arena's lure are the same object content.

## Cut order

| # | Cut | Repo | Builds | Waits on | Size |
|---|---|---|---|---|---|
| 1 | `lodur-percepts` | Lodur | emitter id and kind, percept memory, phantom id bit | none | S, specced |
| 2 | `brain-goal-targets` | Lodur | target filters, (goal, percept) utility, commitment | 1 | M |
| 2b | `lodur-goal-planner` | Lodur | percept facts, verbs with needs and gives, depth-limited regressive planner (core, no caller) | 2 | M, specced |
| 2c | `lodur-planner-moves`, `lodur-planner-select` (built), `lodur-planner-frustration` | Lodur | the move rule and a budget per Plan call; wants, the Select gate, direct tries at a discount (`bds-unplannable-tries-direct`), one think budget per Select, the plan on the Actor; the frustration that ends a futile direct try | 2b | S, M, S |
| 3 | `lodur-ik-goals` | Lodur | Look, ArmL, ArmR as a Core producer of joint targets (legs later) | none (parallel to 2) | M |
| 3b | `lodur-reach-path` | Lodur | hand path through a clearance via point when blocked; arm hand-off slewed both ways | 3, 4 | M, landed |
| 3c | `ymir-world-gravity`, then `lodur-gravity-feedforward` | Ymir, Lodur (+ BDS content) | the world reports its gravity; muscles carry their subtree's weight times strength | Q `gravity-feedforward-scope` | S, M |
| 3d | `ymir-touching-pairs`, then `lodur-gravity-subtree-support`, then `bds-gravity-feedforward-content` | Ymir, Lodur, BDS | the world publishes each step's touching pairs (ABI 20); only a touch outside a joint's subtree supports it (facts 32-33); the chamber re-authored against it (fact 34); Lodur and BDS land in one sitting | 3c | S, S, S |
| 4 | `lodur-orient-goals` | Lodur + BDS | look, reach, ward off and recoil as layers; fetal (a key-pose goal) and stare-at-hands (Look and Reach on a phantom) are data; seated Fella demo | 2, 3 | M |
| 5 | `lodur-phantom-percepts` + `bds-thought-bubble` | Lodur, BDS | phantom things by hallucination type; the bubble reads their kinds | 1, 4 | M, S |
| 6 | `lodur-assist-pin` | Lodur | upright assist | none | S |
| 7 | `lodur-stand` | Lodur + BDS | standing puppet scene, stool weld dropped | 6 | S |
| 8 | `lodur-walk-gait` | Lodur | Rosen walk, leg IK | 3, 7 | M |
| 9 | `lodur-steering` | Lodur | approach, avoid, arrive into desired velocity | 2, 8 | M |
| 10 | `lodur-grip`, then `lodur-interaction-steps` | Lodur + BDS | a verb's toil steps run the plan's current step | 2b, 9, Q `bds-sandbox-lures` | M each |
| 11 | `lodur-mind-stats` | Lodur | intelligence, quickness, judgment, memory as derived stats; potions drive them | 2b, `lodur-physiology-core` | S-M |

Cut 1 is `lodur:cut_spec:cut-lodur-percepts.r1` (landed). Cuts 2 and 3 are
`lodur:cut_spec:cut-brain-goal-targets.r1` and `lodur:cut_spec:cut-lodur-ik-goals.r1`; they touch disjoint files.
The rest are follow-ups. `brain-goal-targets.r1` stands unchanged under the planner: its (goal, target) pair is the
planner's unit, F.E.A.R.'s split of target selection from planning; cut 2b adds one admission gate and one
consideration input to its Select.

**IK rationale (pass `goal-targets-ik-map`, research `ik-prior-art`).** IK is a producer of joint targets in
Lodur.Core, as Final IK feeds PuppetMaster's target pose, not a solver in the binding: the binding stays the
muscle layer and the will mix and graft mask see IK like any other layer. Arms are two-bone with a hinge elbow,
so the analytic solve is exact; FABRIK and CCD wait for graft chains that are not two-bone. The elbow swivel is
the one needing no shoulder twist, because the shoulder's twist muscle tracks a twist weakly (fact 7); what this
gives up is an authored elbow direction. Where that swivel leaves the cone, the swivel is the nearest one inside
the cone and the twist limits, the feasible swivel range of Tolani, Goswami and Badler (2000) (fact 8). When no
swivel is feasible, the swivel is the one whose limit violation (swing past the cone plus twist past its range) is
least, with swing and twist clamped to the limits: at the edge of the feasible set that is the last feasible swivel,
so a goal crossing the edge moves the targets continuously (the zero-twist clamp it replaces jumped 1.4 rad there).
That choice is stateless, and mirrored swivels tie under it (at the shoulder's sagittal plane, unreached or reached),
so it must jump somewhere on the tie set. Ruling `ik-swivel-temporal-coherence` makes each `ArmChain` the arm's solver
across ticks: it holds the previous swivel's basin, takes the stateless choice only when that is better by
`BasinMargin` (less violation, or less twist when both are feasible), and turns the swivel no faster than
`SwivelRate`; the first solve after construction or `Reset` is stateless. Both constants are placeholders. What it
gives up: IK is no longer a pure function of the goal. Near a half-turn swing the swing-twist split in the clamp is
singular, so the clamped solution can still jump; ruling `ik-output-slew-limit` covers it at the output: the returned
shoulder and elbow targets move toward the solution no faster than `MaxAngularRate` (a placeholder near human peak
arm speed), everywhere, and the singular-band special case is deleted. What that gives up: a leaping goal is followed
at the rate, not at once. The slew holds across the hand-off too (cut `lodur-reach-path`, fact 27): an arm that engages
slews from the base pose, one the layer stops driving slews back to it, and `OrientRig.Compose` runs on every tick of an
Actor with a rig, so the arm layer's own contribution to the written arm targets moves at most `MaxAngularRate` times the tick across engage
and disengage while the base pose is constant. A goal switch to a different key pose steps the base itself, and the written arm targets
with it by the base difference (0.2-0.4 rad measured at weights 0.2-0.5), as every other joint's target steps by its key difference;
that step is not slewed. Reach under gravity
lands short by PD sag (facts 9, 23); that is follow-up `lodur-gravity-feedforward`.

**Reach rationale (pass `reach-tracking-map`, ruling `reach-clears-body-by-via-point`).** One owner of an arm's motion
across ticks: `ArmChain` holds the held swivel, the slewed output and now the hand path and the hand-off state, so a
teleport's `Reset` forgets all of it at once. The path is minimum-jerk (Flash and Hogan 1985) in the arm parent's creation
frame, where the chain already solves; it starts at the measured wrist when the arm engages or the Actor commits a (goal,
target) pair, and it is taken only when the straight wrist segment is blocked by the trunk (fact 25). An unblocked reach
solves the goal directly each tick, slewed (ruling `reach-path-blocked-only`; a path there ends further off, fact 26;
it gives up the bell-shaped hand speed of an unobstructed human reach). The via point sits ahead of the wrist's own side,
not of the midpoint (`ArmChain.ViaClearance` 0.2 m and `PathSegmentSeconds` 0.3 s are placeholders; blocked is decided once per
path start, against the trunk bounds the chain derives from the arm's parent part and its ancestors): the arm comes forward first, then across, as a person reaching across their body does. Gravity is a
muscle-layer term, not a reach-layer correction (fact 23): computed-torque practice (PD plus gravity compensation,
Takegaki and Arimoto 1981; Coros et al. 2010 for simulated characters) puts it in the torque, scaled here by strength so a
failing will still sags; a task-space loop on the target was unstable (fact 9) and fights a weak will. PuppetMaster's
alternative, pin forces pulling bodies to the target pose, is an external force and breaks `body-one-interface`.

**Goal-target rationale.** A target is tracked by percept Id (fact 11), selection scores (goal, percept) pairs,
and a retarget inside the goal waits the deliberate latency but not the dwell, held only by the commitment
multiplier. No `TargetChanged` event: nothing reads one.

**Planner rationale (pass `goal-planner-map`).** The ruling makes intelligence the lookahead, and utility
selection has none, so a planner exists. Regression over verbs (F.E.A.R.) is taken because its depth is plan
length, the ruling's own words, and because it visits about a tenth of forward search's nodes at depth 4
(fact 13). HTN is not taken: its depth is authored decomposition, so lowering it breaks methods rather than
dulling the puppet. MCTS is not taken: it needs a forward simulator and is stochastic per decision. What this
gives up: verbs now need preconditions and effects authored as facts (the cost the first pass named), a failed
plan has to be read by a debug view of the plan, and facts on emitters widen the game's interface to Lodur by
one field.

**Planner cut rationale (pass `goal-planner-cut`).** Facts are one `ulong` per thing, the game's bits; bit 63 is
Lodur's in-reach, derived per call from the sense point and the percept's position and refused from the game, as
`PhantomIdBit` is (idea `goal-planner-cut.rejected-reach-as-game-fact`: a game-supplied near goes stale as soon as
he moves; what it gives up is one bit). Verbs are the world's list, given to the planner, not each Brain's
(`rejected-verbs-on-brain`, ruling `bds-lures-tagged-props`). Verbs carry only needs and gives now; the interaction
point and toil steps join the same record in `lodur-interaction-steps`, which waits on locomotion
(`rejected-interaction-steps-first`). Plan steps name percept ids, not slots. A verb that gives its This in-reach is a move
(no Verb field marks it; the give is the mark): after it every other thing is in reach exactly when it is within reach
of that This, by the percepts' remembered positions, so a plan cannot hold two far things in reach (finding
`reach-is-monotone`). A carried thing is reached through a game fact, not reach, so a give of in-reach on a Tool or a
false one is a plain literal. A Plan call takes a budget, defaulting to `NodeBudget`, and `LastExhausted` says it spent
it; a spent budget drops legitimate plans (finding `budget-drops-legit-plans`), and not worked out is tried direct
(ruling `bds-unplannable-tries-direct`).
Intelligence 0 is read as: want goals go direct at the discount and still end in frustration, and every Brain
without wants selects exactly as `brain-goal-targets` (idea `depth-zero-is-direct-with-frustration`, in force for
the part-2 mapper).

**Planner-select rationale (pass `planner-select-cut`).** Cut 2c is three cuts: `lodur-planner-moves` (Planner only:
a This give of in-reach is a move, so after it another thing is in reach only if near the move's target; a budget per
Plan call and `LastExhausted`), `lodur-planner-select` (wants, the gate, the plan on the Actor) and follow-up
`lodur-planner-frustration`. The gate: a satisfied want is not offered; a planned pair scores through `PlanSteps`; an
unplanned or not-worked-out pair is tried direct at `DirectDiscount`, reading `PlanSteps` at depth + 1 so a futile try
never outranks an equal pair he can plan. Pairs are planned by deepening across them (round k, a bound per pair, a round
ends when no bound can win; with an unbounded budget this chooses exactly as planning every pair), so a pair that no plan
reaches cannot spend the budget before a cheap plan is found; fact 31 measures best raw first starving at 59 of 100
decisions at depth 6 and deepening at 0. One `ThinkBudget` per Select bounds cost (fact 22) at the price of 4-7% of choices in crowds at depth 5 and up (fact 21), and 0-2% in fact 31's room:
question `bds-think-budget-per-decision` (Deru). With no pair offered, the goal in force stays and lets go of its
target (finding `stale-target-without-pair`); the orient layers then rest. The pair's plan is made with the decision (the
Select that chose it, or re-chose it, or was refused and plans the pair in force) and carried; it is not derived each tick, which cost a
second Plan call per tick (fact 31).

## Mind stats

`lodur:ruling:bds-intelligence-is-planning-depth` answers the withdrawn `bds-intelligence-in-goal-ai`: four
stats, not one scalar. Follow-up `lodur-mind-stats` holds them (see Target shape).
