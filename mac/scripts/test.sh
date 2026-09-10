#!/bin/bash
# Runs the Swift test suite.
#
# `swift test` on its own is enough when Xcode is installed. It is not enough
# with only the Command Line Tools, which is what this project builds with: the
# CLT ship swift-testing, but they put Testing.framework and its interop dylib
# somewhere the compiler and the loader are not told about, so a plain
# `swift test` fails first to compile `import Testing` and then to dlopen the
# bundle. Both are a matter of four search paths, added here and nowhere else so
# that a machine with Xcode is unaffected.
set -euo pipefail

cd "$(dirname "$0")/.."

FRAMEWORKS="$(xcode-select -p)/Library/Developer/Frameworks"
LIBS="$(xcode-select -p)/Library/Developer/usr/lib"

if [ -d "$FRAMEWORKS" ]; then
    exec swift test \
        -Xswiftc -F -Xswiftc "$FRAMEWORKS" \
        -Xlinker -F -Xlinker "$FRAMEWORKS" \
        -Xlinker -rpath -Xlinker "$FRAMEWORKS" \
        -Xlinker -rpath -Xlinker "$LIBS" \
        "$@"
fi

exec swift test "$@"
