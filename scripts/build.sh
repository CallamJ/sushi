#!/bin/bash
set -euo pipefail # Exit on error

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

PROJECT_NAME="Sushi"
OUTPUT_DIR="${REPO_ROOT}/publish"
CONFIGURATION="Release"
# Color output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color
show_help() {
    echo "Usage: $0 <platform1> [platform2] ..."
    echo ""
    echo "Arguments:"
    echo "  <platforms>  Space-separated list of platforms to build"
    echo ""
    echo "Available platforms:"
    echo "  win-x64, win-x86, win-arm64"
    echo "  linux-x64, linux-arm64, linux-arm"
    echo "  osx-x64, osx-arm64"
    echo ""
    echo "Examples:"
    echo "  $0 win-x64                  # Build only Windows 64-bit"
    echo "  $0 win-x64 linux-x64        # Build Windows and Linux 64-bit"
    echo "  $0 osx-x64 osx-arm64        # Build both macOS versions"
    echo "  $0 win-x64 linux-x64 osx-arm64  # Build multiple platforms"
    exit "${1-0}"
}
# Check for help flag or no arguments
if [[ $# -eq 0 ]]; then
    echo -e "${RED}Error: No platforms specified${NC}"
    echo ""
    show_help 2
fi
if [[ "${1-}" == "-h" ]] || [[ "${1-}" == "--help" ]]; then
    show_help
fi
BUILD_PLATFORMS=("$@")
echo -e "${GREEN}  Building ${PROJECT_NAME}${NC}"
# Clean output directory
if [ -d "$OUTPUT_DIR" ]; then
    echo -e "${YELLOW}Cleaning output directory...${NC}"
    rm -rf "$OUTPUT_DIR"
fi
mkdir -p "$OUTPUT_DIR"

get_output_filename() {
    local rid=$1
    local platform=""
    local arch=""
    local bits=""
    local extension=""

    case "$rid" in
    win-x64)
        platform="win"
        arch="x64"
        bits="64"
        extension=".exe"
        ;;
    win-x86)
        platform="win"
        arch="x86"
        bits="32"
        extension=".exe"
        ;;
    win-arm64)
        platform="win"
        arch="arm"
        bits="64"
        extension=".exe"
        ;;
    linux-x64)
        platform="linux"
        arch="x64"
        bits="64"
        ;;
    linux-arm64)
        platform="linux"
        arch="arm"
        bits="64"
        ;;
    linux-arm)
        platform="linux"
        arch="arm"
        bits="32"
        ;;
    osx-x64)
        platform="osx"
        arch="x64"
        bits="64"
        ;;
    osx-arm64)
        platform="osx"
        arch="arm"
        bits="64"
        ;;
    *)
        echo "Unsupported runtime identifier: $rid" >&2
        return 1
        ;;
    esac

    echo "${PROJECT_NAME}-${platform}_${arch}-${bits}${extension}"
}

build_platform() {
    local rid=$1
    local description=$2
    local temp_dir="$OUTPUT_DIR/temp_$rid"

    echo ""
    echo -e "${YELLOW}Building for $description ($rid)...${NC}"

    if ! dotnet publish "./src/${PROJECT_NAME}/${PROJECT_NAME}.csproj" \
        -c "$CONFIGURATION" \
        -r "$rid" \
        --self-contained \
        -o "$temp_dir" \
        -p:UseAppHost=true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=true \
        -p:TrimMode=CopyUsed \
        -p:PublishReadyToRun=true; then
        echo -e "${RED}✗ Build failed${NC}"
        rm -rf "$temp_dir"
        return 1
    fi

    # Get source and destination filenames
    if [[ "$rid" == win-* ]]; then
        src_file="$temp_dir/${PROJECT_NAME}.exe"
    else
        src_file="$temp_dir/${PROJECT_NAME}"
    fi

    dest_file="$OUTPUT_DIR/$(get_output_filename "$rid")"

    if [[ ! -f "$src_file" ]]; then
        echo -e "${RED}✗ Build completed but executable not found: $src_file${NC}"
        rm -rf "$temp_dir"
        return 1
    fi

    mv "$src_file" "$dest_file"
    rm -rf "$temp_dir"

    size=$(du -h "$dest_file" | cut -f1)
    echo -e "${GREEN}✓ Built successfully ($size) -> $(basename "$dest_file")${NC}"
}

get_platform_description() {
    local rid=$1
    case "$rid" in
    win-x64) echo "Windows (64-bit)" ;;
    win-x86) echo "Windows (32-bit)" ;;
    win-arm64) echo "Windows ARM64" ;;
    linux-x64) echo "Linux (64-bit)" ;;
    linux-arm64) echo "Linux ARM64" ;;
    linux-arm) echo "Linux ARM" ;;
    osx-x64) echo "macOS Intel" ;;
    osx-arm64) echo "macOS Apple Silicon" ;;
    *)
        echo "Unsupported runtime identifier: $rid" >&2
        return 1
        ;;
    esac
}
# Build selected platforms
echo -e "${BLUE}Building platforms: ${BUILD_PLATFORMS[*]}${NC}"
for rid in "${BUILD_PLATFORMS[@]}"; do
    description=$(get_platform_description "$rid")
    build_platform "$rid" "$description"
done
echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  Build Complete!${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo "Executables are located in:"
echo ""
# Show output files
ls -lh "$OUTPUT_DIR"/${PROJECT_NAME}-* 2>/dev/null || echo "No executables found"
