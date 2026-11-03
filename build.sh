#!/bin/sh
# Beast Build Script (macOS / Linux)
# Builds beast as a self-contained single-file executable for this machine's OS and CPU, and the Agent as
# a Docker image (beastagent) for the Docker VM's CPU. build.bat is the Windows equivalent.
set -e
cd "$(dirname "$0")"

echo "============================================"
echo "  Beast CLI Build Script"
echo "============================================"
echo

if ! command -v docker >/dev/null 2>&1; then
	echo "ERROR: Docker not found. Please install Docker."
	exit 1
fi

echo "Docker version:"
docker --version
echo

# Runtime identifier for this machine.
case "$(uname -s)" in
	Darwin) os=osx ;;
	Linux)  os=linux ;;
	*) echo "ERROR: Unsupported OS: $(uname -s)"; exit 1 ;;
esac
case "$(uname -m)" in
	x86_64|amd64)  arch=x64 ;;
	arm64|aarch64) arch=arm64 ;;
	*) echo "ERROR: Unsupported CPU architecture: $(uname -m)"; exit 1 ;;
esac

# A shell running under Rosetta reports x86_64 on Apple Silicon; the hardware flag tells the truth.
if [ "$os" = osx ] && [ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = 1 ]; then
	arch=arm64
fi
rid="$os-$arch"

# Publish beast inside the .NET SDK container; Beast/publish.sh does the work. NuGet packages are cached in
# a named Docker volume rather than ~/.nuget, which the container's root user would otherwise take over on Linux.
echo "Building beast for $rid inside Docker (mcr.microsoft.com/dotnet/sdk:10.0)..."
docker run --rm -v "$PWD:/src" -v beast-nuget:/root/.nuget/packages mcr.microsoft.com/dotnet/sdk:10.0 sh /src/Beast/publish.sh "$rid"
echo

# Install to ~/.local/bin, where Claude Code's installer also puts claude, so it is usually on the PATH
# already. Copied beside and renamed over the old one, so a running beast keeps its own file.
install_dir="$HOME/.local/bin"
mkdir -p "$install_dir"
cp bin/beast "$install_dir/beast.new"

# The SDK can only sign a macOS executable when it builds on a Mac; Apple Silicon kills an unsigned one, so
# it gets an ad-hoc signature here.
if [ "$os" = osx ]; then
	codesign --force --sign - "$install_dir/beast.new"
fi
mv -f "$install_dir/beast.new" "$install_dir/beast"
echo "Installed $install_dir/beast"
case ":$PATH:" in
	*":$install_dir:"*) ;;
	*) echo "NOTE: $install_dir is not on your PATH. Add it in your shell profile, e.g.:"
	   echo "      echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.zshrc" ;;
esac
echo

# Build Docker image (contains Agent at /app/Agent as entrypoint). docker build targets the Docker VM's own
# CPU, so the image is amd64 or arm64 to match this machine.
echo "============================================"
echo "Building Docker image: beastagent..."
echo "============================================"
echo
docker build -t beastagent:latest -f Dockerfile .
echo

echo "============================================"
echo "Build Complete!"
echo "============================================"
echo
echo "Outputs:"
echo "  $install_dir/beast  ($rid, also in $PWD/bin)"
echo "  Docker image: beastagent:latest  (entrypoint: /app/Agent)"
echo "============================================"
