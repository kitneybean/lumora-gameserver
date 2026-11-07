#!/usr/bin/env bash
# Installs a NeoForge dedicated server wired to Lumora Auth via authlib-injector.
# Usage: ./install.sh <neoforge-version> <auth-url> [server-dir] [memory]
#   ./install.sh 21.1.77 https://auth.example.com
# Set ACCEPT_EULA=1 only if you have read and accept the Minecraft EULA: https://aka.ms/MinecraftEULA
set -euo pipefail

NEOFORGE_VERSION="${1:?neoforge version, e.g. 21.1.77}"
AUTH_URL="${2:?auth url, e.g. https://auth.example.com}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SERVER_DIR="${3:-$SCRIPT_DIR/server}"
MEMORY="${4:-4G}"
BASE="${AUTH_URL%/}"

mkdir -p "$SERVER_DIR"
cd "$SERVER_DIR"

INSTALLER="neoforge-$NEOFORGE_VERSION-installer.jar"
curl -fL -o "$INSTALLER" "https://maven.neoforged.net/releases/net/neoforged/neoforge/$NEOFORGE_VERSION/$INSTALLER"
java -jar "$INSTALLER" --installServer
rm -f "$INSTALLER" "$INSTALLER.log"

# authlib-injector: prefer the copy served by Lumora Auth, fall back to the official mirror.
if ! curl -fL -o authlib-injector.jar "$BASE/api/launcher/authlib-injector/download"; then
  URL="$(curl -fsL https://authlib-injector.yushi.moe/artifact/latest.json | sed -n 's/.*"download_url": *"\([^"]*\)".*/\1/p')"
  curl -fL -o authlib-injector.jar "$URL"
fi

cat > user_jvm_args.txt <<EOF
-Xms1G
-Xmx$MEMORY
-javaagent:authlib-injector.jar=$BASE/api/yggdrasil
EOF

[ -f server.properties ] || cp "$SCRIPT_DIR/config/server.properties" server.properties
if [ "${ACCEPT_EULA:-0}" = "1" ]; then
  echo "eula=true" > eula.txt
elif [ ! -f eula.txt ]; then
  echo "eula=false" > eula.txt
  echo "Read the Minecraft EULA (https://aka.ms/MinecraftEULA) and set eula=true in $SERVER_DIR/eula.txt, or rerun with ACCEPT_EULA=1."
fi
echo "Done. Start the server with: $SERVER_DIR/run.sh"
