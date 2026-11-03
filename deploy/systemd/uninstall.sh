#!/bin/sh
# Stops and removes the Dormant systemd service and executable. Run as root.
# The configuration (/etc/dormant), history and reports (/var/lib/dormant) and the 'dormant' user are kept;
# remove them yourself once you no longer need the history.
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Run this as root (sudo)." >&2
    exit 1
fi

systemctl disable --now dormant.service >/dev/null 2>&1 || true
rm -f /etc/systemd/system/dormant.service
systemctl daemon-reload
rm -f /usr/local/bin/dormant
echo "Removed dormant.service and /usr/local/bin/dormant. Kept /etc/dormant and /var/lib/dormant."
