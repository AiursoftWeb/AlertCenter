#!/bin/sh
set -e
mkdir -p /var/lib/aiursoft-alertCenter
chown www-data:www-data /var/lib/aiursoft-alertCenter

# Bootstrap config if missing (matching Docker entrypoint pattern)
if [ ! -f /etc/aiursoft-alertCenter/appsettings.json ]; then
    cp /usr/share/aiursoft-alertCenter/appsettings.json /etc/aiursoft-alertCenter/appsettings.json
fi
# Replace /usr/share copy with symlink to /etc
rm -f /usr/share/aiursoft-alertCenter/appsettings.json
ln -sf /etc/aiursoft-alertCenter/appsettings.json /usr/share/aiursoft-alertCenter/appsettings.json
