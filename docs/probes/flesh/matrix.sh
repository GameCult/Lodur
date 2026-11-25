#!/bin/sh
# name|env|args
CONF='ship_1pin||1 0.08 0.10 0 0 0 0 600
ship_4pin||4 0.08 0.10 0 0 0 0 600
pin_1||1 0.08 0.10 0 1 1 0 600
pin_4||4 0.08 0.10 0 1 1 0 600
nc_4_forearms|NOCONTACT=1|4 0.08 0.10 1 1 1 0 600
nc_4_one_live|NOCONTACT=1|4 0.08 0.10 2 1 1 0 600
nc_4_one_live_gut|NOCONTACT=1|4 0.08 0.10 2 1 1 1 600
c_4_one_live|CONTACT=1|4 0.08 0.10 2 1 1 0 600
nc_4_all_live|NOCONTACT=1|4 0.08 0.10 3 1 1 0 600
nc_1_live|NOCONTACT=1|1 0.08 0.10 3 1 1 0 600'
for rep in 1 2 3 4 5; do
  echo "$CONF" | while IFS='|' read name env args; do
    if [ -n "$env" ]; then out=$(env $env ./fella.exe $args); else out=$(./fella.exe $args); fi
    echo "$rep $name $(echo "$out" | grep -o 'median [0-9.]*' )"
  done
done
