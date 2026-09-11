# Generation comparison examples

Each example has a Sushi source file and hand-written Bash, Zsh, and PowerShell
equivalents. The `generated/` directory contains output produced by the Sushi
transpiler for direct comparison with the native versions.

The examples intentionally cover three different code-generation shapes:

- `arithmetic`: scalar arithmetic and a typed function
- `strings`: a chained string transformation
- `collections`: native array and object access

## Generated size comparison

Line counts include shebangs and target setup:

| Example | Native Bash | Generated Bash | Native Zsh | Generated Zsh | Native PowerShell | Generated PowerShell |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| arithmetic | 9 | 11 | 9 | 12 | 7 | 8 |
| strings | 7 | 8 | 7 | 9 | 4 | 4 |
| collections | 6 | 5 | 7 | 7 | 5 | 5 |

All native and generated variants produce matching output. Generated scripts
contain no embedded Sushi helper functions; the few additional arithmetic and
string lines are target-required result and transformation steps.
