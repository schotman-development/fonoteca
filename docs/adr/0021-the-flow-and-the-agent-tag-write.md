# 0021 — One flow from scan to tag write, and the tag write for agents

**Status:** accepted, 2026-11-19

## Context

The tag write was the one pass nobody might automate: three buttons and no
fourth route, and `/mcp` omitted it. Running the library from a scan to written
files meant a person pressing seven buttons in order, or an agent that could
start six of them and then stop. Playlists were reachable over the web client
and Subsonic only.

## Decision

The owner's choice: **an agent may run the whole chain, the tag write
included.**

- `POST /api/library/flow` (`start_flow`) runs scan, identify, enrich,
  attribute, probe, analyse and then the library tag write, each once the one
  before has released the gate. `GET` reports the step it is on, the steps done
  and why it stopped; `DELETE` stops after the pass it is on. `writeTags=false`
  leaves the last step out. `LibraryFlowService` only chains: every step is the
  service its button starts, so the gate and every refusal stay theirs.
- The first step that cannot start or records an error ends the flow and is
  named. The tag write never runs over a catalogue a pass just failed to build.
  A refused album is a question for a person, not a failure.
- `write_tags` (library, album, artist scope) is a tool, journalled as the
  agent; `undo_folder_edit` takes it back per folder. `Fonoteca:AllowFileMutation`
  still gates the bytes: with it off the flow's last step is a dry run.
- Playlist tools call the same handlers as the web client. A smart playlist
  made over `/mcp` has no owner, so rules on hearts, plays and tags match
  nothing for it.

## Not changed

Trash, move, upload and Qobuz stay off `/mcp`. Nothing runs a flow on its own:
it starts when a caller asks.
