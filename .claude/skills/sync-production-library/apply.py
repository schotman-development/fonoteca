"""Apply plan.py's plan: new folders, files and links first; changed files replaced in place, production's copy kept in the trash; stale files moved to the trash; emptied folders removed. Usage: apply.py plan.json "/mnt/vault/media/music-trash/<date> sync"."""
import json, os, sys

T, P = "/mnt/vault/fonoteca-test/music", "/mnt/vault/media/music"
plan, TRASH = json.load(open(sys.argv[1])), sys.argv[2]
assert TRASH.startswith("/mnt/vault/media/music-trash/") and not os.path.exists(TRASH), TRASH
assert not plan["links_stale"], "a link changed or went: do it by hand"
for d in sorted(plan["dirs_new"]):
    os.mkdir(os.path.join(P, d))
    os.chmod(os.path.join(P, d), os.stat(os.path.join(T, d)).st_mode & 0o7777)
for p in plan["new"]:
    os.link(os.path.join(T, p), os.path.join(P, p))
for p, target in plan["links_new"]:
    os.symlink(target, os.path.join(P, p))
for p in plan["changed"]:
    target, kept = os.path.join(P, p), os.path.join(TRASH, p)
    os.makedirs(os.path.dirname(kept), exist_ok=True)
    os.link(target, kept)
    tmp = os.path.join(os.path.dirname(target), "." + os.path.basename(target) + ".sync")
    os.link(os.path.join(T, p), tmp)
    os.replace(tmp, target)
for p in plan["stale"]:
    kept = os.path.join(TRASH, p)
    os.makedirs(os.path.dirname(kept), exist_ok=True)
    os.rename(os.path.join(P, p), kept)
for d in sorted(plan["dirs_stale"], key=lambda d: -d.count("/")):
    os.rmdir(os.path.join(P, d))
print(f"made {len(plan['dirs_new'])} folders, linked {len(plan['new'])} files and {len(plan['links_new'])} links, "
      f"replaced {len(plan['changed'])}, moved {len(plan['stale'])} to the trash, removed {len(plan['dirs_stale'])} folders")
