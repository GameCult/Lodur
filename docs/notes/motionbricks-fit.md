# MotionBricks fit survey

Question: can NVIDIA MotionBricks (arXiv 2604.24833, `NVlabs/motionbricks`,
Hugging Face weights) serve as an intent layer for Lodur: characters with
nervous systems on Ymir, headless, state read as a structure-of-arrays view?

Sources were read on 2026-11-06 at `NVlabs/motionbricks` commit `54fc563`
(2026-10-23) unless stated. This is a survey; nothing was run. Items not
established are listed in the last section.

## Verdict

**Optional tier, off by default, never core.** Keep the licence question out of
the repo (weights are fetched by the user, not committed). Do not adopt now.

- The licence is not the blocker. The NVIDIA Open Model Agreement carries no
  field-of-use limit and no no-training clause (quoted below). One clause
  (indemnity) needs a human to rule on it against the always-open-source rule.
- The runtime is the blocker. The shipped runtime is Python plus TensorRT and
  needs an NVIDIA GPU. Lodur's core is engine-free C# with a headless,
  deterministic Ymir world. A CPU ONNX path is documented only as an example
  snippet; its speed and determinism are unmeasured.
- The output is a kinematic pose stream on a fixed human skeleton. It does not
  claim to be physically feasible, which is the right shape for a *target*, and
  the wrong shape for a body.

Revisit when (a) a CPU ONNX run of the SOMA model is measured, and (b) Lodur has
a body with a humanoid rig to track poses at all (Ymir's SoA world has spheres
and static boxes only today).

## 1. Licences

| Part | Licence | Source |
|---|---|---|
| Code | Apache-2.0 | README "License" |
| Docs and media | CC-BY-4.0 | README "License" |
| Models (SOMA and G1 checkpoints, bundled ONNX) | NVIDIA Open Model Agreement (v. March 9, 2026; release date April 2, 2026) | README; `motionbricks/assets/onnx/LICENSE.md`; SOMA model card |
| Sample BVH/CSV motions | NVIDIA Sample Data Evaluation License | `assets/motions/LICENSE.txt` |
| Python runtime's TensorRT wheels | NVIDIA TensorRT Software License Agreement (proprietary, distributed with the packages) | README "License" |

Agreement text: <https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-agreement/>.
Section numbers did not survive the page; clauses are cited by heading.

### Open Model Agreement, against the questions asked

- **Field of use.** None. The preamble: "Works are commercially usable. You are
  free to create and distribute Derivative Works. NVIDIA does not claim
  ownership to any outputs generated using the Works or Derivative Works."
  No clause restricts a domain or use case.
- **Training on outputs or on the model.** No clause restricts it. The only
  termination trigger is: "If You institute patent or copyright litigation
  against any entity ... alleging that the Work or an output from the Work
  constitutes direct or contributory patent or copyright infringement, then any
  licenses granted to You under this License for that Work shall terminate."
- **Grant.** "a perpetual, worldwide, non-exclusive, no-charge, royalty-free,
  irrevocable license to reproduce, prepare Derivative Works of, publicly
  display, publicly perform, sublicense, and distribute the Work and such
  Derivative Works in source or object form." (Irrevocable, subject to the
  litigation clause above.)
- **Derivatives.** "Derivative Works shall not include works that remain
  separable from, or merely link (or bind by name) to the interfaces of, the
  Work." Lodur calling the model through ONNX is therefore not a Derivative
  Work of it. Fine-tuned or re-exported weights would be.
- **Redistribution.** Allowed "with or without modifications, in source or
  object form" on three conditions: give recipients a copy of the licence;
  retain copyright, patent, trademark and attribution notices in the source
  form of distributed Derivative Works; and, "If the Work includes a 'NOTICE'
  text file", Derivative Works must carry "Licensed by NVIDIA Corporation under
  the NVIDIA Open Model Agreement." (The repo has no `NOTICE` file at this
  commit, so the attribution clause has nothing to trigger on in the code
  repo; the Hugging Face checkpoint repos were not checked.) You may add
  "additional or different license terms" to your own modifications.
- **Other terms.** Trademarks not granted. As-is warranty and liability
  disclaimers. Delaware law, Santa Clara County courts. Export and sanctions
  compliance.
- **Indemnity (the clause to rule on).** Under "Accepting Warranty or
  Additional Liability": "You will indemnify and hold harmless NVIDIA from and
  against any claim by any third party arising out of or related to your use or
  distribution of the Works, Derivative Works thereof, or output from the Works
  or Derivative Works." It sits in a section about offering warranties, but the
  sentence is not limited to that case on its face. OSI-approved licences do not
  carry an unconditional user indemnity. Whether that counts against "always
  open source" is a legal and operator call, not an engineering one.

### Fit with GameCult's always-open-source rule

- Code (Apache-2.0): compatible.
- Weights: permissive, redistributable and sublicensable, no field-of-use
  limit. This is not an OSI licence and the indemnity sentence is unusual, so
  "compatible" is not established until someone rules on it.
- Cleanest posture, if ever adopted: Lodur's repo contains no weights. The
  tier is a documented optional download that the user fetches themselves
  under NVIDIA's terms. Lodur's own code stays open and separable (the
  Derivative Works carve-out above). Do not commit the sample motions: they
  are internal evaluation only and may not be redistributed.
- Not a lawyer's reading. Nothing here is legal advice.

## 2. Training data

- Trained on Bones RigPlay 1 (RP1): 313,526 clips selected from about 350,000
  clips / 700 hours, a 4,881-clip validation subset disjoint from training
  (SOMA model card, "Training Dataset", "Testing Dataset").
- Captured by Bones in a studio ("Automatic/Sensors"), processed by NVIDIA into
  the 30-joint SOMA skeleton at 30 Hz. The corpus is not distributed with the
  model: "Access to the dataset is governed separately from the model weights."
- Bones' own page (<https://bones.studio/datasets>) describes RP01 as
  "Enterprise-grade · IP-clean · Ready to license" and "full IP clearance",
  optical Vicon mocap at 120 fps, 170+ performers. That is a commercial
  product. **No public RP1 licence text was found**, so the terms binding
  NVIDIA's weights to RP1 are unread.
- The separate BONES-SEED dataset (HF `bones-studio/seed`, gated, licence at
  <https://bones.studio/info/seed-license>) is a different licence from RP1 and
  is not what the weights were trained on, so it does not bind Lodur. It
  matters only if Lodur wanted to fine-tune on it: SEED is restricted to
  "Academic User" or "Qualifying Startup" (under $1,000,000 gross revenue),
  forbids public mirrors, and forbids using it "to train, fine-tune, or
  condition any generative model whose primary purpose ... is to generate
  motion capture data", which excludes fine-tuning MotionBricks on it. (Models
  that "consume the Dataset ... and produce non-data outputs", such as
  control policies, are expressly allowed.) Closed data, so it fails the
  open-source rule for anything Lodur ships.
- The model's outputs are motion, not recordings; the model card says nothing
  on whether generated motion can reproduce training clips. Unverified.

## 3. Runtime

- **Official runtime**: Python on NVIDIA Warp, Newton (viewer and scene) and
  TensorRT. README: "The runtime uses TensorRT, which requires an NVIDIA GPU."
  Tested on Windows and Linux x86-64 with an RTX 4090 only. `runtime_api.md`:
  "Engines need a CUDA device." The first launch of each model builds a
  TensorRT plan, which takes minutes, cached in `.trt-cache/`.
- **Headless**: `runtime_api.md` ships "two short scripts in `examples` that
  call the library without a viewer or the apps". Headless in the sense of
  no window: yes by the doc. Headless in the sense of no GPU: no.
- **Model**: SOMA is about 200M parameters (VQ-VAE 23.4M, pose predictor
  136.5M, root predictor 40.4M), ONNX opset 17, FP32 and mixed FP16 files,
  variable batch N. Interface (`onnx_inference.md`): inputs `global_root_values`
  [N,8,5], `local_root_values` [N,8,4], `local_poses` [N,8,267], three int32
  masks [N,8] and `allowed_pred_num_tokens` [N,11]; outputs
  `pred_global_motions` [N,64,366] and `pred_num_tokens` [N]. Files use Git LFS.
- **Latency** (`onnx_inference.md`, TensorRT plan alone, RTX 4090, Windows,
  inputs already on the GPU): SOMA FP32 2.50 ms at N=1 and 5.28 ms at N=32
  (6,063 requests/s); mixed FP16 1.28 ms at N=1 and 3.93 ms at N=32. Input
  preparation, decoding and endpoint correction are excluded. The 15,000 FPS /
  2 ms figures in the paper abstract are consistent with this. Starfire's GTX
  1070 (8 GB, shared) has no tensor-core FP16 path and is untested.
- **CPU path**: `onnx_inference.md` shows an `onnxruntime` session with
  `providers=["CPUExecutionProvider"]`, but the example loads the G1 model, not
  SOMA. The SOMA card says "A CPU-versus-GPU inference comparison is not
  provided." CPU speed for a 200M-parameter model is unmeasured here.
- **ONNX leaves work undone**: the ONNX output "includes the boundary frames
  and has no endpoint correction. The SOMA and G1 engines remove the boundary
  frames and apply endpoint correction." The SOMA engine also decodes the
  foot contacts packed in the output, and finds the model's 30 joints from the
  input rig. A C# consumer would reimplement all of that against Python
  reference code. Real cost, not estimated.
- **Determinism**: not documented. The card warns "Mixed FP16 and FP32 can
  produce different motion ... Differences can accumulate when generated
  motion is fed back." Ymir's own stance is byte-equal lanes on the same
  build, not across CPUs. A model whose output differs across precision and
  backend, and which is fed its own output, cannot be replayed from a
  command stream unless its outputs are recorded. Whether the graph itself is
  free of sampling randomness is unverified.
- **Platforms**: Windows and Linux x86-64 only; no macOS or ARM claim.

## 4. Pose format and skeleton

SOMA card ("Model Space", "Inference"):

- 30-joint SOMA skeleton, 30 Hz, Y-up, metres. One output frame is 366 floats:
  root position and heading (5), joint positions (87), 6D rotations (180),
  joint velocities (90), foot contacts (4). 6D means the first two columns of
  a rotation matrix.
- One generation returns 18 to 58 valid frames (the ONNX tensor carries 24 to
  64 raw frames, between boundary frames), conditioned on 4 context poses and
  1 to 4 target poses; generations chain by feeding the last four poses back.
  Conditioning is poses and constraints only: no text, no scene geometry.
- The skeleton is fixed. Another rig needs retargeting (a SOMA Retargeter
  exists; not read).
- Stated limit, verbatim from the card: "Kinematic motion generation does not
  establish dynamic feasibility, physical robot tracking, collision avoidance,
  or safe contact. Physical execution requires a separate controller." And:
  "Evaluate generated motion in the intended application. Physical execution
  requires a separate tracking and safety system."

## 5. How a tracker over Ymir's SoA world would follow it

Today (`Ymir/docs/soa-world.md`): headless Box3D world, lanes for stable id,
kind, position, rotation, linear and angular velocity, spheres and static boxes
only. Its own "needed next" list is joints, capsule/box bodies, a torque write
path, joint state lanes and contact facts. Lodur's `body-one-interface` says the
body consumes only targets and per-muscle strength.

Reading, not tested:

1. A MotionBricks tier would sit under *behaviour*. Behaviour picks a target
   pose set (a few SOMA poses placed ahead of the character) plus per-muscle
   strength; the model fills the in-between frames at 30 Hz.
2. The body maps SOMA joint rotations (6D to quaternion) onto the Ymir
   ragdoll's joints, retargeted if the ragdoll is not the SOMA 30-joint rig,
   and drives each joint's PD or motor target toward the frame for the current
   tick. Strength scales the torque limit. A limb with strength loss lags or
   misses the target; that is the "motor system that may fail".
3. The measured ragdoll state (the SoA lanes plus joint state) feeds back as
   perception, and, once the character is no longer on the kinematic path (fall,
   impairment), the context poses for the next generation come from the *real*
   body, not the previous output (the runtime's `MotionRecorder` / history
   already takes context from "the last four played frames"). A body that is
   on the floor is out of distribution for a model trained on upright studio
   mocap; expect garbage targets then and fall back to Lodur's own ground
   behaviours (design target: v1 ships those).
4. The model runs per character, not per physics tick: 30 Hz targets,
   one generation per 18 to 58 frames. It is intent, below the nervous system
   and above the tracker; reflexes overlay it and never wait for it.

Missing for this to run at all: a jointed humanoid in Ymir's SoA world, a
joint motor write path, and a SOMA-to-ragdoll rig mapping. All three are Ymir
or Lodur work that exists whether or not MotionBricks is used.

## 6. Fit

- Fit: target poses for a motor system that may fail. Kinematic, human-only,
  upright-trained, poses in and poses out. Not a replacement for the physical
  body, and the card says as much.
- Competing options already in the design target: Lodur's own
  behaviours emit targets directly (crawl, writhe); a small learned tracker
  (DReCon scale) is v2. MotionBricks is the only one that supplies natural
  locomotion and transitions between authored pose targets, and it is the
  heaviest by orders of magnitude.
- Lodur's characters are meant to be hurt, not graceful. Whether upright
  mocap-derived targets help the interesting 20 seconds (crawl, shock, pass
  out) is doubtful. They would help locomotion before injury.

## Unverified

- Whether the Open Model Agreement's indemnity sentence is acceptable under
  GameCult's open-source rule (needs a ruling, and likely a lawyer).
- RP1 licence text and what it says, if anything, about models trained on it.
- Whether the Hugging Face checkpoint repos ship a `NOTICE` file (none in the
  GitHub repo at `54fc563`), which would trigger the attribution clause.
- SOMA ONNX on CPU: speed, memory, and agreement with TensorRT output.
- Bit determinism of one backend across runs; any sampling randomness inside
  the graph.
- Behaviour on a GTX 1070 (no tensor-core FP16) and under GPU sharing.
- Quality of targets when the body is off the training distribution (prone,
  one-legged).
- SOMA Retargeter licence and fit for Lodur's rig; Lodur has no rig yet.
- Effort to port boundary-frame trimming, endpoint correction and foot-contact
  decoding to C#.
- The paper (arXiv 2604.24833) was read at abstract level only.
