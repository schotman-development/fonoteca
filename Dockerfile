FROM python:3.12-slim

WORKDIR /app

# Copied alone so a source change does not re-run pip.
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app/ app/
COPY static/ static/
COPY templates/ templates/
COPY main.py cli.py ./

# config.py resolves a relative LIBRARY_PATH/DATA_PATH against the repo root
# (BASE_DIR), which in here is /app — the layer pip and COPY wrote, not a mount.
# Absolute container paths are what make the volumes below actually take effect.
#
# HOST defaults to 127.0.0.1, which inside a container is reachable by nothing.
ENV HOST=0.0.0.0 \
    PORT=8000 \
    LIBRARY_PATH=/music \
    DATA_PATH=/data \
    PYTHONUNBUFFERED=1

# ponytail: fixed uid rather than a PUID/PGID entrypoint. Both mounts must be
# writable by 1000 — Fonoteca downloads INTO /music, so it is not a read-only
# mount. Override with compose's `user:` if your host uid differs.
RUN mkdir -p /music /data && chown 1000:1000 /music /data
USER 1000:1000

EXPOSE 8000
# slim has no curl and this needs no more than stdlib. /health probes the DB too.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD \
    ["python", "-c", "import urllib.request,os;urllib.request.urlopen('http://127.0.0.1:'+os.environ['PORT']+'/health').read()"]

CMD ["python", "main.py"]
