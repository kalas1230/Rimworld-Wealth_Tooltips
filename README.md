# Wealth Readout

A RimWorld 1.6 mod. Hover a category or item in the top-left resource readout to see what it
contributes to colony wealth, in silver and as a share of the total.

- Design: `docs/superpowers/specs/2026-09-01-wealth-readout-design.md`
- Why it is shaped this way, and what was already rejected: `HANDOVER.md`

## Building

```
dotnet build Source/WealthReadout.csproj
```

Override the install path if yours differs:

```
dotnet build Source/WealthReadout.csproj -p:RimWorldDir="D:\Steam\steamapps\common\RimWorld"
```

Output lands in `Assemblies/`.
