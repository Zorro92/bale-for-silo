@AGENTS.md

## Claude Code

- After any change to the Xbox app's UI or behavior, run the `verify-on-xbox` skill
  (`.claude/skills/verify-on-xbox`) before saying it works.
- Before implementing a screen or interaction, check how Silo's Android TV / Apple TV app does it
  (sources in AGENTS.md) and say which file you matched.
- `XBOX_HOST` isn't in the repo. If it isn't set, ask the user for the console's IP (Dev Home shows it).
