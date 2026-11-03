#!/bin/sh
# Installs Dormant as a systemd service. Run as root:
#
#   sudo deploy/systemd/install.sh path/to/dormant
#
# - copies the executable to /usr/local/bin/dormant
# - creates the system user 'dormant' (no login, no home)
# - writes /etc/dormant/dormant.yaml from the example if there is none, with the store and reports under /var/lib/dormant
# - creates /etc/dormant/dormant.env (0600, root) for connection strings
# - installs and enables the unit; it does not start it until you have configured it
#
# Running it again upgrades the executable and the unit, and leaves the configuration and history alone.
set -eu

binary="${1:-}"
here="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this as root (sudo)." >&2
    exit 1
fi
if [ -z "$binary" ] || [ ! -f "$binary" ]; then
    echo "Usage: $0 path/to/dormant (the linux-x64 or linux-arm64 build)" >&2
    exit 1
fi

if ! id dormant >/dev/null 2>&1; then
    useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin dormant
    echo "Created the system user dormant."
fi

install -m 0755 "$binary" /usr/local/bin/dormant
echo "Installed /usr/local/bin/dormant ($(/usr/local/bin/dormant version | head -n 1))."

install -d -m 0750 -o root -g dormant /etc/dormant
if [ ! -f /etc/dormant/dormant.yaml ]; then
    /usr/local/bin/dormant init -o /etc/dormant/dormant.yaml >/dev/null
    sed -i \
        -e 's|^  path: dormant.db$|  path: /var/lib/dormant/dormant.db|' \
        -e 's|^  out_dir: reports$|  out_dir: /var/lib/dormant/reports|' \
        /etc/dormant/dormant.yaml
    grep -q '^  path: /var/lib/dormant/dormant.db$' /etc/dormant/dormant.yaml
    grep -q '^  out_dir: /var/lib/dormant/reports$' /etc/dormant/dormant.yaml
    chown root:dormant /etc/dormant/dormant.yaml
    chmod 0640 /etc/dormant/dormant.yaml
    echo "Wrote an example configuration to /etc/dormant/dormant.yaml."
fi
if [ ! -f /etc/dormant/dormant.env ]; then
    install -m 0600 -o root -g root /dev/null /etc/dormant/dormant.env
    echo "# Connection strings for the configuration's env: references, one NAME=value per line." > /etc/dormant/dormant.env
    echo "Created /etc/dormant/dormant.env (root only) for connection strings."
fi

install -m 0644 "$here/dormant.service" /etc/systemd/system/dormant.service
systemctl daemon-reload
systemctl enable dormant.service >/dev/null 2>&1
echo "Installed and enabled dormant.service."
echo
echo "Next:"
echo "  1. Edit /etc/dormant/dormant.yaml (instances) and /etc/dormant/dormant.env (connection strings)."
echo "  2. Check them:  sudo sh -c 'set -a; . /etc/dormant/dormant.env; dormant validate -c /etc/dormant/dormant.yaml'"
echo "  3. Start:       systemctl start dormant"
echo "  4. Follow:      journalctl -u dormant -f"
