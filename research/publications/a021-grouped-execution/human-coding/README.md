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


## Signal survey path

The preferred administration path is now the Signal acceptance fixture defined
in `kemiller2002/signal` PR #26:

- survey id: `A021-ARCH-CODE-V1`;
- 4 blind-coder eligibility items;
- 32 architecture category judgments;
- 32 confidence judgments;
- 2 final validity/contamination items;
- anonymous, closed-ended, no PII, no treatment mapping.

Signal owns categorical response collection and anonymous finalization. The
companion code-citation/rationale worksheet remains in this publication package
and joins by study/arm/dimension.

Do **not** administer WI-0075 through Signal until Signal can publish the fixture
canonically, its 70-item browser flow and raw categorical export pass, and the
exact published template hash is added to this freeze before any human coder
starts. Until then, `build_human_coding_packet.py` plus
`coding-sheet.csv` remains the valid fallback.
