#!/bin/sh
# Publishes beast as a self-contained single-file executable for each runtime identifier given
# (win-x64, osx-arm64, linux-x64, ...). Runs inside the mcr.microsoft.com/dotnet/sdk:10.0 container with
# the repo bind-mounted at /src; the host build scripts only pick the identifiers and start the container.
# The build runs on a private copy of the source, so the host's build/obj folders are never touched and a
# native Linux host is not left with root-owned files. Output: /src/bin/beast.exe (Windows) or
# /src/bin/beast, so bin/ is the one folder to put on the PATH. Not beside the scripts: on macOS's
# case-insensitive filesystem that path is the Beast/ source folder. With several identifiers, each
# overwrites the last of the same name.
set -e

if [ $# -eq 0 ]; then
	echo "usage: publish.sh <rid> [<rid> ...]"
	exit 1
fi

# Source only: no git history, worktrees, sessions, or previous build output.
rm -rf /tmp/src
mkdir -p /tmp/src
tar -C /src --exclude=./.git --exclude=./.beast --exclude=./.vs --exclude=./build --exclude=./bin -cf - . | tar -C /tmp/src -xf -

# Published files are handed to whoever owns the repo on the host (a no-op under Docker Desktop).
owner=$(stat -c '%u:%g' /src)

for rid in "$@"; do
	echo "Publishing beast for $rid..."
	dotnet publish /tmp/src/Beast/Beast.csproj -c Release -r "$rid" --self-contained -o "/tmp/publish/$rid"

	# Windows executables keep their extension; every other platform gets a bare "beast".
	case "$rid" in
		win-*) exe=beast.exe ;;
		*)     exe=beast     ;;
	esac

	mkdir -p /src/bin
	chown "$owner" /src/bin 2>/dev/null || true
	cp "/tmp/publish/$rid/$exe" "/src/bin/$exe"
	chmod +x "/src/bin/$exe"
	chown "$owner" "/src/bin/$exe" 2>/dev/null || true
	echo "  -> bin/$exe ($rid)"
done
