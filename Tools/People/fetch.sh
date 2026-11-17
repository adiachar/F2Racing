#!/bin/sh
# Fetch the source models (CC0, Quaternius) into $1 (default /tmp/people_src) with sparse, blob-less git clones.
#  * Universal Base Characters (Standard, free): Superhero_Male / Superhero_Female (.glb, from Dallolz/moorfall-assets;
#    the kit's licence file is checked from tomoponz/novel-game-prototype, which carries the original zip contents)
#  * Universal Animation Library: UAL1.glb, UAL2.glb (Dallolz/moorfall-assets, LICENSE.md: CC0 1.0, Quaternius)
# Then: python3 -I convert.py $OUT ../../Assets/Resources/People/people.bytes [preview.png]; verify.py renders the result.
set -e
OUT=${1:-/tmp/people_src}
mkdir -p "$OUT" && cd "$OUT"
[ -d moorfall ] || git clone -q --filter=blob:none --no-checkout --depth 1 https://github.com/Dallolz/moorfall-assets moorfall
git -C moorfall checkout -q HEAD -- LICENSE.md characters/Superhero_Male.glb characters/Superhero_Female.glb animations/UAL1.glb animations/UAL2.glb
[ -d ubc ] || git clone -q --filter=blob:none --no-checkout --depth 1 https://github.com/tomoponz/novel-game-prototype ubc
git -C ubc checkout -q HEAD -- "assets/models/Universal Base Characters[Standard]/License_Standard.txt"
cat moorfall/LICENSE.md "ubc/assets/models/Universal Base Characters[Standard]/License_Standard.txt" | grep -i "cc0"
