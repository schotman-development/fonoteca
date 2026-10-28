"""Read-only: compare test and production music trees by path and inode; write plan.json."""
import json, os, sys

TEST, PROD = "/mnt/vault/fonoteca-test/music", "/mnt/vault/media/music"
BACKUPS = ["/mnt/vault/fonoteca-test/music-trash", "/mnt/vault/fonoteca-test/music-replaced"]
AUDIO = {".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".wv", ".ape", ".dsf", ".dff", ".alac", ".wma"}

def walk(root, skip_hidden_fonoteca=False):
    files, links, dirs = {}, {}, set()
    stack = [""]
    while stack:
        rel = stack.pop()
        with os.scandir(os.path.join(root, rel)) as it:
            for e in it:
                p = os.path.join(rel, e.name) if rel else e.name
                if not rel and skip_hidden_fonoteca and e.name.startswith(".fonoteca-"):
                    continue
                if e.is_symlink():
                    links[p] = os.readlink(e.path)
                elif e.is_dir(follow_symlinks=False):
                    dirs.add(p); stack.append(p)
                else:
                    st = e.stat(follow_symlinks=False)
                    files[p] = (st.st_ino, st.st_size)
    return files, links, dirs

t_files, t_links, t_dirs = walk(TEST, True)
p_files, p_links, p_dirs = walk(PROD)
backup_inodes = set()
for b in BACKUPS:
    if os.path.isdir(b):
        f, _, _ = walk(b)
        backup_inodes |= {ino for ino, _ in f.values()}
test_inodes = {ino for ino, _ in t_files.values()}

new = sorted(p for p in t_files if p not in p_files)
stale = sorted(p for p in p_files if p not in t_files)
changed = sorted(p for p in t_files if p in p_files and t_files[p][0] != p_files[p][0])
only_copy = [p for p in stale + changed if p_files[p][0] not in test_inodes | backup_inodes]
is_audio = lambda p: os.path.splitext(p)[1].lower() in AUDIO

plan = {
    "new": new, "stale": stale, "changed": changed, "only_copy": only_copy,
    "links_new": sorted(set(t_links.items()) - set(p_links.items())),
    "links_stale": sorted(set(p_links.items()) - set(t_links.items())),
    "dirs_new": sorted(t_dirs - p_dirs), "dirs_stale": sorted(p_dirs - t_dirs),
}
json.dump(plan, open(sys.argv[1], "w"), indent=1)
gb = lambda paths, side: sum(side[p][1] for p in paths) / 1e9
print(f"test {len(t_files)} files, prod {len(p_files)} files")
print(f"new {len(new)} ({gb(new, t_files):.2f} GB), audio {sum(map(is_audio, new))}")
print(f"changed {len(changed)} ({gb(changed, p_files):.2f} GB prod copies), audio {sum(map(is_audio, changed))}")
print(f"stale {len(stale)} ({gb(stale, p_files):.2f} GB), audio {sum(map(is_audio, stale))}")
print(f"only copy in prod: {len(only_copy)}, audio {sum(map(is_audio, only_copy))}")
print(f"links new {len(plan['links_new'])}, stale {len(plan['links_stale'])}; dirs new {len(plan['dirs_new'])}, stale {len(plan['dirs_stale'])}")
