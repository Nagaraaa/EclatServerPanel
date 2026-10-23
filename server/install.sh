#!/bin/bash
set -euo pipefail
umask 077

if [[ $EUID -ne 0 ]]; then
  echo 'Run this installer as root.' >&2
  exit 1
fi
if [[ $# -ne 1 || ! -f "$1" ]]; then
  echo 'Usage: bash server/install.sh /absolute/path/to/panel-key.pub' >&2
  exit 1
fi
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
key_file=$(realpath -- "$1")
python3 - "$key_file" <<'PY'
import base64, pathlib, sys
lines = pathlib.Path(sys.argv[1]).read_text().strip().splitlines()
if len(lines) != 1:
    raise SystemExit('Exactly one SSH public key is required')
parts = lines[0].split()
if len(parts) < 2 or parts[0] not in {'ssh-ed25519', 'ssh-rsa', 'ecdsa-sha2-nistp256'}:
    raise SystemExit('Unsupported or invalid SSH public key')
base64.b64decode(parts[1], validate=True)
PY

apt-get update
apt-get install -y python3 sudo openssh-server sqlite3
getent passwd eclat-server >/dev/null || useradd --system --home-dir /var/lib/eclat-originel --shell /usr/sbin/nologin eclat-server
getent passwd eclat-panel >/dev/null || useradd --system --create-home --home-dir /var/lib/eclat-panel --shell /bin/sh eclat-panel
install -d -o root -g root -m 755 /usr/local/lib/eclat-panel
install -m 755 -o root -g root "$script_dir/eclat-admin.py" /usr/local/sbin/eclat-admin
install -m 755 -o root -g root "$script_dir/prepare-start.py" /usr/local/lib/eclat-panel/prepare-start.py
if [[ ! -e /etc/eclat-panel.json ]]; then
  install -m 600 -o root -g root "$script_dir/eclat-panel.json.example" /etc/eclat-panel.json
fi
install -d -o eclat-server -g eclat-server -m 700 /var/lib/eclat-originel
install -d -o root -g root -m 700 /var/backups/eclat-originel
# The admin account cannot edit its own forced-command key or SSH options.
chown root:root /var/lib/eclat-panel
chmod 755 /var/lib/eclat-panel
install -d -o root -g root -m 755 /var/lib/eclat-panel/.ssh
read -r key_type key_data remainder < "$key_file"
printf 'restrict,command="sudo -n /usr/local/sbin/eclat-admin" %s %s\n' "$key_type" "$key_data" > /var/lib/eclat-panel/.ssh/authorized_keys
chown root:root /var/lib/eclat-panel/.ssh/authorized_keys
chmod 644 /var/lib/eclat-panel/.ssh/authorized_keys
printf '%s\n' 'Defaults:eclat-panel env_reset' 'Defaults:eclat-panel !setenv' 'eclat-panel ALL=(root) NOPASSWD: /usr/local/sbin/eclat-admin ""' > /etc/sudoers.d/eclat-panel
chmod 440 /etc/sudoers.d/eclat-panel
visudo -cf /etc/sudoers.d/eclat-panel
for unit in eclat-server.service eclat-watchdog.service eclat-watchdog.timer eclat-backup.service eclat-backup.timer; do
  install -m 644 -o root -g root "$script_dir/$unit" "/etc/systemd/system/$unit"
done
systemctl daemon-reload
systemctl enable eclat-server.service eclat-watchdog.timer eclat-backup.timer
systemctl start eclat-watchdog.timer eclat-backup.timer
echo 'Panel installed. The game was not started or stopped. Configure and validate the game before starting eclat-server.'
