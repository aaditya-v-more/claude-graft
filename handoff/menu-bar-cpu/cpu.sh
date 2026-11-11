#!/bin/zsh
# Read-only. How much of one core Graft, its relay, ControlCenter and the
# WindowServer used over a stretch, worked out from the CPU time each had used
# at either end of it: a busy moment counts for what it cost, not for whether
# a sample happened to land on it. ControlCenter and the WindowServer do the
# menu bar's work on an item's behalf, but anything on screen moves them too,
# so they are context rather than a verdict.
#
#     cpu.sh <seconds> <label> [seconds to settle first, 15 if left out]
#
# The settle is for bringing a Claude forward after starting this from a
# terminal, and for the bar to catch up with it. What it finds is printed and
# added to cpu.txt beside this file.
secs=${1:?how many seconds to measure}
label=${2:?a label for the line}
settle=${3:-15}
here=${0:A:h}

snapshot() {
    ps -Ao pid=,time=,rss=,comm= | awk '$0 ~ /\/(ClaudeGraft|graft-relay|ControlCenter|WindowServer)$/ {
        n = split($2, part, ":"); used = 0
        for (i = 1; i <= n; i++) used = used * 60 + part[i]
        path = $0; sub(/^ *[0-9]+ +[^ ]+ +[0-9]+ +/, "", path)
        printf "%s\t%.2f\t%d\t%s\n", $1, used, $3, path
    }'
}

print -r -- "Settling for ${settle} s: bring forward what \"$label\" needs, then touch nothing."
sleep $settle
began=$(date '+%H:%M:%S')
before=$(snapshot)
sleep $secs
after=$(snapshot)
{
    print -r -- "$label · from $began · $secs s"
    awk -F'\t' -v secs=$secs '
        NR == FNR { used[$1] = $2; name[$1] = $4; next }
        {
            short = $4; sub(/.*\//, "", short)
            seen[$1] = 1
            if (!($1 in used)) { printf "  %-14s pid %-6s started during the stretch, not measured\n", short, $1; next }
            app = ""
            if (short == "ClaudeGraft" || short == "graft-relay") { app = $4; sub(/\.app\/.*/, ".app", app); app = "  " app }
            printf "  %-14s pid %-6s %5.2f%% of a core  %4d MB%s\n", short, $1, ($2 - used[$1]) / secs * 100, $3 / 1024, app
        }
        END {
            for (pid in used) if (!(pid in seen)) {
                short = name[pid]; sub(/.*\//, "", short)
                printf "  %-14s pid %-6s quit during the stretch, not measured\n", short, pid
            }
        }' <(print -r -- "$before") <(print -r -- "$after")
} | tee -a "$here/cpu.txt"
