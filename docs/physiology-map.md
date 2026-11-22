# Lodur physiology: one substrate for bodies, tissues, substances and environments (map)

Imagination pass `physiology-map`, nibu-imagination, session nibu-self-2026-11-17, 2026-11-22. Serves
`lodur:ruling:bds-death` (Deru's direction), `bds-mvp-scope`, `bds-gore-tarantino`, `bds-reattach-both-and-grafting`,
`bds-will-visible`, `puppet-living-mesh-physiology-and-nerve-oscillators`, `doll-material-cast-silicone` and
`bds-doll-silicone-interior` (answered meat-core). Prior art: pass `lodur:bds-physiology-prior-art` (Eyes, 23 notes)
and `lodur:mvp-prior-art`. This page sits beside `docs/bds-mvp-map.md`; where they differ, this page is newer.

## Bases

| Repo | Base | Note |
|---|---|---|
| GameCult/Lodur | `dedf391` (main) | physiology is `DriveState` scalars; Consciousness and Attention are slots reading 1 |
| GameCult/Lodur | `d7a626b` (`nibu/lodur-ragdoll-hands`, reported h3) | 19 bodies, 18 joints; the first cut here bases on it |
| GameCult/BadDaySimulator | `cf0ef3a` (origin/main) | consumes `Limb`, `Muscle`, `Region` in `BodyPainBox.cs`, `OfficeBody.cs`, `SeatedPuppet.cs` (read through `git grep origin/main`, the tree untouched) |
| GameCult/AquaSynth | working tree | `docs/ipa-vocal-tract-roadmap.md`: `PhoneticIntent` → `ArticulatoryPlan` (morphology-constrained) → DSP |

## Body facts (probes)

1. **Nothing physiological exists.** Lodur at `dedf391`: `DriveState.cs:78-145` holds pain, strain, arousal,
   resolve, the break; `Consciousness` (96-97) and `Attention` (93-94) are slots written only by the test seam
   `Actor.SetWillSlots` (`Actor.cs:101-105`). `Will = min(Resolve x Attention, Consciousness)` (100). No organ,
   blood, substance, atmosphere or vocal code (Eyes `lodur-current`).
2. **The body is code, three enums deep.** On the hands branch `HumanoidRagdoll.cs:7-24` declares `Muscle` (18),
   `Region` (3), `Limb` (19); `Build` (61-144) hard-codes every shape, frame, limit and gain; `ReadRegions`
   (180-200) hard-codes palm and head. Consumers by `git grep`: Lodur src 48 uses in that file only (Core sees
   array lengths, never the enums), tests 89 uses in five files, BDS 33 uses in three files. The Godot client
   derives the puppet from shapes, parents and joints, not from limb labels.
3. **Build order is not enum order, and that is harmless.** Bodies are added Pelvis, Torso, Head, then per side
   upper arm, lower arm, thigh, shin, foot, then per side palm, thumb, fingers; joints Spine, Neck, then per side
   shoulder, elbow, hip, knee, ankle, then per side wrist, thumb, fingers. `Drive` (153-174) walks muscles in enum
   order. Re-indexing the muscle arrays to build order keeps, for every body lane, the relative order of the
   torque contributions it receives (torso: spine, neck, shoulder L, shoulder R in both orders; pelvis: spine,
   hip L, hip R; forearm: elbow then wrist; palm: wrist, thumb, fingers), so the float sums are the same and the
   pinned replays should not move. Claimed, to be shown by the first cut's unchanged pins.
4. **Cost, run.** `docs/notes/physiology-cost-probe.cs` (a net10 console program, Release, this workstation): one slow physiology step
   over 128 tissue compartments x 16 substance slots (perfusion-limited exchange with an `exp` per pair, every
   step), osmotic water flux per compartment, 64 Hill-curve effects, 3,072 effective properties recomputed from
   base x factor + offset, and a 32-part thermal chain: **140 us per step**, average of 20,000 after 2,000 warm-up.
   Allocation was not isolated from setup; zero steady-state allocation is the cut's test, not this probe's.

## The substrate

A puppet is not a humanoid; it is a graph. **Parts** (nodes, each a session-unique stable id) are joined by
**attachments** (edges: a joint frame plus which conduits cross it: bone, vessel, nerve, airway). Each part is a
**stack of tissues** listed outside-in, each tissue an instance of a material with properties. Under meat-core the
outermost layer of every part is the cast silicone skin, a few mm thick; meat (fat, muscle), bone and, in head and
trunk, organs lie under it. Each tissue holds a **compartment**: its water and its amount of each active
substance, exchanged with the blood of its circulation by PBPK parameters (perfusion, partition, permeability,
clearance through organ function). The **environment** is boundary conditions sampled at each part (ambient
pressure, gas partial pressures, temperature, convective medium, ionizing dose rate) and at the airway; Lodur
stores none of it. An **effect** is data: (target family and property, selector over parts and tissues, operation,
magnitude as a curve of a driver, time shape, condition). A potion or toxin is a substance definition whose
effects, partitions and reactions are data; it becomes code only when it needs a property that no mechanism reads.

The organism is no longer an owner; it is derived: the set of parts connected through attachments. The actor is
no longer one per body plan; there is one per brain (a part whose tissue carries the Consciousness function),
and each muscle answers to a brain by the rule in question `bds-graft-control`.

### Mechanisms (each the single reader of its property family)

| Mechanism | Rate | Reads | Writes | MVP |
|---|---|---|---|---|
| Insult resolution: an insult (part, kind, magnitude, point) penetrates the stack outside-in; each layer absorbs by its resistance to that kind, loses integrity, may open a wound | fast | tissue material: thickness, cut/pierce/blunt resistance, structural | tissue integrity, wounds | yes |
| Bleeding and blood: wounds bleed by vascularity x wound size; circulation blood fraction | fast | vascularity, wounds, BloodPumping | blood fraction, bleed events | yes |
| Nociception: damage and inflammation per tissue to afferent per part | fast | nociception per tissue, integrity change | per-part afferent into `Perception` | yes |
| Capacities: organ function x tissue integrity x substance factors, aggregated per organism (RimWorld) | slow | organ function family | Consciousness slot, strength ceiling, Breathing, Pumping, Filtration... | yes (Consciousness, Breathing, BloodPumping, Moving, Manipulation) |
| Death (RimWorld rule, `bds-death`) | fast | Consciousness, blood fraction, lethal parts | dead | yes |
| Transport (PBPK): perfusion-limited exchange, partition, clearance, routes (inject, ingest, inhale, contact) | slow | substance defs; tissue perfusion, water/lipid fraction; Filtration, Detox | compartment amounts | no |
| Pharmacodynamics: effects as curves of concentration, dose or severity | slow | effect defs | modifiers on properties | no |
| Osmosis: water flux between compartment and blood by osmolarity difference | slow | substance osmotic coefficient, tissue permeability | compartment water, blood volume | no |
| Precipitation: amount above solubility becomes a solid phase in the compartment | slow | substance solubility | solid amount (a driver for effects) | no |
| Reactions: a substance consumes a tissue class's integrity at a rate law | slow | reaction defs | tissue integrity | no |
| Respiration and gas exchange: alveolar gas from ambient partial pressures, binding (O2, CO by affinity) | slow | gas substance defs, Breathing, ambient | blood gases | no |
| Barometric: barotrauma from pressure rate against the airway, ebullism below vapour pressure at tissue temperature | slow | ambient pressure, tissue temperature | insults, swelling (Scale) | no |
| Thermal: per-part temperature, conduction along attachments, convection and radiation to ambient, metabolic heat, burns and freezing | slow | heat capacity, conductivity, damage temperatures | temperature, insults | no |
| Ionizing dose: absorbed dose per tissue accumulates; effects keyed on dose with delays (acute radiation syndrome as data) | slow | radiosensitivity, dose rate | dose state | no |
| Geometry: part scale and density become body size and mass in Ymir; stiffness becomes joint softness | on change | part Scale, tissue density, stiffness | binding commands | no |
| Fatigue: muscle energy reserve consumed by work, restored by perfusion | slow | muscle family | strength factor | no |
| Vocal condition: tract area ceilings, perforations, fluid, glottis state, subglottal pressure | fast | airway tissues, Breathing, ambient | `VocalCondition` for AquaSynth | no |

Fast runs inside `Actor.Tick` at the physics rate; slow runs every Nth tick. "No" means Deru's later direction,
not the MVP.

## The property set and where its boundary sits

Every surveyed system keeps a closed set of target properties (Eyes `s4-effect-pattern`). That is not timidity: a
property means nothing until a mechanism reads it, and a name no mechanism reads is a lie the data tells. So the
set is closed, and **the boundary is the consumer test: a property is in the set exactly when one mechanism reads
it every tick or on change.** The set grows by mechanism cuts, never by data. Arbitrariness comes from the breadth
of mechanisms and from effects composing over them, not from open property names.

Families, each with one reader:

- **Tissue material** (per tissue): density, stiffness, thickness, cut, pierce and blunt resistance, structural,
  integrity (state), vascularity, nociception, water and lipid fraction, perfusion, permeability, heat capacity,
  conductivity, high and low damage temperature, radiosensitivity.
- **Organ function** (per tissue): its contribution to Consciousness, Breathing, BloodPumping, BloodFiltration,
  Detox, Digestion, Sight, Hearing, Voice, Moving, Manipulation.
- **Muscle** (per muscle tissue): specific force, contraction speed, energy reserve.
- **Nerve and perception** (per channel or part): gain, threshold, conduction delay, phantom rate, phantom intensity.
- **Drive** (per actor): the `DriveParameters` fields (`DriveState.cs:6-38`), and the Brain's deliberate latency.
- **Part geometry** (per part): scale. Mass is derived (density x volume), never a property of its own.

Attributes are derived, never stored: there is no stat sheet. Strength is muscle specific force through the
capacity ceiling; agility is contraction speed (PD gain scale and reflex latency); perception is nerve gain and
threshold; willpower is drive drain trait and resolve recovery; endurance is the energy reserve; mass is density;
size is scale; elasticity is stiffness. A second "Strength" number would be a second owner of the muscle ceiling.

Operations are closed too: multiply, add, and approach (move toward a value at a rate). An effect never writes
state; a mechanism reads the effective property, which is base (content) folded with the active modifiers,
recomputed each slow step. Booleans are properties with a factor (no breathing need is O2 demand x 0), so there is
no capability-tag effect kind (DF `CE_ADD_TAG`).

## Deru's examples against the design

| Example | Data or new? | What it needs |
|---|---|---|
| Targeted toxins for every organ system | data, after transport and PD | a substance with partitions toward the organ, an effect on that organ's integrity or function; Filtration and Detox loss slows its own clearance, so accumulation emerges |
| Atmospheric composition | new mechanism (respiration), then data | O2, CO2, N2 as gas substances; any mix, CO by binding affinity, anaesthetic gases are data after |
| Pressure, including vacuum | new mechanism (barometric), then data | hypoxia from respiration; ebullism (Armstrong limit is water's vapour pressure at body temperature) swells parts through Scale; barotrauma by rate |
| Temperature | new mechanism (thermal), then data | hot and cold zones, burns, freezing, fever as data |
| Radiation, non-ionizing | data on thermal | absorbed radiant flux is heat |
| Radiation, ionizing | new mechanism (dose), then data | dose state per tissue plus radiosensitivity; radiation sickness is effects keyed on dose with delays |
| Skin like woven carbon nanotubes | **data on the MVP** | skin tissue: cut and pierce resistance up, density down; blunt still passes through and bruises the meat under it |
| Hallucinations | new property (phantom rate and intensity in Perception), then data | phantom stimuli startle, drain attention and draw reactions; showing them to the player is question `bds-hallucination-shown` |
| Size | new binding capability, then data | Scale rebuilds the part's Ymir bodies and remeshes the puppet (`bds-sdf-puppet-skinned-remesh-on-topology`); waits on `ymir-break-and-remove` |
| Mass | property exists (density); binding needs a mass write | a Ymir mass-data command |
| Elasticity | property exists (stiffness); binding needs it | Ymir joint softness and restitution (the flail-bar ruling already wants softness) |
| Strength | data | specific force through the capacity ceiling; MVP has the ceiling |
| Intelligence | **no mechanism** | question `bds-intelligence-meaning` |
| Perception | data | nerve gain and threshold exist (`NerveParameters`) |
| Agility | small new reader, then data | contraction speed read by the binding (PD gain scale) and the reflex latency |
| Endurance | new mechanism (fatigue), then data | |
| Willpower | data | `DrainTrait`, `ResolveRecovery` exist |
| Dissolved bones | new mechanism (reactions), then data | integrity of bone falls; a part whose structural tissue has failed loses its joint limits and holds nothing; released mineral is a solute in the blood |
| Salty bones, osmotic consequences | new mechanisms (osmosis, precipitation) and one effect driver (joint speed) | salt dosed into bone draws water out of the blood (volume falls), leaks into blood (hypernatraemia), loads the kidneys (Filtration damage by concentration: data); crystals above solubility hurt in proportion to the joint's speed (the effect driver) |

## Identity, lifecycle, authority

| Kind | Named by | Lifecycle | Decides | Persisted |
|---|---|---|---|---|
| Body plan | content id | authored, immutable at runtime; the spawn template only | content (game), schema Lodur | CultCache (code default until `lodur-body-plan-cultcache`) |
| Part | session-unique stable part id (u64, never re-keyed), plus (plan id, part label) for its definition | spawned from a plan → intact or damaged → severed (its attachment removed; it is its own organism) → attached again (reattach or graft) → destroyed (gibbed: removed from physics, viscera are BDS presentation) → necrotic (no perfusion, later); a split creates new parts with new ids (`bds-cut-location`) | Lodur physiology; the session requests attach and split | no (a level persists what spawned it) |
| Attachment | (parent part id, child part id) plus an ordinal | created at spawn from the plan's joint, or by attach; removed by severing or destruction; conduits are a set on it (bone, vessel, nerve, airway); a graft starts with all four, a prop weld is not an attachment | Lodur physiology; Ymir executes the joint on command | no |
| Organism | derived: a connected component of parts over attachments | changes whenever an attachment is made or removed | derived | no |
| Actor | the brain part's id | exists while a part with Consciousness function is alive; dies with the brain | Lodur | no |
| Tissue | (part id, layer ordinal), definition by material id | created with its part; integrity, temperature, dose evolve; destroyed with its part | Lodur physiology | no |
| Compartment | (part id, layer ordinal): one per tissue, never free-floating; the central blood pool is the compartment of the part that holds the BloodPumping organ, the circulation its vessel-connected component | contents evolve every slow step; on severing the severed side keeps its own blood and substances; on attach the pools mix by volume; on destruction its contents spill (a gore event) | Lodur physiology (transport) | no |
| Wound | (part id, layer ordinal, ordinal) | opened by an insult → bleeding → clotting → closed | Lodur physiology | no |
| Substance (definition) | content id | authored, immutable | content | CultCache |
| Substance amount | (compartment, slot), slot bound per organism to a substance id | a slot binds when the substance first enters and frees when its total falls under epsilon; slots are bounded (16 per organism); a dose with no free slot is refused and counted, never silent | Lodur physiology; BDS only doses (route, part, substance, amount) | no |
| Effect (definition) | (owning definition id, ordinal): inside a substance, an exposure profile, or a reaction | authored, immutable | content | CultCache |
| Effect (instance) | (organism, effect definition, target) only for time-shaped effects (delay, peak, end) and severity-driven stages | onset → peak → end, or severity over time (RimWorld stages); a concentration-driven effect has no instance: it is recomputed from concentration each slow step | Lodur physiology | no |
| Effective property | (family, property, target) | derived each slow step from base and modifiers; nobody writes it | derived | no |
| Environment zone | BDS content id (a level object: gas mix, pressure, temperature, medium, dose rate, radiant sources) | authored in a level, changed by contraptions | BDS session | CultCache (level) |
| Environment sample | (part id, tick) | sampled by the session each slow step at each part, passed into the physiology tick like `BoxStimulus` | derived (session) | no |
| Vocal condition | (actor, tick) | derived each fast tick from airway tissues and capacities | derived (Lodur) | no |

## Authority map against Lodur today

- **Owner of the body's shape:** the body plan (data), built by one generic Ymir binding. `HumanoidRagdoll` is no
  longer an owner; it is a plan. `Limb`, `Muscle` and `Region` (`HumanoidRagdoll.cs:7-24` on the hands branch) are
  deleted, not aliased; consumers resolve labels against the plan once, at build.
- **Owner of the part graph at runtime:** Lodur physiology. Ymir creates and removes joints on command only
  (`one-severance-owner`); the session requests attach, graft and split; a prop weld is a session joint that
  physiology feels as an external joint (MVP map, "grab is felt").
- **Owner of tissue state, wounds, blood, substance amounts:** Lodur physiology. BDS reaches it through insults
  (built by the binding from Ymir facts and BDS's material table) and doses only.
- **Consciousness:** physiology's capacity is its only writer. `Actor.SetWillSlots`' consciousness half
  (`Actor.cs:101-105`) dies in the physiology cut; the attention half stays a seam until startle lands.
- **Muscle strength ceiling:** capacities times consciousness (unchanged from the MVP map); specific force joins
  it as content.
- **`DriveParameters`:** the record (`DriveState.cs:6-69`) stays the shape; the base is content, the parameters the
  drive steps with are the effective ones, derived. Nothing else writes a drive parameter.
- **Environment:** BDS session. Lodur reads samples passed in and keeps none.
- **Voice:** Lodur owns the vocal condition and the vocal drive (involuntary cries, gasps and screams as reflexes;
  voluntary speech weighted by will, `will-is-the-budget`); BDS owns what he says (the simlish stream as
  `PhoneticIntent` content); AquaSynth owns articulatory realization under the condition as morphology, and the DSP.
- **The hands branch** (`nibu/lodur-ragdoll-hands`, reported h3) lands first; its geometry, gains, rest curl and
  thumb frame move into the humanoid plan verbatim, with their comments.

**Derived state:** organism, actor set, effective properties, capacities, impairment states, mass, vocal condition,
environment samples, dead. **Forbidden writers:** an effect writing state (effects only modify properties or
rates a mechanism reads); BDS or the client writing tissue, compartment or graph state; a substance id or potion
name in Lodur's `src` (every potion is data: negative grep); any per-potion or per-wound branch (`drive-is-state`);
Ymir removing a joint by threshold; the enums. **Shared paths:** inject, ingest, inhale and contact are one
`Dose(route, part, substance, amount)`; environment gases enter through the same lung transport as an inhaled dose;
reattach is a graft onto the part's original socket (one `Attach`); blades, bullets, crush, burns, ebullism and
barotrauma are all insults into one resolver; a severed and a destroyed part leave through the same graph removal.
**Deletion line:** the enums and the hard-coded build (cut 1); the consciousness seam (physiology cut); the MVP map's
"never resurrected in MVP (`bds-reattach`)" is superseded by `bds-reattach-both-and-grafting` (later, not MVP).

## Grafting on the graph

Grafting needs nothing special once the body is a graph: `Attach(parent part, child part, frame, conduits)` adds an
edge. The frame comes from the two cut faces the tool presses together (a ball-joint doll keeps sockets fixed at
first sight, `doll-severed-keeps-socket`, so a graft onto a cup reuses the cup's frame; a graft onto a cut face
uses the face). The joint kind is the child's old one where it had one, else spherical with the plan's limits
for that tissue class. Circulation pools mix, nerves splice by `bds-graft-control`, the airway joins only when
both sides carry one. Nothing in the graph assumes a species: a horse plan and a man plan are both plans, and the
merged organism is a component of their parts. Severing is the same edge removed. Behaviour over a strange
topology stays the r4 line: Lodur's motor layers drive the joints a plan's poses name, and a grafted part with no
pose entry holds its rest pose under will; topology-agnostic learned control stays out of scope.

## The vocal tract

The airway is tissue in the graph: trachea and larynx in the neck part, pharynx, oral and nasal cavities in the
head part, each an airway tissue with a section index range on a Pink Trombone-class tube (44 tract and 28 nasal
sections, MIT; Eyes `s5-pink-trombone`). Each fast tick Lodur derives `VocalCondition`: per-section area ceilings
(a screwdriver through the trachea is a foreign body that narrows its sections, its wound a perforation that opens
a side branch like the nasal junction), fluid in the airway (blood gurgles as noise), glottis state (larynx
integrity bounds tenseness), subglottal pressure (Breathing and lung tissue), and the ambient medium (vacuum
carries no sound). AquaSynth already has the seam: its `ArticulatoryPlan` is morphology-constrained and its
`ArticulatoryConstraintReport` names what a body cannot say; `VocalCondition` is that morphology, per tick. No
source maps tract damage to synthesizer parameters (Eyes `s5-damage-mapping-gap`); the condition is ours.

## Cost budget

- Physics and the nervous system: **120 Hz**, as today (the ragdoll's gains are tuned at 120 Hz).
- Fast physiology inside `Actor.Tick` at 120 Hz: insults, bleeding, nociception, death, vocal condition. Budget
  **50 us per puppet per tick**, zero allocation.
- Slow physiology **every 12th tick (10 Hz)**: capacities, transport, PD, osmosis, gas, thermal, dose, effective
  properties. Budget **500 us per puppet per slow step**, zero allocation; measured 140 us at 128 compartments x 16
  slots x 64 effects (probe 4), so the budget holds with a 3x margin. Several puppets stagger their slow step
  across the 12 ticks, so eight puppets cost about 1.1 ms in each of their own ticks, not 9 ms in one.
- Why not Pulse's 50 Hz: Pulse resolves a pulsatile cardiovascular circuit; this model carries mean flows, and every
  slow time constant is seconds or longer, integrated exactly (exponential) for the linear exchanges.
- Audio: AquaSynth at 48 kHz from the 120 Hz condition, interpolated.

## MVP boundary

**MVP (`bds-mvp-scope`, Deru's dismemberment and bleeding, `bds-death`, meat-core):** body plan as data (cut 1);
the part graph as the authored tree; tissue stacks with the silicone skin outermost and meat, bone and organs
under it; insult resolution through the stack; wounds, bleeding, blood fraction; nociception per part into
Perception; capacities (Consciousness, Breathing, BloodPumping, Moving, Manipulation) with consciousness as the
only writer of its slot and the strength ceiling; the death rule; severing at joints. Wound faces show the white
rim over red meat because the stack says so, not because a shader guesses.

**Deru's later direction (follow-ups, each a mechanism cut that widens the property set):** substances,
transport and effects (the first potion); osmosis and precipitation; reactions; respiration and the atmosphere;
barometric (vacuum); thermal; ionizing dose; geometry effects (size, mass, elasticity) after
`ymir-break-and-remove`; fatigue; phantom perception; grafting and true reattachment; splitting a segment; the
vocal tract and voice.

## Rejected

Embedding Pulse or BioGears; HumMod; data-declared property names; per-potion scripts; an attribute stat sheet;
capability tags; voxel or finite-element tissue; one global circuit not keyed to parts; keeping the enums as
aliases over plan ordinals. Each is an `idea` of pass `physiology-map` with its Recorded resolution.
