#!/bin/sh
CONF='A_ship_pinned_1|MINRING=4|1 0.10 0.14 0 0 0 0 600
B_skipflag_pinned_1|MINRING=4|1 0.10 0.14 0 1 1 0 600
D_forearm_live_1|MINRING=4 NOCONTACT=1 ABSENT=1|1 0.10 0.14 1 1 1 0 600
E_fella_live_1|MINRING=4 NOCONTACT=1|1 0.10 0.14 3 1 1 0 600
F_fella_live_gut_1|MINRING=4 NOCONTACT=1|1 0.10 0.14 3 1 1 1 600
G_fella_live_contact_1|MINRING=4|1 0.10 0.14 3 1 1 0 600
I_fella_live_heldby_1|MINRING=4 NOCONTACT=1|1 0.10 0.14 3 1 0 0 600
H_skipflag_pinned_4|MINRING=4|4 0.10 0.14 0 1 1 0 600
J_4_one_live_absent|MINRING=4 NOCONTACT=1 ABSENT=1|4 0.10 0.14 2 1 1 0 600
K_4_one_live_gut_absent|MINRING=4 NOCONTACT=1 ABSENT=1|4 0.10 0.14 2 1 1 1 600'
for rep in 1 2 3 4 5; do
  echo "$CONF" | while IFS='|' read name env args; do
    out=$(env $env ./fella.exe $args)
    echo "$rep $name $(echo "$out" | grep -o 'median [0-9.]*') $(echo "$out" | grep -o 'live particles [0-9]*')"
  done
done
