# Flesh cost probes

Probes behind `docs/flesh-map.md` and the Nibu research notes
`lodur:research:flesh-bones-map.probe-flesh-cost`, `.probe-flesh-commands` and
`.probe-skin-sieve`.

- `ymir_particles.probe.patch`: the probe's changes to Ymir's
  `native/Ymir.Box3D/ymir_particles.{c,h}` at GameCult/Ymir d82da6d (pin
  skipping by flag, a no-contact phase switch). Probe only; not a Ymir change.
- `fella.c`: one or more Fellas of skin and muscle sheets on rigid bones,
  stepped at 120 Hz x 4 substeps. `matrix.sh` / `matrix2.sh` run the
  configurations interleaved, five repetitions; `matrix*.out` are the runs the
  notes cite.
- `sieve.c`: a skin sheet attached along a bone under fluid and solid load at
  4 substeps.

Build against a patched copy of the two Ymir files with the same flags Ymir
uses (`-O2 -std=c17 -ffp-contract=off`).
