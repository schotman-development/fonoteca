"""Read-only: for each changed audio path in a plan, compare production's and test's decoded audio and ALBUM tag. Usage: compare.py plan.json out.jsonl"""
import json, os, subprocess, sys
from concurrent.futures import ThreadPoolExecutor

T, P = "/mnt/vault/fonoteca-test/music", "/mnt/vault/media/music"
AUDIO = (".flac", ".mp3", ".m4a")
plan, out_path = json.load(open(sys.argv[1])), sys.argv[2]
done = set()
if os.path.exists(out_path):
    done = {json.loads(line)["path"] for line in open(out_path)}


def audio_md5(path):
    run = subprocess.run(["ffmpeg", "-nostdin", "-v", "error", "-i", path, "-map", "0:a", "-f", "md5", "-"],
                         capture_output=True, text=True, stdin=subprocess.DEVNULL)
    return run.stdout.strip() or "error: " + run.stderr.strip()[:200]


def album_tag(path):
    run = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format_tags", "-of", "json", path],
                         capture_output=True, text=True, stdin=subprocess.DEVNULL)
    tags = json.loads(run.stdout or "{}").get("format", {}).get("tags", {})
    return next((v for k, v in tags.items() if k.lower() == "album"), None)


def id3_gap(path):
    with open(path, "rb") as f:
        head = f.read(10)
        if head[:3] != b"ID3":
            return None
        size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9]
        f.seek(10 + size)
        return f.read(1) == b"\x00"


def compare(rel):
    t, p = os.path.join(T, rel), os.path.join(P, rel)
    row = {"path": rel, "md5_test": audio_md5(t), "md5_prod": audio_md5(p), "album_test": album_tag(t), "album_prod": album_tag(p)}
    if rel.lower().endswith(".mp3"):
        row["gap_test"], row["gap_prod"] = id3_gap(t), id3_gap(p)
    return row


todo = [p for p in plan["changed"] if p.lower().endswith(AUDIO) and p not in done]
with open(out_path, "a") as out, ThreadPoolExecutor(4) as pool:
    for row in pool.map(compare, todo):
        out.write(json.dumps(row, ensure_ascii=False) + "\n")
        out.flush()
print(f"compared {len(todo)}")
