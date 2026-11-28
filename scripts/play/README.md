# Play helpers (main session only)

Small scripts the main session uses to drive the game through the mod's TCP bridge (port 7878) without the MCP
server. Fair play rules apply exactly as through the MCP tools.

- `call.mjs <method> '<json>' [wait_s]`: one bridge call; with `wait_s` it polls `get_task` until the task ends
  (prints `RESULT`, progress lines, `DONE`/`TIMEOUT`, `EVENTS`). Never hangs: hard deadline `wait_s + 60 s`
  (90 s without wait_s), exits on a closed connection or a closed stdout pipe (`| head -1`).
- `cave_loop.sh N`: N rounds of explore_caves (stops on life crystals), collect + use crystals, mine nearby veins.
- `mine_nearby.sh [n] [radius]`: mine the nearest visible ore veins.
- `farm.sh X` / `Y=… farm2.sh X`: night farming rounds at x=X (attack all, pick up), until dawn.
- `caves.py`: summarises an explore_caves result.

Rule for scripts: every loop must end on its own (bounded iterations, and every bridge call has a deadline), and
background jobs must be fully detached (`setsid … </dev/null >/dev/null 2>&1 &`) so they never keep a caller's
output pipe open.
