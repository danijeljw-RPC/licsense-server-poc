# Contributing

## Bumping Licensing.Core's version

Since you're only bumping `Licensing.Core`'s version manually, about once a year: each time you
do, remember to also run `dotnet restore SoftwareLicensing.slnx --force-evaluate` (or
delete+regenerate the lock files) and commit the updated `packages.lock.json` files in the same
commit — otherwise you'll hit this exact CI failure again: CI restores with `--locked-mode`, and a
stale lock file still pinned to the old `Licensing.Core` version will fail locked restore before
the build even starts.
