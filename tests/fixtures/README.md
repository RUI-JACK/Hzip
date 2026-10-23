# Archive test fixtures

RAR and RAR5 fixtures are decoded from libarchive's public test corpus. See provenance.json for each exact source URL, byte length and SHA-256, and LIBARCHIVE-COPYING.txt for the upstream notice.

The RAR4 fixture deliberately contains a symbolic link. Hzip's default full extraction rejects it; the selected ordinary file is extracted in a separate test and validated by its expected CRC. The RAR5 fixture contains a compressed 1200-byte member validated against CRC 7CCA70CD.

These are development fixtures, not application assets. They are never automatically executed.
