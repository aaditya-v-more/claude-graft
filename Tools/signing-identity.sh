#!/bin/zsh
# Prints the certificate hash a local build is signed with, or "-" for ad-hoc.
# Never used for a release: release.sh always hands build.sh its own identity.
#
# Claude's "Claude Safe Storage" item trusts an ad-hoc app by its code hash, so
# every development build is a stranger to it and puts the keychain dialog up
# again, and "Always Allow" only adds one more hash to a list the next build
# never matches. Signed with an Apple Development certificate, the keychain
# trusts a requirement naming the certificate instead, which every later build
# signed by it meets, so one "Always Allow" lasts. The hash rather than the
# name, because a renewed certificate sits beside the old one under the same
# name until that expires, and codesign refuses an ambiguous name.
hash="$(security find-identity -v -p codesigning 2>/dev/null \
    | awk 'index($0, "\"Apple Development:") { print $2; exit }')"
print -r -- "${hash:--}"
