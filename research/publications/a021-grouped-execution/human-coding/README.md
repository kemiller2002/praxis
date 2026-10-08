# WI-0075 independent human second coding

The human-coding protocol is frozen.

Files:
- `CODEBOOK.md`: category and dimension definitions.
- `INSTRUCTIONS.md`: coder independence and procedure.
- `coding-sheet.csv`: 32 blank coding units.
- `FREEZE.json`: immutable-material manifest.

Build the treatment-blind packet with:

```
python3 research/publications/a021-grouped-execution/scripts/build_human_coding_packet.py
```

The resulting `build/human-coding-packet.tar` is the only artifact that should
be given to the coder. It deliberately excludes the mapping, prior ratings,
evaluations, metrics, prompts and manuscript.

WI-0075 is **not complete** until an independent human who does not know the
arm mapping returns a frozen coding sheet. An AI agent cannot satisfy that
requirement.
